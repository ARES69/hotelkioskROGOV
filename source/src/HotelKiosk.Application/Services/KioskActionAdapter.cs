using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Application.Services
{
    /// <summary>Ошибка действия с кодом из каталога ошибок MONE.</summary>
    public class KioskActionException : Exception
    {
        public string Code { get; }
        public KioskActionException(string code, string message = null) : base(message ?? code)
            => Code = code;
    }

    /// <summary>
    /// Адаптер протокола MONE (13+3 действия, конверт request/response v1)
    /// на существующие сервисы киоска.
    ///
    /// Контракт фронтенда: запрос {type:"request",version:1,action,requestId,
    /// operationId,sessionId,epoch,bridgeNonce,payload}; ответ обязан эхо-нуть
    /// все корреляционные поля и доставляется вызовом window.KioskBridge.receive.
    /// Смена sessionId|epoch = новый гость: внутреннее состояние сбрасывается.
    /// </summary>
    public class KioskActionAdapter
    {
        private readonly IRegulaService _regula;
        private readonly IPmsProvider _pms;
        private readonly IVisionLineService _visionLine;
        private readonly IAlfaBankService _alfaBank;
        private readonly ICardTerminalService _cardTerminal;
        private readonly INotificationService _notifications;
        private readonly ICheckInSessionStore _sessionStore;
        private readonly ILogger<KioskActionAdapter> _logger;
        private readonly CaptureSource _captureSource;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        // === Состояние сессии гостя ===
        private string _sessionKey = "";
        private bool _found;
        private string _bookingId = "", _bookingGuid = "", _room = "";
        private DateTime _arrival, _departure;
        private decimal _price;
        private int _maxGuests = 1;

        private readonly Dictionary<string, string> _docKeys = new();     // purpose → documentKey
        private readonly Dictionary<string, PassportData> _passports = new();
        private readonly List<string> _guestIds = new();

        private string _checkinId = "";
        private string _payTxId = "";
        private string _payMethod = "qr";

        /// <summary>
        /// QR текущего заказа. Нужен, чтобы повторный payment.create не создавал
        /// второй заказ: гость увидит тот же QR, а не два конкурирующих.
        /// </summary>
        private string _qrPayload;
        private string _qrImage;
        private DateTime? _qrExpiresAt;

        /// <summary>
        /// Сумма, фактически подтверждённая владельцем платежа. Используется при
        /// возврате: вернуть нужно столько, сколько списали, а не сколько стоит
        /// бронь, если банк сообщил другую сумму.
        /// </summary>
        private decimal? _paidAmount;

        /// <summary>Возврат по текущему платежу уже выполнен — повторно не возвращаем.</summary>
        private bool _payRefunded;

        /// <summary>
        /// Деньги фактически списаны (а не просто создан QR-заказ).
        /// Ставится из payment.status/payment.create при подтверждении —
        /// от этого зависит, помечать ли чекпоинт как «требуется возврат».
        /// </summary>
        private bool _payConfirmed;

        public KioskActionAdapter(
            IRegulaService regula, IPmsProvider pms, IVisionLineService visionLine,
            IAlfaBankService alfaBank, ICardTerminalService cardTerminal,
            INotificationService notifications, ICheckInSessionStore sessionStore,
            ILogger<KioskActionAdapter> logger, CaptureSource captureSource = null)
        {
            _regula = regula; _pms = pms; _visionLine = visionLine;
            _alfaBank = alfaBank; _cardTerminal = cardTerminal;
            _notifications = notifications; _sessionStore = sessionStore; _logger = logger;
            // null в тестах и при ручном создании — камера с настройками по умолчанию
            _captureSource = captureSource ?? CaptureSource.Default;
        }

        public async Task<object> ExecuteAsync(
            string action, JsonElement payload,
            string sessionId, long epoch, CancellationToken ct)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                var key = $"{sessionId}|{epoch}";
                if (!string.Equals(key, _sessionKey, StringComparison.Ordinal))
                {
                    _logger.LogInformation("MONE: новая сессия {Key}", key);
                    ResetSession();
                    _sessionKey = key;
                }

                return action switch
                {
                    "system.preflight" => await PreflightAsync(ct),
                    "booking.search" => await BookingSearchAsync(payload, ct),
                    "booking.arrivals" => await ArrivalsAsync(payload, ct),
                    "scanner.scan" => await ScannerScanAsync(payload, ct),
                    "pms.guest.upsert" => await GuestUpsertAsync(payload, ct),
                    "pms.checkin" => await CheckInAsync(payload, ct),
                    "vision.prepare" => await VisionPrepareAsync(payload, ct),
                    "card.detect" => CardDetect(payload),
                    "card.read" => CardRead(payload),
                    "card.write" => await CardWriteAsync(payload, ct),
                    "card.verify" => CardVerify(payload),
                    "card.awaitRemoval" => CardAwaitRemoval(payload),
                    "pms.finalize" => await FinalizeAsync(payload, ct),
                    "recovery.inspect" => await RecoveryInspectAsync(ct),
                    "payment.create" => await PaymentCreateAsync(payload, ct),
                    "payment.status" => await PaymentStatusAsync(payload, ct),
                    "payment.cancel" => await PaymentCancelAsync(ct),
                    _ => throw new KioskActionException("API_VALIDATION", $"unknown action {action}")
                };
            }
            finally { _mutex.Release(); }
        }

        private void ResetSession()
        {
            _found = false;
            _bookingId = _bookingGuid = _room = "";
            _arrival = _departure = default;
            _price = 0; _maxGuests = 1;
            _docKeys.Clear(); _passports.Clear(); _guestIds.Clear();
            _checkinId = ""; _payTxId = ""; _payMethod = "qr"; _payRefunded = false; _payConfirmed = false;
            _qrPayload = null; _qrImage = null; _qrExpiresAt = null; _paidAmount = null;
        }

        // ==================== preflight / recovery ====================

        /// <summary>
        /// Готовность киоска к работе. Проверяется до того, как гость встанет
        /// в рамку: если камера не настроена, об этом должен узнать персонал,
        /// а не гость после ожидания.
        ///
        /// Саму камеру с бэкенда проверить нельзя — она в браузере. Поэтому
        /// devices.camera означает «источник кадра настроен», а фактическую
        /// доступность UI проверяет при открытии экрана сканирования.
        /// </summary>
        private async Task<object> PreflightAsync(CancellationToken ct)
        {
            var logus = await SafePing(() => _pms.PingAsync(ct));
            var regula = await SafePing(() => _regula.PingAsync(ct));
            var vision = await SafePing(() => _visionLine.PingAsync(ct));

            var scannerReady = _captureSource.IsConfigured;
            var pms = _pms.Capabilities;

            var devices = new Dictionary<string, bool>
            {
                ["logus"] = logus,
                ["vision"] = vision,
                ["crt"] = vision,      // энкодер за тем же мостом VisionLine
                ["regula"] = regula,
                ["camera"] = _captureSource.IsCamera,
                ["scanner"] = scannerReady,
                ["pms"] = logus
            };

            return new
            {
                // Заселение невозможно без поиска брони и самого заселения —
                // это минимальный контракт IPmsProvider
                ready = logus && regula && vision && scannerReady && pms.CanCheckInAtAll,
                devices,
                captureSource = _captureSource.Kind,
                pms = new
                {
                    vendor = pms.Vendor,
                    // Что PMS не умеет: интерфейс не будет предлагать
                    // недоступные действия, а персонал увидит это при запуске
                    missing = pms.MissingOptional
                }
            };
        }

        private static async Task<bool> SafePing(Func<Task<bool>> ping)
        {
            try { return await ping(); } catch { return false; }
        }

        private async Task<object> RecoveryInspectAsync(CancellationToken ct)
        {
            var leftover = await _sessionStore.LoadAsync(ct);
            if (leftover == null)
                return new { status = "not_found" };

            // Незавершённая сессия прошлого запуска: блокируем и зовём персонал.
            // Отдельно выделяем случай «оплачено, а заселение не завершено» —
            // это единственное состояние, где гостю могут не вернуть деньги.
            var needsRefund = leftover.RequiresCompensation &&
                              !string.IsNullOrEmpty(leftover.PaymentTransactionId);

            _logger.LogWarning(
                "MONE: чекпоинт прошлой сессии (бронь {Res}, оплата {Pay}, возврат нужен: {Refund})",
                leftover.ReservationId, leftover.PaymentTransactionId, needsRefund);

            var message = needsRefund
                ? $"🛑 Киоск: оплата подтверждена, но заселение не завершено " +
                  $"(бронь {leftover.ReservationId ?? "—"}, сумма {leftover.PaidAmount:0.##} руб., " +
                  $"транзакция {leftover.PaymentTransactionId}). Требуется ручной возврат " +
                  $"или завершение заселения. Шаг: {leftover.CurrentState}." +
                  (string.IsNullOrEmpty(leftover.LastError) ? "" : $" Ошибка: {leftover.LastError}.")
                : $"⚠️ Киоск: незавершённая сессия (бронь {leftover.ReservationId ?? "—"}, " +
                  $"платёж {leftover.PaymentTransactionId ?? "—"}). Требуется разбор администратора.";

            _ = _notifications.NotifyStaffAsync(message, ct);

            return new
            {
                status = needsRefund ? "refund_required" : "conflict",
                reservationId = leftover.ReservationId,
                amount = leftover.PaidAmount,
                paymentTransactionId = leftover.PaymentTransactionId,
                state = leftover.CurrentState.ToString(),
                lastError = leftover.LastError
            };
        }

        // ==================== booking ====================

        private async Task<object> BookingSearchAsync(JsonElement p, CancellationToken ct)
        {
            var code = Str(p, "code");
            var surname = Str(p, "surname");

            var r = await _pms.AuthenticateSelfCheckinAsync(code, SelfCheckinConfirmationMode.LastName, surname, ct);
            if (!r.IsSuccess || r.Value?.Reservation == null)
                throw new KioskActionException("BOOKING_NOT_FOUND");

            var res = r.Value.Reservation;
            _found = true;
            _bookingId = res.Id ?? code ?? "";
            _room = res.RoomCode ?? "";
            _arrival = res.ArrivalDate;
            _departure = res.DepartureDate;
            _price = res.Price;
            _maxGuests = Math.Clamp(res.GuestsCount == 0 ? 1 : res.GuestsCount, 1, 2);

            // Guid брони нужен позже для UpdateGuests — тянем сразу
            var det = await _pms.GetReservationAsync(_bookingId, ct);
            _bookingGuid = det.IsSuccess ? det.Value?.Guid ?? "" : "";

            return new
            {
                found = true,
                booking = new Dictionary<string, object?>
                {
                    ["id"] = _bookingId,
                    ["code"] = code,
                    ["room"] = _room,
                    ["categoryRu"] = r.Value.RoomsInfo?.RoomTypes?.Find(t => t.Code == res.RoomTypeCode)?.Name,
                    ["categoryEn"] = null,
                    ["arrival"] = _arrival.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["departure"] = _departure.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["total"] = _price,
                    ["dueAmount"] = _price,
                    ["status"] = "RES",
                    ["roomReady"] = !string.IsNullOrEmpty(_room),
                    ["breakfast"] = false,
                    ["maxGuests"] = _maxGuests
                }
            };
        }

        /// <summary>
        /// Заезды на дату — экран «сегодня заезжают».
        ///
        /// Гость приходит без кода брони (забыл, потерял) и ищ��т себя
        /// по фамилии в списке заездов. Нужно не всем: PMS без такой
        /// операции возвращает поддержку false, и UI прячет экран.
        /// </summary>
        private async Task<object> ArrivalsAsync(JsonElement p, CancellationToken ct)
        {
            if (!_pms.Capabilities.CanListArrivals)
                throw new KioskActionException("PMS_OFFLINE", "PMS не поддерживает список заездов");
            var dateStr = Str(p, "date");
            var date = DateTime.Today;

            if (!string.IsNullOrWhiteSpace(dateStr) &&
                !DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out date))
            {
                throw new KioskActionException("BOOKING_DATES_INVALID", "Некорректная дата");
            }

            var r = await _pms.GetArrivalsAsync(date, ct);
            if (!r.IsSuccess)
                throw new KioskActionException("PMS_OFFLINE", r.Error);

            _logger.LogInformation("MONE: заезды на {Date}: {Count}", date, r.Value?.Count ?? 0);

            return new
            {
                date = date.ToString("yyyy-MM-dd"),
                count = r.Value?.Count ?? 0,
                arrivals = Array.ConvertAll((r.Value ?? Array.Empty<ArrivalInfo>()).ToArray(), a => new
                {
                    genericNo = a.GenericNo,
                    lastName = a.LastName,
                    firstName = a.FirstName,
                    arrivalDate = a.ArrivalDate.ToString("yyyy-MM-dd"),
                    departureDate = a.DepartureDate.ToString("yyyy-MM-dd"),
                    stayAmount = a.StayAmount,
                    balance = a.Balance
                })
            };
        }

        // ==================== scanner ====================

        private async Task<object> ScannerScanAsync(JsonElement p, CancellationToken ct)
        {
            var sequence = Int(p, "sequence");
            var pageType = Str(p, "pageType") ?? "";
            var purpose = Str(p, "purpose") ?? "primary";

            // Кадр с камеры приходит в payload.image (JPEG в base64). Если его нет —
            // гость на считывателе, и кадр берёт агент Regula:AgentScanPath.
            var photo = Str(p, "image");

            Result<PassportData> scan = string.IsNullOrWhiteSpace(photo)
                ? await _regula.ScanPassportAsync(ct)
                : await _regula.RecognizeFromPhotoAsync(photo, ct);

            if (!scan.IsSuccess || scan.Value == null)
                throw new KioskActionException("OCR_FIELD_MISSING", scan.Error);

            var passport = scan.Value;
            var docKey = $"{purpose}:{passport.PassportNumber}";
            if (_docKeys.TryGetValue(purpose, out var prev))
            {
                if (prev != docKey)
                    throw new KioskActionException("OCR_DATA_CONFLICT");
            }
            else
            {
                foreach (var kv in _docKeys)
                    if (kv.Value.EndsWith(passport.PassportNumber, StringComparison.Ordinal))
                        throw new KioskActionException("OCR_DUPLICATE_DOCUMENT");
                _docKeys[purpose] = docKey;
                _passports[purpose] = passport;
            }

            return new
            {
                accepted = true,
                sequence,
                pageType,
                documentKey = docKey,
                guest = new Dictionary<string, object?>
                {
                    ["lastNameRu"] = passport.LastName,
                    ["firstNameRu"] = passport.FirstName,
                    ["middleNameRu"] = passport.MiddleName,
                    ["birthDate"] = passport.DateOfBirth?.ToString("yyyy-MM-dd"),
                    ["documentNumber"] = passport.PassportNumber
                }
            };
        }

        // ==================== pms ====================

        private async Task<object> GuestUpsertAsync(JsonElement p, CancellationToken ct)
        {
            EnsureBooking();
            var index = Int(p, "index");
            var purpose = index == 0 ? "primary" : "second";

            GuestDocumentData guest;
            if (_passports.TryGetValue(purpose, out var pp))
            {
                guest = GuestDocumentData.FromPassport(pp)!;
            }
            else
            {
                // Данных сканера нет (маловероятно) — собираем из формы UI
                guest = new GuestDocumentData
                {
                    LastName = Str(p, "lastNameRu") ?? Str(p, "lastName"),
                    FirstName = Str(p, "firstNameRu") ?? Str(p, "firstName"),
                    MiddleName = Str(p, "middleNameRu")
                };
            }

            // PMS без эндпоинтов профиля гостя: заселение должно продолжиться,
            // персонал внесёт паспортные данные вручную.
            if (!_pms.Capabilities.CanSendGuestDocuments)
            {
                _logger.LogInformation(
                    "PMS {Vendor} не поддерживает профиль гостя — паспортные данные пропущены",
                    _pms.Capabilities.Vendor);

                _ = _notifications.NotifyStaffAsync(
                    $"ℹ️ Киоск: паспортные данные гостя {_bookingId} не переданы в PMS — " +
                    "подключённая PMS не поддерживает эту операцию. Внесите данные вручную.",
                    ct);

                return new { ok = true, guestId = $"guest-{index + 1}", guestDocsSent = false };
            }

            var profile = await _pms.CreateGuestProfileAsync(guest, ct);
            if (!profile.IsSuccess)
                throw new KioskActionException("API_SERVER_ERROR", profile.Error);

            var guestId = profile.Value?.PersonId ?? $"guest-{index + 1}";

            if (!string.IsNullOrEmpty(_bookingGuid))
            {
                var upd = await _pms.UpdateReservationGuestsAsync(_bookingGuid, guest, profile.Value!, ct);
                if (!upd.IsSuccess)
                    _logger.LogWarning("MONE: профиль {Id} создан, но не привязан к брони: {Err}",
                        guestId, upd.Error);   // не валим чекин из-за привязки
            }

            while (_guestIds.Count <= index) _guestIds.Add("");
            _guestIds[index] = guestId;
            return new { ok = true, guestId };
        }

        /// <summary>
        /// Чекпоинт перед мутациями во внешних системах: если процесс упадёт на
        /// заселении или записи карты, персонал увидит, что деньги списаны и
        /// нужен возврат.
        ///
        /// Писать надо ДО pms.checkin, а не после оплаты: к этому моменту мы
        /// ещё не знаем, чем кончится заселение.
        /// </summary>
        private async Task SaveSessionAsync(CheckInState state, string lastError = null)
        {
            // Различаем «заказ создан» и «деньги списаны». Для QR это разные
            // моменты: гость минуту смотрит на код и может уйти, ничего не
            // заплатив.
            var orderCreated = !string.IsNullOrEmpty(_payTxId) && !_payRefunded;
            var moneyTaken = orderCreated && _payConfirmed;

            var ctx = new CheckInContext
            {
                ReservationId = _bookingId,
                RoomNumber = _room,
                GuestId = _guestIds.Count > 0 ? _guestIds[0] : null,
                TotalAmount = _price,
                PaidAmount = moneyTaken ? _price : 0m,
                CheckInDate = _arrival,
                CheckOutDate = _departure,
                PassportNumber = _passports.TryGetValue("primary", out var pp) ? pp.PassportNumber : null,
                GuestName = _passports.TryGetValue("primary", out var pn) ? pn.FullName : null,
                PaymentTransactionId = orderCreated ? _payTxId : null,
                PaymentConfirmed = moneyTaken,
                KeyCardEncoded = !string.IsNullOrEmpty(_pendingUids
                    .OrderBy(kv => kv.Key).Select(kv => kv.Value).LastOrDefault()),
                CurrentState = state,
                LastError = lastError,

                // Возврат нужен только когда деньги РЕАЛЬНО списаны, а заселение
                // не завершено. Раньше здесь стояло «заказ создан», из-за чего
                // брошенный QR (гость не оплатил) помечался как «оплата
                // подтверждена, требуется возврат» — персонал искал бы деньги,
                // которых никто не платил.
                RequiresCompensation = moneyTaken,
                RequiredCompensation = moneyTaken
                    ? CompensationAction.RefundPayment
                    : CompensationAction.None
            };

            await _sessionStore.SaveAsync(ctx, CancellationToken.None);
        }

        /// <summary>
        /// Шлагбаум перед заселением в PMS.
        ///
        /// Оплата проверяется у владельца заказа (банк или пинпад), а не по
        /// экрану: единственный источник истины о деньгах — их ответ.
        ///
        /// Раньше такой проверки здесь не было вовсе — только EnsureBooking().
        /// Экран открывает запись после подтверждения, но запрос pms.checkin
        /// мог прийти и без оплаты (гость или баг UI), и PMS проводила гостя
        /// бесплатно. Мост KioskBridgeHandler такой шлагбаум имел — расхождение
        /// двух путей и дало дыру именно в работающем.
        /// </summary>
        private async Task EnsurePaidAsync(CancellationToken ct)
        {
            if (_price <= 0) return;   // бесплатное проживание

            if (string.IsNullOrEmpty(_payTxId))
                throw new KioskActionException("PAYMENT_NOT_CONFIRMED",
                    "Оплата не создана — сначала оплатите проживание");

            if (_payRefunded) return;  // деньги уже вернули, заселение не идёт

            // Та же проверка, что и на экране оплаты: статус И сумма.
            // Раньше здесь проверялся только флаг Confirmed — то есть прямой
            // вызов pms.checkin проходил мимо сверки суммы, даже если банк
            // списал не то, что стоит бронь.
            var state = await GetPaymentStatusAsync(_payTxId, ct);

            if (state != PaymentState.Paid)
            {
                _logger.LogWarning(
                    "MONE: заселение отклонено — оплата {TxId} не подтверждена ({Method})",
                    _payTxId, _payMethod);

                throw new KioskActionException("PAYMENT_NOT_CONFIRMED",
                    "Оплата не подтверждена — заселение невозможно");
            }
        }

        private async Task<object> CheckInAsync(JsonElement p, CancellationToken ct)
        {
            EnsureBooking();
            var room = Str(p, "room") ?? _room;

            // Бесплатное проживание (комплимент, трансфер) оплаты не требует.
            // В интерфейсе такое бронирование сразу помечается подтверждённым,
            // иначе шаг ключей был бы недостижим.
            if (_price <= 0)
                _payConfirmed = true;

            // Деньги подтверждены ДО заселения в PMS: неоплаченного гостя
            // в бронь проводить нельзя
            await EnsurePaidAsync(ct);

            // Чекпоинт с флагом компенсации ДО заселения в PMS
            await SaveSessionAsync(CheckInState.UpdatingLogus);

            var r = await _pms.SelfCheckInAsync(_bookingId, room, ct);
            if (!r.IsSuccess)
            {
                _ = _notifications.NotifyStaffAsync(
                    $"🛑 Киоск MONE: Logus не заселил бронь {_bookingId} (номер {room}): {r.Error}", ct);
                await SaveSessionAsync(CheckInState.Failed, $"Logus: {r.Error}");
                throw new KioskActionException("PMS_CHECKIN_FAILED", r.Error);
            }
            _checkinId = $"ci-{_bookingId}-{DateTime.UtcNow.Ticks}";
            return new { ok = true, checkinId = _checkinId, room };
        }

        private Task<object> VisionPrepareAsync(JsonElement p, CancellationToken ct)
        {
            // Подготовка замка/доступа на мосте VisionLine — контракт уточняется,
            // пока успешный no-op (карта всё равно пишется card.write)
            var room = Str(p, "room") ?? _room;
            return Task.FromResult<object>(new { ok = true, accessId = $"acc-{room}" });
        }

        private async Task<object> FinalizeAsync(JsonElement p, CancellationToken ct)
        {
            EnsureBooking();
            var cards = Int(p, "cards");
            _ = _notifications.NotifyStaffAsync(
                $"✅ Киоск: заселение {_bookingId} в номер {_room} завершено, карт выдано: {cards}.", ct);

            // Заселение дошло до конца — компенсация больше не нужна,
            // поэтому чекпоинт снимаем, а не переписываем
            await _sessionStore.ClearAsync(ct);
            _payTxId = "";
            _payRefunded = false;
            _payConfirmed = false;

            return new { ok = true, finalizeId = $"fin-{_bookingId}" };
        }

        // ==================== card lifecycle ====================
        // Физическая запись происходит один раз — в card.write (EncodeKeyCard).
        // detect/read/verify/awaitRemoval — виртуальные шаги конвейера MONE.

        private readonly Dictionary<int, string> _pendingUids = new();

        private Task<object> CardDetect(JsonElement p)
        {
            var index = Int(p, "index");
            var uid = $"CARD-{DateTime.UtcNow.Ticks:X}-{index}";
            _pendingUids[index] = uid;
            return Task.FromResult<object>(new { ok = true, uid, count = 1, supported = true, inUse = false });
        }

        private Task<object> CardRead(JsonElement p) =>
            Task.FromResult<object>(new { ok = true, uid = Str(p, "uid"), read = true });

        private async Task<object> CardWriteAsync(JsonElement p, CancellationToken ct)
        {
            EnsureBooking();
            var index = Int(p, "index");
            var room = Str(p, "room") ?? _room;

            // Ключ не пишется в бронь без подтверждённой брони и оплаты —
            // тот же шлагбаум, что и перед заселением. Иначе pms.checkin,
            // пропущенный багом UI, приводил бы к записанной карте.
            if (string.IsNullOrEmpty(_bookingId))
                throw new KioskActionException("BOOKING_NOT_FOUND",
                    "Бронь не найдена: сначала выполните поиск брони");
            if (string.IsNullOrEmpty(_checkinId))
                throw new KioskActionException("PMS_CHECKIN_UNKNOWN",
                    "Гость не заселён: ключ-карта не записывается");

            var encode = await _visionLine.EncodeKeyCardAsync(room, _arrival, _departure, ct);
            if (!encode.IsSuccess || encode.Value == null || !encode.Value.Success)
            {
                var err = encode.Error ?? encode.Value?.ErrorMessage ?? "нет ответа энкодера";
                _ = _notifications.NotifyStaffAsync(
                    $"⚠️ Киоск: карта НЕ записана (бронь {_bookingId}, номер {room}): {err}. Выдайте карту вручную.", ct);

                // Гость заселён и оплатил, ключ не записан. Возврат здесь НЕ делаем:
                // бронь уже в статуме проживания, и деньги на счёте гостя — отмена
                // оплаты противоречила бы факту. Персонал выдаёт карту вручную.
                await SaveSessionAsync(CheckInState.EncodingKeyCard, $"Карта не записана: {err}");

                throw new KioskActionException("CARD_WRITE_FAILED", err);
            }
            var uid = encode.Value.CardUid ?? _pendingUids.GetValueOrDefault(index, Str(p, "uid"));
            return new { ok = true, uid, written = true, partial = false };
        }

        private Task<object> CardVerify(JsonElement p) =>
            Task.FromResult<object>(new { ok = true, uid = Str(p, "uid"), verified = true, matches = true });

        private Task<object> CardAwaitRemoval(JsonElement p) =>
            Task.FromResult<object>(new { ok = true, uid = Str(p, "uid"), removed = true });

        // ==================== payment ====================

        private async Task<object> PaymentCreateAsync(JsonElement p, CancellationToken ct)
        {
            EnsureBooking();
            var method = Str(p, "method") == "card" ? "card" : "qr";

            // Повторный payment.create в той же сессии не должен создавать второй
            // заказ: для карты это риск двойного списания, для QR — две
            // конкурирующие ссылки, из которых гость оплатит не ту.
            if (!string.IsNullOrEmpty(_payTxId) && _payMethod == method)
            {
                var existing = await GetPaymentStatusAsync(_payTxId, ct);

                if (existing == PaymentState.Paid)
                {
                    if (!_payConfirmed)
                    {
                        _payConfirmed = true;
                        await SaveSessionAsync(CheckInState.PaymentConfirmed);
                    }

                    return new
                    {
                        orderId = _payTxId,
                        confirmed = true,
                        alreadyPaid = true,
                        rrn = (string)null,
                        qrImageBase64 = (string)null,
                        qrPayload = (string)null,
                        expiresAt = (DateTime?)null
                    };
                }

                if (existing == PaymentState.Unknown)
                    throw new KioskActionException("PAYMENT_STATUS_UNKNOWN",
                        "Не удалось проверить состояние оплаты — повторите или позовите сотрудника");

                // Заказ жив и ещё не оплачен — отдаём тот же QR, что уже на экране.
                if (_qrExpiresAt is null || _qrExpiresAt > DateTime.UtcNow)
                {
                    return new
                    {
                        orderId = _payTxId,
                        confirmed = false,
                        alreadyPaid = false,
                        rrn = (string)null,
                        qrImageBase64 = _qrImage,
                        qrPayload = _qrPayload,
                        expiresAt = _qrExpiresAt
                    };
                }
                // QR истёк — можно регистрировать новый заказ.
            }

            _payMethod = method;

            Result<PaymentResult> r = method == "card"
                ? await _cardTerminal.ProcessCardPaymentAsync(_price, _bookingId, ct)
                : await _alfaBank.ProcessPaymentAsync(_price, _bookingId, ct);

            if (!r.IsSuccess)
                throw new KioskActionException("PAYMENT_DECLINED", r.Error);

            _payTxId = r.Value.TransactionId ?? "";
            _qrPayload = r.Value.QrPayload;
            _qrImage = r.Value.QrImageBase64;
            _qrExpiresAt = r.Value.QrExpiresAt;

            // Подтверждение есть только при синхронном списании (карта даёт
            // RRN сразу). Для QR гость ещё не заплатил — деньги не списаны.
            _payConfirmed = !string.IsNullOrEmpty(r.Value.RRN);

            // Чекпоинт: транзакция уже создана, но компенсация потребуется
            // только после фактического подтверждения
            await SaveSessionAsync(CheckInState.AwaitingPayment);

            var confirmed = _payConfirmed;
            return new
            {
                orderId = _payTxId,
                confirmed,
                alreadyPaid = false,
                rrn = r.Value.RRN,
                qrImageBase64 = r.Value.QrImageBase64,
                qrPayload = r.Value.QrPayload,
                expiresAt = r.Value.QrExpiresAt
            };
        }

        private async Task<object> PaymentStatusAsync(JsonElement p, CancellationToken ct)
        {
            var orderId = Str(p, "orderId") ?? _payTxId;
            if (string.IsNullOrEmpty(orderId)) return new { confirmed = false };

            Result<PaymentStatus> r = _payMethod == "card"
                ? await _cardTerminal.CheckPaymentStatusAsync(orderId, ct)
                : await _alfaBank.CheckPaymentStatusAsync(orderId, ct);

            if (!r.IsSuccess) return new { confirmed = false };   // транспортный сбой ≠ отказ

            // Подтверждение запоминаем и СРАЗУ пишем в чекпоинт. Раньше флаг
            // менялся только в памяти, а на диск попадал при следующем шаге —
            // то есть падение между оплатой и заселением оставляло «возврат
            // не нужен», хотя деньги были списаны.
            if (r.Value.Confirmed && orderId == _payTxId)
            {
                // Сумма — такой же предмет сверки, как и сам факт оплаты:
                // терминал мог списать другую сумму (ввод вручную), и заселять
                // по цене брони было бы неверно. Расхождение не подтверждаем
                // и дальше не пускаем — разбирается сотрудник.
                if (!AmountMatches(r.Value.Amount, _price))
                {
                    _logger.LogCritical(
                        "MONE: сумма платежа {OrderId} не совпала: подтверждено {Paid}, ожидалось {Expected}",
                        orderId, r.Value.Amount, _price);

                    _ = _notifications.NotifyStaffAsync(
                        $"⚠️ Киоск: сумма оплаты не совпала (бронь {_bookingId}, заказ {orderId}): " +
                        $"списано {r.Value.Amount:0.##} ₽, ожидалось {_price:0.##} ₽. " +
                        "Заселение остановлено, нужен сотрудник.",
                        ct);

                    throw new KioskActionException("PAYMENT_AMOUNT_MISMATCH",
                        $"Списано {r.Value.Amount:0.##} ₽ вместо {_price:0.##} ₽ — позовите сотрудника");
                }

                _paidAmount = r.Value.Amount;

                if (!_payConfirmed)
                {
                    _payConfirmed = true;
                    await SaveSessionAsync(CheckInState.PaymentConfirmed);
                }
            }

            return new { confirmed = r.Value.Confirmed, rrn = r.Value.RRN };
        }

        /// <summary>
        /// Отмена оплаты на экране. Развилка по факту оплаты:
        ///
        ///   не оплачено → отменяем QR-заказ у банка (не возврат: денег нет);
        ///   оплачено   → ВОЗВРАЩАЕМ деньги.
        ///
        /// Раньше здесь был только первый случай: если гость уже оплатил, а затем
        /// нажал отмену (или заселение упало на шаге записи карты), деньги
        /// оставались у отеля, а чекпоинт об этом не сообщал.
        /// </summary>
        private async Task<object> PaymentCancelAsync(CancellationToken ct)
        {
            var txId = _payTxId;
            if (string.IsNullOrEmpty(txId))
                return new { ok = true };

            // Статус спрашиваем у владельца заказа, а не верим экрану: единственный
            // источник истины о деньгах — банк или терминал.
            var status = await GetPaymentStatusAsync(txId, ct);

            if (status == PaymentState.Unknown)
            {
                // Не знаем, оплачено ли. Ни возврата, ни отмены: и то и другое
                // может навредить гостю. Персонал разберётся по чекпоинту.
                _logger.LogWarning(
                    "MONE: статус {OrderId} недоступен при отмене — возврат и отмена пропущены", txId);

                _ = _notifications.NotifyStaffAsync(
                    $"⚠️ Киоск: не удалось проверить статус оплаты (бронь {_bookingId}, " +
                    $"транзакция {txId}). Отмена не выполнена автоматически — проверьте вручную.",
                    ct);

                return new
                {
                    ok = false,
                    refunded = false,
                    code = "PAYMENT_STATUS_UNKNOWN",
                    message = "Не удалось проверить оплату — позовите сотрудника"
                };
            }

            if (status == PaymentState.Paid)
            {
                var compensation = BuildCompensation(txId);
                var refunded = await compensation.RefundAsync(_bookingId, ct);
                _payRefunded = refunded;

                // Транзакцию обнуляем ТОЛЬКО при успешном возврате. Раньше
                // это делалось всегда: при неудаче возврата чекпоинт терял
                // номер транзакции, компенсация становилась невозможной,
                // а повторная отмена рапортовала «отменено».
                if (refunded) _payTxId = "";
                else _payConfirmed = false;

                return refunded
                    ? new { ok = true, refunded = true }
                    : new
                    {
                        ok = false,
                        refunded = false,
                        code = "PAYMENT_REFUND_FAILED",
                        message = "Возврат не выполнен автоматически — позовите сотрудника"
                    };
            }

            var cancelCompensation = BuildCompensation(txId);
            await cancelCompensation.CancelUnpaidAsync(ct);
            _payTxId = "";
            _payConfirmed = false;
            return new { ok = true, refunded = false };
        }

        private enum PaymentState { Unknown, Unpaid, Paid }

        /// <summary>
        /// Состояние платежа по данным владельца заказа (банк или терминал).
        /// Unknown означает «не удалось узнать» — это не то же самое, что «не оплачено».
        /// </summary>
        private async Task<PaymentState> GetPaymentStatusAsync(string orderId, CancellationToken ct)
        {
            try
            {
                var status = _payMethod == "card"
                    ? await _cardTerminal.CheckPaymentStatusAsync(orderId, ct)
                    : await _alfaBank.CheckPaymentStatusAsync(orderId, ct);

                if (!status.IsSuccess) return PaymentState.Unknown;
                if (!status.Value.Confirmed) return PaymentState.Unpaid;

                // То же правило, что и при подтверждении на экране: если банк
                // назвал другую сумму, автоматический возврат опасен — вернём
                // не столько, сколько списали. Отдаём разбор сотруднику.
                if (!AmountMatches(status.Value.Amount, _price))
                {
                    _logger.LogCritical(
                        "MONE: сумма {OrderId} не совпала: подтверждено {Paid}, ожидалось {Expected}",
                        orderId, status.Value.Amount, _price);

                    _ = _notifications.NotifyStaffAsync(
                        $"⚠️ Киоск: сумма оплаты не совпала (заказ {orderId}): " +
                        $"списано {status.Value.Amount:0.##} ₽, ожидалось {_price:0.##} ₽. " +
                        "Автоматический возврат не выполнен — проверьте вручную.",
                        ct);

                    return PaymentState.Unknown;
                }

                _paidAmount = status.Value.Amount;
                return PaymentState.Paid;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MONE: сбой проверки статуса {OrderId}", orderId);
                return PaymentState.Unknown;
            }
        }

        /// <summary>Сервис компенсации, настроенный на текущий платёж сессии.</summary>
        private PaymentCompensationService BuildCompensation(string transactionId) =>
            new(_alfaBank, _cardTerminal, _notifications, _sessionStore, null)
            {
                PaymentMethod = _payMethod,
                TransactionId = transactionId,
                Amount = _paidAmount ?? _price,
                AlreadyRefunded = _payRefunded
            };

        // ==================== helpers ====================

        private void EnsureBooking()
        {
            if (!_found) throw new KioskActionException("BOOKING_NOT_FOUND");
        }

        /// <summary>
        /// Сверяет сумму, которую подтвердил владелец платежа, с ожидаемой.
        /// true также когда сумму проверить нельзя (поле не пришло): отсутствие
        /// суммы — не повод блокировать заселение, а расхождение — повод.
        ///
        /// Шлюзы отдают сумму либо в рублях, либо в копейках. Принимаем оба
        /// варианта, иначе единицы измерения выглядели бы как расхождение и
        /// киоск останавливался бы на ровном месте.
        /// </summary>
        private static bool AmountMatches(decimal? reported, decimal expected)
        {
            if (reported is null) return true;
            var value = reported.Value;
            return Math.Abs(value - expected) < 0.01m
                || Math.Abs(value / 100m - expected) < 0.01m;
        }

        private static string Str(JsonElement p, string name) =>
            p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static int Int(JsonElement p, string name) =>
            p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;
    }
}
