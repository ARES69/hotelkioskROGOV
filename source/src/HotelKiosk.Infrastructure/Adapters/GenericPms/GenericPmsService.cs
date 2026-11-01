using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.GenericPms
{
    /// <summary>
    /// Универсальный HTTP/JSON-адаптер PMS.
    ///
    /// Задача: подключить киоск к PMS, у которой нет нашего SDK, не меняя код.
    /// Всё, что различается между системами — URL, имена полей, способ
    /// авторизации, формат дат и сумм — описывается в appsettings.json
    /// (секция Pms:Generic). Здесь остаётся только перевод канонической модели
    /// киоска в вызовы конкретной системы и обратно.
    ///
    /// Честное ограничение: адаптер покрывает PMS с REST/JSON-контрактом.
    /// Системы с SOAP (1С:Отель в части старых конфигураций, ShelterOld)
    /// требуют отдельного адаптера — но и для них карта операций и путей
    /// полей остаётся той же, то есть конфиг переиспользуется.
    /// </summary>
    public class GenericPmsService : IPmsProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<GenericPmsService> _logger;
        private readonly GenericPmsOptions _opts;

        public GenericPmsService(
            HttpClient http, IOptions<GenericPmsOptions> opts, ILogger<GenericPmsService> logger)
        {
            _http = http;
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _logger = logger;

            if (!string.IsNullOrWhiteSpace(_opts.BaseUrl))
                _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");

            _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);

            foreach (var header in _opts.DefaultHeaders)
                if (!string.IsNullOrWhiteSpace(header.Key) &&
                    !string.IsNullOrWhiteSpace(header.Value))
                    _http.DefaultRequestHeaders.TryAddWithoutValidation(
                        header.Key, header.Value);
        }

        /// <summary>Что именно поддерживает подключённая PMS.</summary>
        public PmsCapabilities Capabilities => new(
            Vendor: _opts.Vendor,
            CanSearchReservations: _opts.Authenticate.IsConfigured,
            CanCheckIn: _opts.CheckIn.IsConfigured,
            CanCancel: _opts.Cancel.IsConfigured,
            // Постинг оплаты нужен, только если есть и URL, и код транзакции:
            // без кода PMS не примет платёж, а гость заплатил бы впустую
            CanPostPayment: _opts.AddPayment.IsConfigured &&
                            !string.IsNullOrWhiteSpace(_opts.PaymentTransactionCode),
            CanListArrivals: _opts.Arrivals.IsConfigured,
            CanSendGuestDocuments: _opts.SendGuestDocuments && _opts.UpsertGuest.IsConfigured,
            CanReadReservationDetails: _opts.ReservationDetails.IsConfigured);

        // ====================================================================
        //  Поиск брони
        // ====================================================================

        public async Task<Result<SelfCheckinInfo>> AuthenticateSelfCheckinAsync(
            string reservationNo, string confirmationMode, string key, CancellationToken ct)
        {
            var op = _opts.Authenticate;
            if (!op.IsConfigured)
                return Unsupported<SelfCheckinInfo>("поиск брони");

            var values = Vars(("reservationNo", reservationNo),
                              ("confirmationMode", confirmationMode),
                              ("key", key));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<SelfCheckinInfo>();

            SelfCheckinInfo info;
            try
            {
                info = MapSelfCheckin(response.Root);
            }
            catch (PmsMappingException ex)
            {
                // Маппинг внутри using: ответ не разобран — гость увидит
                // внятную причину, а не NullReferenceException
                return Result<SelfCheckinInfo>.BusinessFailure(ex.Message);
            }

            return Result<SelfCheckinInfo>.Success(info);
        }

        // ====================================================================
        //  Заселение и отмена
        // ====================================================================

        public async Task<Result<bool>> SelfCheckInAsync(
            string reservationNo, string roomNo, CancellationToken ct)
        {
            var op = _opts.CheckIn;
            if (!op.IsConfigured)
                return Unsupported<bool>("заселение");

            var values = Vars(("reservationId", reservationNo), ("reservationNo", reservationNo),
                              ("roomNo", roomNo));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<bool>();

            return Result<bool>.Success(true);
        }

        public async Task<Result<bool>> CancelReservationAsync(
            string reservationId, string cancellationReasonCode, CancellationToken ct)
        {
            var op = _opts.Cancel;
            if (!op.IsConfigured)
                return Unsupported<bool>("отмена брони");

            // Наш внутренний код причины переводим в код PMS, если словарь задан
            var reason = ResolveCancellationReason(cancellationReasonCode);

            var values = Vars(("reservationId", reservationId), ("reservationNo", reservationId),
                              ("cancellationReason", reason),
                              ("cancellationReasonCode", reason));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<bool>();

            return Result<bool>.Success(true);
        }

        private string ResolveCancellationReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return null;

            return _opts.CancellationReasonCodes.TryGetValue(reason, out var code)
                ? code
                : reason;
        }

        // ====================================================================
        //  Оплата
        // ====================================================================

        public async Task<Result<bool>> AddPaymentAsync(
            string reservationGenericNo, decimal amount, string bankTransactionId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opts.PaymentTransactionCode))
            {
                // Раньше это приводило к BusinessFailure в середине флоу:
                // гость заплатил, а на folio брони деньги не попали
                _logger.LogWarning(
                    "PMS {Vendor}: код транзакции не задан (Pms:Generic:PaymentTransactionCode) — " +
                    "оплата не будет проведена в счёт брони", _opts.Vendor);
                return Result<bool>.BusinessFailure(
                    "Код транзакции PMS не настроен — оплата не проведена в счёт брони");
            }

            var op = _opts.AddPayment;
            if (!op.IsConfigured)
                return Unsupported<bool>("постинг оплаты");

            var values = Vars(
                ("reservationId", reservationGenericNo),
                ("reservationNo", reservationGenericNo),
                ("amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("transactionId", bankTransactionId),
                ("uniqueId", bankTransactionId),
                ("transactionCode", _opts.PaymentTransactionCode),
                ("propertyId", _opts.PropertyId));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<bool>();

            return Result<bool>.Success(true);
        }

        // ====================================================================
        //  Заезды
        // ====================================================================

        public async Task<Result<IReadOnlyList<ArrivalInfo>>> GetArrivalsAsync(
            DateTime arrivalDate, CancellationToken ct)
        {
            var op = _opts.Arrivals;
            if (!op.IsConfigured)
            {
                _logger.LogInformation(
                    "PMS {Vendor}: список заездов не поддерживается — экран «сегодня заезжают» скрыт",
                    _opts.Vendor);
                return Result<IReadOnlyList<ArrivalInfo>>.BusinessFailure(
                    "PMS не поддерживает список заездов");
            }

            var values = Vars(
                ("date", arrivalDate.ToString("yyyy-MM-dd")),
                ("dateTime", arrivalDate.ToString("yyyy-MM-ddTHH:mm:ss")),
                ("propertyId", _opts.PropertyId));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<IReadOnlyList<ArrivalInfo>>();

            return Result<IReadOnlyList<ArrivalInfo>>.Success(MapArrivals(response.Root));
        }

        // ====================================================================
        //  Данные брони и гости
        // ====================================================================

        public async Task<Result<ReservationDetails>> GetReservationAsync(
            string reservationGenericNo, CancellationToken ct)
        {
            var op = _opts.ReservationDetails;
            if (!op.IsConfigured)
                return Unsupported<ReservationDetails>("данные брони");

            var values = Vars(
                ("reservationId", reservationGenericNo),
                ("reservationNo", reservationGenericNo));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<ReservationDetails>();

            var paths = op.Paths;
            var details = new ReservationDetails
            {
                Guid = PmsJson.GetString(response.Root, paths.ReservationGuid) ??
                        PmsJson.GetString(response.Root, "guid"),
                GenericNo = PmsJson.GetString(response.Root, paths.ReservationId)
            };

            if (string.IsNullOrEmpty(details.Guid))
            {
                _logger.LogWarning(
                    "PMS {Vendor}: ответ брони без внутреннего идентификатора — " +
                    "привязка гостя может не сработать", _opts.Vendor);
            }

            return string.IsNullOrEmpty(details.Guid)
                ? Result<ReservationDetails>.BusinessFailure(
                    "PMS не вернул идентификатор брони")
                : Result<ReservationDetails>.Success(details);
        }

        public async Task<Result<GuestProfileRef>> CreateGuestProfileAsync(
            GuestDocumentData guest, CancellationToken ct)
        {
            var op = _opts.UpsertGuest;
            if (!op.IsConfigured)
                return Unsupported<GuestProfileRef>("профиль гостя");

            var values = Vars(
                ("lastName", guest?.LastName),
                ("firstName", guest?.FirstName),
                ("middleName", guest?.MiddleName),
                ("birthDate", guest?.BirthDate?.ToString("yyyy-MM-dd")),
                ("sex", guest?.Sex),
                ("documentNumber", guest?.DocumentNumber),
                ("documentSeries", guest?.DocumentSeries),
                ("issueDate", guest?.IssueDate?.ToString("yyyy-MM-dd")),
                ("departmentCode", guest?.DepartmentCode),
                ("issuedBy", guest?.IssuedBy),
                ("birthPlace", guest?.BirthPlace),
                ("countryCode", guest?.CountryCode),
                ("docTypeCode", guest?.DocumentTypeCode ?? _opts.DocumentTypeCode),
                ("docTypeName", guest?.DocumentTypeName ?? _opts.DocumentTypeName));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<GuestProfileRef>();

            var paths = op.Paths;
            var profile = new GuestProfileRef
            {
                PersonId = PmsJson.GetString(response.Root, paths.PersonId),
                ProfileGuid = PmsJson.GetString(response.Root, paths.ProfileGuid)
            };

            if (string.IsNullOrEmpty(profile.PersonId) && string.IsNullOrEmpty(profile.ProfileGuid))
            {
                _logger.LogWarning(
                    "PMS {Vendor}: профиль гостя создан, но ответ без personId/profileGuid", _opts.Vendor);
                return Result<GuestProfileRef>.BusinessFailure(
                    "PMS не вернул идентификатор профиля гостя");
            }

            return Result<GuestProfileRef>.Success(profile);
        }

        public async Task<Result<bool>> UpdateReservationGuestsAsync(
            string reservationGuid, GuestDocumentData mainGuest,
            GuestProfileRef profile, CancellationToken ct)
        {
            var op = _opts.AttachGuest;
            if (!op.IsConfigured)
                return Unsupported<bool>("привязка гостя к брони");

            var values = Vars(
                ("reservationGuid", reservationGuid ?? string.Empty),
                ("reservationId", reservationGuid ?? string.Empty),
                ("personId", profile?.PersonId),
                ("profileGuid", profile?.ProfileGuid),
                ("lastName", mainGuest?.LastName),
                ("firstName", mainGuest?.FirstName),
                ("middleName", mainGuest?.MiddleName),
                ("documentNumber", mainGuest?.DocumentNumber),
                ("documentSeries", mainGuest?.DocumentSeries),
                ("docTypeCode", _opts.DocumentTypeCode));

            using var response = await CallAsync(op, values, ct);
            if (!response.Ok) return response.Failure<bool>();

            return Result<bool>.Success(true);
        }

        public async Task<bool> PingAsync(CancellationToken ct)
        {
            var op = _opts.Ping;

            try
            {
                var values = Vars(("propertyId", _opts.PropertyId));

                var url = op.IsConfigured
                    ? PmsJson.Expand(op.Path, values, urlEncode: true)
                    : string.Empty;

                var request = new HttpRequestMessage(
                    ParseMethod(op.IsConfigured ? op.Method : "GET"),
                    string.IsNullOrEmpty(url) ? null : url);

                if (request.Content == null && IsWriteMethod(request.Method) && op.Body != null)
                    request.Content = BuildContent(op, values);

                ApplyAuth(request, values);
                ApplyOperationHeaders(request, op, values);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

                using var resp = await _http.SendAsync(request, cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PMS {Vendor}: health-check не пройден", _opts.Vendor);
                return false;
            }
        }

        // ====================================================================
        //  Маппинг ответов
        // ====================================================================

        private SelfCheckinInfo MapSelfCheckin(JsonElement root)
        {
            var p = _opts.Authenticate.Paths;

            var reservation = new SelfCheckinReservation
            {
                Id = PmsJson.GetString(root, p.ReservationId),
                RoomCode = PmsJson.GetString(root, p.ReservationRoomCode),
                RoomTypeCode = PmsJson.GetString(root, p.ReservationRoomTypeCode),
                GuestsCount = PmsJson.GetInt(root, p.ReservationGuestsCount) ?? 1,
                ArrivalDate = PmsJson.GetDateTime(root, p.ReservationArrival) ?? DateTime.Today,
                DepartureDate = PmsJson.GetDateTime(root, p.ReservationDeparture) ?? DateTime.Today,
                Price = PmsJson.GetDecimal(root, p.ReservationPrice) ?? 0m
            };

            if (string.IsNullOrEmpty(reservation.Id))
                throw new PmsMappingException(
                    "PMS не вернул номер брони — проверьте Pms:Generic:Authenticate:Paths:ReservationId");

            var guest = new SelfCheckinGuest
            {
                Id = PmsJson.GetString(root, p.GuestId),
                FirstName = PmsJson.GetString(root, p.GuestFirstName),
                LastName = PmsJson.GetString(root, p.GuestLastName),
                MiddleName = PmsJson.GetString(root, p.GuestMiddleName),
                BirthDate = PmsJson.GetDateTime(root, p.GuestBirthDate)
            };

            return new SelfCheckinInfo
            {
                Guest = guest,
                Reservation = reservation,
                CurrencyNls = PmsJson.GetString(root, p.Currency) ?? "RUB",
                RoomsInfo = MapRooms(root, p)
            };
        }

        private SelfCheckinRoomsInfo MapRooms(JsonElement root, FieldMap p)
        {
            var info = new SelfCheckinRoomsInfo();

            if (string.IsNullOrWhiteSpace(p.AvailableRooms)) return info;

            foreach (var room in PmsJson.GetArray(root, p.AvailableRooms))
            {
                info.Rooms.Add(new SelfCheckinRoom
                {
                    RoomCode = PmsJson.GetString(room, p.RoomCode),
                    RoomTypeCode = PmsJson.GetString(room, p.RoomTypeCode),
                    FloorCode = PmsJson.GetString(room, p.RoomFloor),
                    BuildingCode = PmsJson.GetString(room, p.RoomBuilding),
                    IsOccupied = PmsJson.GetBool(room, p.RoomOccupied) ?? false
                });
            }

            if (!string.IsNullOrWhiteSpace(p.RoomTypes))
            {
                foreach (var type in PmsJson.GetArray(root, p.RoomTypes))
                {
                    info.RoomTypes.Add(new SelfCheckinRoomType
                    {
                        Code = PmsJson.GetString(type, p.RoomTypeCode),
                        Name = PmsJson.GetString(type, p.RoomTypeName)
                    });
                }
            }

            return info;
        }

        private List<ArrivalInfo> MapArrivals(JsonElement root)
        {
            var p = _opts.Arrivals.Paths;
            var result = new List<ArrivalInfo>();

            foreach (var item in PmsJson.GetArray(root, p.ArrivalsArray))
            {
                result.Add(new ArrivalInfo
                {
                    GenericNo = PmsJson.GetString(item, p.ArrivalGenericNo),
                    Guid = PmsJson.GetString(item, p.ArrivalGuid),
                    LastName = PmsJson.GetString(item, p.ArrivalLastName),
                    FirstName = PmsJson.GetString(item, p.ArrivalFirstName),
                    ArrivalDate = PmsJson.GetDateTime(item, p.ArrivalArrivalDate) ?? default,
                    DepartureDate = PmsJson.GetDateTime(item, p.ArrivalDepartureDate) ?? default,
                    StayAmount = PmsJson.GetDecimal(item, p.ArrivalStayAmount),
                    Balance = PmsJson.GetDecimal(item, p.ArrivalBalance)
                });
            }

            return result;
        }

        // ====================================================================
        //  Транспорт
        // ====================================================================

        private async Task<PmsResponse> CallAsync(
            OperationOptions op, IReadOnlyDictionary<string, string> values, CancellationToken ct)
        {
            if (!op.IsConfigured)
                throw new InvalidOperationException("Операция не сконфигурирована");

            try
            {
                var url = PmsJson.Expand(op.Path, values, urlEncode: true);
                var request = new HttpRequestMessage(ParseMethod(op.Method), url);

                if (IsWriteMethod(request.Method) && op.Body != null)
                    request.Content = BuildContent(op, values);

                ApplyAuth(request, values);
                ApplyOperationHeaders(request, op, values);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

                using var resp = await _http.SendAsync(request, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(ct);

                return Interpret(op, resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return PmsResponse.Transport(
                    $"PMS {_opts.Vendor}: таймаут {_opts.TimeoutSeconds} с");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "PMS {Vendor}: сетевой сбой", _opts.Vendor);
                return PmsResponse.Transport($"PMS {_opts.Vendor}: недоступна ({ex.Message})");
            }
            catch (PmsMappingException ex)
            {
                _logger.LogError(ex, "PMS {Vendor}: не удалось разобрать ответ", _opts.Vendor);
                return PmsResponse.Business(ex.Message);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "PMS {Vendor}: ответ не является JSON", _opts.Vendor);
                return PmsResponse.Business("PMS вернула ответ, который не удалось разобрать");
            }
        }

        /// <summary>
        /// Разбор ответа: часть PMS сообщает об ошибке HTTP-кодом, часть —
        /// кодом в теле при HTTP 200. Учитываем оба случая.
        /// </summary>
        private PmsResponse Interpret(OperationOptions op, HttpStatusCode status, string body)
        {
            if (status == HttpStatusCode.NotFound)
                return PmsResponse.NotFound("Бронь не найдена");

            JsonDocument doc = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body) && LooksLikeJson(body))
                    doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                doc = null;
            }

            // Тело не пригодно для разбора, но HTTP успешен — считаем успехом
            if (doc == null)
            {
                if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                    return PmsResponse.Transport(
                        $"PMS {_opts.Vendor}: отказ в доступе (HTTP {(int)status})");

                return (int)status is >= 200 and < 300
                    ? PmsResponse.Success(null)
                    : PmsResponse.Transport(
                        $"PMS {_opts.Vendor}: HTTP {(int)status} {Trunc(body)}");
            }

            var root = doc.RootElement;

            // Ошибка в теле при успешном HTTP
            if (_opts.ErrorInBody && status != HttpStatusCode.Unauthorized &&
                !IsBodySuccess(root))
            {
                var message = PmsJson.GetString(root, _opts.ErrorMessagePath);

                if (LooksLikeNotFound(message))
                {
                    doc.Dispose();
                    return PmsResponse.NotFound(message);
                }

                doc.Dispose();
                return PmsResponse.Business(
                    string.IsNullOrEmpty(message)
                        ? $"PMS {_opts.Vendor} отклонила запрос"
                        : message);
            }

            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            {
                doc.Dispose();
                return PmsResponse.Transport(
                    $"PMS {_opts.Vendor}: отказ в доступе — проверьте Pms:Generic:AuthMode и учётные данные");
            }

            if ((int)status is >= 200 and < 300)
                return PmsResponse.Success(doc);

            doc.Dispose();
            return PmsResponse.Transport($"PMS {_opts.Vendor}: HTTP {(int)status} {Trunc(body)}");
        }

        private bool IsBodySuccess(JsonElement root)
        {
            if (string.IsNullOrWhiteSpace(_opts.SuccessPath))
            {
                // Явного признака нет: успех определяется HTTP-кодом.
                // Пустой объект {} тоже считаем успехом — некоторые PMS
                // возвращают 200 с пустым телом на успешных операциях.
                return true;
            }

            var ok = PmsJson.GetBool(root, _opts.SuccessPath);
            if (ok.HasValue) return ok.Value;

            // Признак может прийти строкой "OK"/"Success"
            var text = PmsJson.GetString(root, _opts.SuccessPath);
            if (string.IsNullOrEmpty(text)) return true;

            return text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("success", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeNotFound(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;

            var markers = new[] { "не найдена", "не найден", "not found",
                                  "неверный параметр подтверждения", "не существует" };

            return Array.Exists(markers,
                m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeJson(string body)
        {
            var trimmed = body?.TrimStart();
            return !string.IsNullOrEmpty(trimmed) && (trimmed[0] == '{' || trimmed[0] == '[');
        }

        private void ApplyAuth(HttpRequestMessage request, IReadOnlyDictionary<string, string> values)
        {
            switch ((_opts.AuthMode ?? "basic").Trim().ToLowerInvariant())
            {
                case "none":
                    break;

                case "bearer":
                    if (!string.IsNullOrWhiteSpace(_opts.Token))
                        request.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", _opts.Token);
                    break;

                case "basic":
                    if (!string.IsNullOrWhiteSpace(_opts.Login))
                    {
                        var raw = $"{_opts.Login}:{_opts.Password ?? string.Empty}";
                        request.Headers.Authorization = new AuthenticationHeaderValue(
                            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
                    }
                    break;

                case "token-header":
                    if (!string.IsNullOrWhiteSpace(_opts.Token))
                    {
                        var name = string.IsNullOrWhiteSpace(_opts.TokenHeaderName)
                            ? "X-Api-Key"
                            : _opts.TokenHeaderName;

                        request.Headers.Remove(name);
                        request.Headers.TryAddWithoutValidation(
                            name, _opts.TokenPrefix + _opts.Token);
                    }
                    break;

                case "token-query":
                    // Токен уходит в строке запроса — добавляем к URL
                    if (!string.IsNullOrWhiteSpace(_opts.Token) && request.RequestUri != null)
                    {
                        var separator = string.IsNullOrEmpty(request.RequestUri.Query)
                            ? "?" : "&";
                        var name = string.IsNullOrWhiteSpace(_opts.TokenQueryName)
                            ? "token"
                            : _opts.TokenQueryName;

                        request.RequestUri = new Uri(
                            request.RequestUri.AbsoluteUri +
                            separator + name + "=" +
                            Uri.EscapeDataString(_opts.TokenPrefix + _opts.Token));
                    }
                    break;

                default:
                    _logger.LogWarning(
                        "PMS {Vendor}: неизвестный AuthMode \"{Mode}\" — запрос уйдёт без авторизации",
                        _opts.Vendor, _opts.AuthMode);
                    break;
            }

            _ = values;
        }

        private void ApplyOperationHeaders(
            HttpRequestMessage request, OperationOptions op, IReadOnlyDictionary<string, string> values)
        {
            if (op.Headers == null) return;

            foreach (var header in op.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key)) continue;

                // Значение вида "@{idempotencyKey}" подставляется, остальное — как есть
                var value = header.Value ?? string.Empty;
                value = value.StartsWith("@", StringComparison.Ordinal)
                    ? PmsJson.Expand(value[1..], values)
                    : value;

                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, value);
            }
        }

        private static HttpContent BuildContent(OperationOptions op, IReadOnlyDictionary<string, string> values)
        {
            var shaped = Shape(op.Body, values);

            // Без UnsafeRelaxedJsonEscaping кириллица уезжает как \uXXXX.
            // Формально JSON верный, но некоторые PMS (и их WAF) с этим не
            // работают, а логи становятся нечитаемыми.
            var json = JsonSerializer.Serialize(shaped, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// Подстановка значений в тело запроса.
        ///
        /// Тип результата определяется форматом плейсхолдера, а не угадыванием:
        ///   "@{amount:0.00}"  → число 5000.00 (PMS ждёт number, а не строку);
        ///   "@{roomNo}"       → строка "305" (номер комнаты — идентификатор,
        ///                       приводить его к числу нельзя: PMS вернёт ошибку
        ///                       или потеряет ведущие нули в номере).
        ///
        /// Автоматическое приведение любой цифры к числу здесь было бы ловушкой,
        /// поэтому его нет.
        /// </summary>
        private static object Shape(object node, IReadOnlyDictionary<string, string> values)
        {
            switch (node)
            {
                case null:
                    return null;

                case string s:
                {
                    if (!s.StartsWith("@", StringComparison.Ordinal)) return s;

                    var template = s[1..];
                    var colon = template.IndexOf(':');
                    var format = colon >= 0 ? template[(colon + 1)..] : null;

                    var expanded = PmsJson.Expand(template, values);

                    // Числовой формат = явное намерение отдать число
                    if (!string.IsNullOrEmpty(format) &&
                        decimal.TryParse(expanded, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var num))
                        return num;

                    if (expanded == "null") return null;
                    return expanded;
                }

                case IDictionary<string, object> dict:
                {
                    var result = new Dictionary<string, object>(dict.Count);
                    foreach (var kv in dict) result[kv.Key] = Shape(kv.Value, values);
                    return result;
                }

                case System.Collections.IEnumerable list and not string:
                {
                    var items = new List<object>();
                    foreach (var item in list) items.Add(Shape(item, values));
                    return items;
                }

                default:
                    return node;
            }
        }

        private static HttpMethod ParseMethod(string method) =>
            (method ?? "POST").Trim().ToUpperInvariant() switch
            {
                "GET" => HttpMethod.Get,
                "PUT" => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                "PATCH" => new HttpMethod("PATCH"),
                _ => HttpMethod.Post
            };

        private static bool IsWriteMethod(HttpMethod method) =>
            method != HttpMethod.Get && method != HttpMethod.Delete;

        private static string Trunc(string s) =>
            string.IsNullOrEmpty(s) ? "" : s[..Math.Min(200, s.Length)];

        private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in pairs) dict[key] = value;
            return dict;
        }

        private static Result<T> Unsupported<T>(string operation) =>
            Result<T>.BusinessFailure(
                $"Подключённая PMS не поддерживает операцию «{operation}». " +
                "Опишите её в секции Pms:Generic.");
    }

    /// <summary>
    /// Разобранный ответ PMS: исход плюс разобранный корень.
    ///
    /// Держит JsonDocument, а не JsonElement: элемент — лишь проекция на
    /// документ, и обращение к нему после Dispose бросает
    /// ObjectDisposedException. Маппинг обязан происходить внутри using.
    /// </summary>
    internal readonly struct PmsResponse : IDisposable
    {
        private readonly JsonDocument _document;

        public bool Ok { get; }
        public string Error { get; }
        public bool SystemFailure { get; }

        private PmsResponse(bool ok, JsonDocument document, string error, bool systemFailure)
        {
            Ok = ok;
            _document = document;
            Error = error;
            SystemFailure = systemFailure;
        }

        /// <summary>Корень ответа. Валиден только пока жив сам PmsResponse.</summary>
        public JsonElement Root => _document?.RootElement ?? default;

        public static PmsResponse Success(JsonDocument document) =>
            new(true, document, null, false);

        public static PmsResponse Business(string error) => new(false, null, error, false);
        public static PmsResponse NotFound(string error) => new(false, null, error, false);
        public static PmsResponse Transport(string error) => new(false, null, error, true);

        public void Dispose() => _document?.Dispose();

        public Result<T> Failure<T>() => SystemFailure
            ? Result<T>.SystemFailure(Error)
            : Result<T>.BusinessFailure(Error);
    }

    /// <summary>Ответ PMS не соответствует описанным в конфиге путям.</summary>
    public class PmsMappingException : Exception
    {
        public PmsMappingException(string message) : base(message) { }
    }
}
