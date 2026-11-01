using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.Logus
{
    public class LogusApiOptions
    {
        public const string SectionName = "LogusApi";
        public string BaseUrl { get; set; } = "https://api-hms.logus.pro/api";
        public string Login { get; set; }
        public string Password { get; set; }

        /// <summary>Токен для прямого использования (если не задан — получаем через Account endpoint).</summary>
        public string AuthToken { get; set; }

        /// <summary>
        /// Использовать прокси для подключения к API.
        /// По умолчанию false — прямое подключение.
        /// </summary>
        public bool UseProxy { get; set; } = false;

        /// <summary>
        /// Адрес прокси-сервера (если UseProxy = true).
        /// Пример: "http://192.168.1.1:8080"
        /// </summary>
        public string ProxyUrl { get; set; }

        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Идентификатор объекта (отеля) в Logus — используется в QuickSearch.
        /// У Реджисты в рабочем конфиге: pms.client.logus.constants.property-id = 1.
        /// </summary>
        public int PropertyId { get; set; } = 1;

        /// <summary>
        /// Код транзакции оплаты в Logus для проведения платежей киоска
        /// (POST /api/Reservation/AddPayment). У Реджисты хранится в БД
        /// (PaytypeTerminalLogus) — у нас в конфиге. Если пуст — постинг
        /// оплаты в PMS отключён (платёж остаётся только в банке).
        /// </summary>
        public string PaymentTransactionCode { get; set; }

        /// <summary>
        /// Словарь кодов причин отмены брони для Logus WebAPI.
        /// </summary>
        public Dictionary<string, string> CancellationReasonCodes { get; set; } = new()
        {
            ["Compensation"] = "Compensation",
            ["GuestRequest"] = "GuestRequest",
            ["SystemError"] = "SystemError",
            ["Overbooking"] = "Overbooking"
        };

        /// <summary>
        /// Передавать паспортные данные гостя в Logus при заселении
        /// (GuestProfile + UpdateGuests, контракт Реджисты).
        /// Отключите, пока эндпоинты не подтверждены на вашем сервере.
        /// </summary>
        public bool SendGuestDocuments { get; set; } = true;

        /// <summary>Код типа документа в Logus. Значение из живого вызова Реджисты.</summary>
        public string DocumentTypeCode { get; set; } = "ПАСГРАРФ";

        /// <summary>Название типа документа в Logus.</summary>
        public string DocumentTypeName { get; set; } = "Паспорт гражданина Российской Федерации";
    }

    public class LogusApiService : ILogusService
    {
        private readonly HttpClient _http;
        private readonly ILogger<LogusApiService> _logger;
        private readonly LogusApiOptions _opts;

        // === Auth management ===
        private readonly SemaphoreSlim _authLock = new(1, 1);
        private string _authToken;
        private bool _authAttempted;
        private bool _useBasicAuth;

        public LogusApiService(HttpClient http, IOptions<LogusApiOptions> opts, ILogger<LogusApiService> logger)
        {
            _http = http;
            _opts = opts.Value;
            _logger = logger;
            _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);

            // Проверено живым API (2026-08): облачный контур (api-hms.logus.pro)
            // Basic-auth ОТКЛОНЯЕТ (401) — работает только токен из GET Account.
            // Локальный WebAPI принимает и токен, и Basic. Поэтому стратегия:
            // токен — основной способ (лениво при первом запросе), Basic —
            // запасной, если Account недоступен или токен в ответе не нашёлся.
            if (!string.IsNullOrEmpty(_opts.AuthToken))
            {
                _authToken = _opts.AuthToken;
                SetAuthHeader("Token " + _authToken);
                _authAttempted = true;
                _logger.LogInformation("Logus: авторизация по токену из конфигурации");
            }
            else if (!string.IsNullOrEmpty(_opts.Login))
            {
                _logger.LogInformation(
                    "Logus: логин/пароль заданы — токен будет запрошен через Account при первом запросе");
            }
            else
            {
                _logger.LogWarning("Logus: ни AuthToken, ни Login не заданы — запросы будут без авторизации");
            }
        }

        /// <summary>Запасная схема: Basic Auth (принимается локальным WebAPI).</summary>
        private void SetBasicAuthFallback()
        {
            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_opts.Login}:{_opts.Password ?? string.Empty}"));
            SetAuthHeader("Basic " + basic);
            _useBasicAuth = true;
            _logger.LogWarning("Logus: токен получить не удалось — откат к Basic Auth");
        }

        private void SetAuthHeader(string headerValue)
        {
            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", headerValue);
        }

        /// <summary>
        /// Получает токен через Account endpoint, если его ещё нет.
        /// Потокобезопасно: SemaphoreSlim гарантирует, что только один
        /// запрос авторизации выполняется одновременно.
        /// </summary>
        private async Task EnsureAuthenticatedAsync(CancellationToken ct)
        {
            // Basic-режим (запасной): заголовок уже выставлен, токен не требуется
            if (_useBasicAuth)
                return;

            // Уже есть токен — ничего не делаем
            if (!string.IsNullOrEmpty(_authToken) && Volatile.Read(ref _authAttempted))
                return;

            await _authLock.WaitAsync(ct);
            try
            {
                // Double-check после захвата блокировки
                if (_useBasicAuth ||
                    (!string.IsNullOrEmpty(_authToken) && Volatile.Read(ref _authAttempted)))
                    return;

                if (string.IsNullOrEmpty(_opts.Login))
                {
                    _logger.LogWarning("Logus: Login не задан — аутентификация невозможна");
                    return;
                }

                _logger.LogInformation("Logus: запрос токена через Account endpoint...");
                var url = $"Account?userName={Uri.EscapeDataString(_opts.Login)}" +
                          $"&password={Uri.EscapeDataString(_opts.Password ?? "")}";

                // Проверено живым API (2026-08): эндпоинт Account принимает ТОЛЬКО GET,
                // на POST отвечает 401. Раньше здесь был PostAsync — из-за этого токен
                // никогда не получался и все запросы падали с 401.
                using var resp = await _http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("Logus: не удалось получить токен. HTTP {Status}: {Body}",
                        (int)resp.StatusCode, body);
                    SetBasicAuthFallback();
                    _authAttempted = true;
                    return;
                }

                // Парсим ответ: ожидаем JSON с полем Token или просто строку
                string token = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("Token", out var t))
                        token = t.GetString();
                    else if (doc.RootElement.TryGetProperty("token", out var t2))
                        token = t2.GetString();
                }
                catch (JsonException)
                {
                    // Может быть просто строка токена. Но осторожно: если с 200
                    // прилетел HTML-контент или обёртка с ошибкой — это НЕ токен.
                    var candidate = body?.Trim('"', ' ', '\t', '\r', '\n');
                    if (!string.IsNullOrEmpty(candidate) &&
                        candidate.Length >= 8 && candidate.Length <= 512 &&
                        !candidate.Contains(' ') &&
                        !candidate.Contains('{') &&
                        !candidate.Contains('<'))
                    {
                        token = candidate;
                    }
                }

                if (!string.IsNullOrEmpty(token))
                {
                    _authToken = token;
                    SetAuthHeader("Token " + _authToken);
                    _logger.LogInformation("Logus: токен получен успешно");
                }
                else
                {
                    _logger.LogError("Logus: токен не найден в ответе: {Body}", body);
                    SetBasicAuthFallback();
                }

                _authAttempted = true;
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>
        /// Выполняет HTTP-запрос с автоматической повторной аутентификацией при 401/403.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithAuthAsync(
            HttpMethod method, string url, HttpContent content, CancellationToken ct)
        {
            await EnsureAuthenticatedAsync(ct);

            var request = new HttpRequestMessage(method, url) { Content = content };
            var resp = await _http.SendAsync(request, ct);

            // Если сервер вернул 401/403 — возможно, токен протух. Пробуем перелогиниться.
            // В Basic-режиме повторная авторизация не нужна (учётные данные фиксированы).
            if (!_useBasicAuth &&
                (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                 resp.StatusCode == System.Net.HttpStatusCode.Forbidden) &&
                !string.IsNullOrEmpty(_opts.Login))
            {
                _logger.LogWarning("Logus: получен {Status} — сбрасываем токен и перелогиниваемся",
                    (int)resp.StatusCode);

                // Сбрасываем токен и флаг
                _authToken = null;
                _authAttempted = false;

                // Пробуем заново
                await EnsureAuthenticatedAsync(ct);

                if (!string.IsNullOrEmpty(_authToken))
                {
                    // Первый ответ больше не нужен: без Dispose соединение
                    // висит до сборки мусора.
                    resp.Dispose();
                    request = new HttpRequestMessage(method, url) { Content = content };
                    resp = await _http.SendAsync(request, ct);
                }
            }

            return resp;
        }

        private async Task<HttpResponseMessage> PostWithAuthAsync(
            string url, HttpContent content, CancellationToken ct)
        {
            return await SendWithAuthAsync(HttpMethod.Post, url, content, ct);
        }

        // ====================================================================
        //  AuthenticateSelfCheckin — поиск брони по номеру + фамилии
        // ====================================================================

        /// <summary>
        /// Реальные возможности боевого контура Logus. Совпадают с ILogusService:
        /// все операции WebAPI подтверждены живыми вызовами.
        /// SendGuestDocuments выключается конфигом — до подтверждения
        /// эндпоинтов на конкретном сервере.
        /// </summary>
        public PmsCapabilities Capabilities => new(
            Vendor: "logus",
            CanSearchReservations: true,
            CanCheckIn: true,
            CanCancel: true,
            CanPostPayment: !string.IsNullOrWhiteSpace(_opts.PaymentTransactionCode),
            CanListArrivals: true,
            CanSendGuestDocuments: _opts.SendGuestDocuments,
            CanReadReservationDetails: true);

        public async Task<Result<SelfCheckinInfo>> AuthenticateSelfCheckinAsync(
            string reservationNo, string confirmationMode, string key, CancellationToken ct)
        {
            try
            {
                var payload = new
                {
                    ReservationNo = reservationNo,
                    ConfirmationMode = confirmationMode,
                    Key = key
                };
                using var resp = await PostWithAuthAsync(
                    "SelfCheckin/Authenticate", JsonContent(payload), ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return Result<SelfCheckinInfo>.BusinessFailure("Бронь не найдена");
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var logusError = ParseLogusError(body);

                    // Проверено на живом API (2026-08): ненайденная бронь приходит
                    // как HTTP 500, а не 404. Два разных сообщения:
                    //   - облако при неверной фамилии: "Неверный параметр
                    //     подтверждения бронирования" (HttpException);
                    //   - локальный сервер: "Бронь с номером '...' не найдена".
                    // Ловим по тексту сообщения — Logus бросает HttpException
                    // и для других ошибок.
                    var notFoundMarkers = new[] { "не найдена", "неверный параметр подтверждения" };
                    var reasonMsg = (logusError?.ExceptionMessage ?? logusError?.Message) ?? string.Empty;
                    var isNotFound = logusError != null && Array.Exists(
                        notFoundMarkers,
                        m => reasonMsg.Contains(m, StringComparison.OrdinalIgnoreCase));
                    if (isNotFound)
                        return Result<SelfCheckinInfo>.BusinessFailure("Бронь не найдена");

                    // Настоящая причина лежит в ExceptionMessage; Message — общая фраза.
                    var errorMsg = logusError != null
                        ? $"{logusError.ExceptionMessage ?? logusError.Message} ({logusError.ExceptionType})"
                        : $"HTTP {(int)resp.StatusCode}";
                    return Result<SelfCheckinInfo>.BusinessFailure(
                        $"Logus отклонил запрос: {errorMsg}");
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var info = JsonSerializer.Deserialize<SelfCheckinInfo>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return info == null
                    ? Result<SelfCheckinInfo>.BusinessFailure("Пустой ответ Logus")
                    : Result<SelfCheckinInfo>.Success(info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой SelfCheckin/Authenticate");
                return Result<SelfCheckinInfo>.SystemFailure(ex.Message);
            }
        }

        // ====================================================================
        //  SelfCheckIn — заселение гостя + запись ключа
        // ====================================================================

        public async Task<Result<bool>> SelfCheckInAsync(
            string reservationNo, string roomNo, CancellationToken ct)
        {
            try
            {
                var payload = new { ReservationNo = reservationNo, RoomNo = roomNo };
                using var resp = await PostWithAuthAsync("SelfCheckin/CheckIn", JsonContent(payload), ct);

                if (resp.IsSuccessStatusCode) return Result<bool>.Success(true);

                var body = await resp.Content.ReadAsStringAsync(ct);
                var logusError = ParseLogusError(body);
                var errorMsg = logusError != null
                    ? $"{logusError.ExceptionMessage ?? logusError.Message} ({logusError.ExceptionType})"
                    : $"HTTP {(int)resp.StatusCode}";
                return Result<bool>.BusinessFailure($"Logus отклонил заселение: {errorMsg}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой SelfCheckin/CheckIn");
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        // === Устаревшие методы ===

        [Obsolete("Используйте AuthenticateSelfCheckinAsync — поиск по номеру брони + фамилии")]
        public Task<Result<ReservationData>> GetReservationByPassportAsync(
            string passportNumber, CancellationToken ct)
        {
            _logger.LogWarning(
                "GetReservationByPassportAsync вызван, но реальный Logus WebAPI не ищет " +
                "по паспорту — используйте AuthenticateSelfCheckinAsync (номер брони + фамилия).");
            return Task.FromResult(Result<ReservationData>.BusinessFailure(
                "Поиск по паспорту не поддерживается Logus WebAPI. " +
                "Используйте номер брони + способ подтверждения."));
        }

        // ====================================================================
        //  CancelReservation — отмена брони
        // ====================================================================

        public async Task<Result<bool>> CancelReservationAsync(
            string reservationId, string cancellationReasonCode, CancellationToken ct)
        {
            try
            {
                var url = $"Reservation/CancelReservations/{Uri.EscapeDataString(reservationId)}?cancelAll=true";
                if (!string.IsNullOrEmpty(cancellationReasonCode))
                {
                    var resolved = _opts.CancellationReasonCodes?.TryGetValue(cancellationReasonCode, out var mapped) == true
                        ? mapped : cancellationReasonCode;
                    url += $"&cancellationReasonCode={Uri.EscapeDataString(resolved)}";
                }
                using var resp = await PostWithAuthAsync(url, null, ct);

                if (resp.IsSuccessStatusCode) return Result<bool>.Success(true);

                var body = await resp.Content.ReadAsStringAsync(ct);
                var logusError = ParseLogusError(body);
                var errorMsg = logusError != null
                    ? $"{logusError.ExceptionMessage ?? logusError.Message} ({logusError.ExceptionType})"
                    : $"HTTP {(int)resp.StatusCode}";
                return Result<bool>.BusinessFailure($"Logus отклонил отмену: {errorMsg}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой Reservation/CancelReservations");
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        [Obsolete("Используйте SelfCheckInAsync или CancelReservationAsync вместо прямого обновления статуса")]
        public Task<Result<bool>> UpdateReservationStatusAsync(
            string reservationId, LogusReservationStatus status, CancellationToken ct)
        {
            _logger.LogWarning("UpdateReservationStatusAsync не реализован для реального API");
            return Task.FromResult(Result<bool>.BusinessFailure(
                "Прямое обновление статуса не подключено — используйте SelfCheckInAsync или CancelReservationAsync"));
        }

        // ====================================================================
        //  AddPayment — проведение оплаты в счёт брони
        //  Контракт сверен с LogusPMSClient Реджисты (addPayment):
        //  POST /api/Reservation/AddPayment
        //  { "payments": [{ "reservationGenericNo", "amount",
        //                   "transactionCode", "uniqueId", "comment" }] }
        // ====================================================================

        public async Task<Result<bool>> AddPaymentAsync(
            string reservationGenericNo, decimal amount, string bankTransactionId, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(_opts.PaymentTransactionCode))
                    return Result<bool>.BusinessFailure(
                        "Постинг оплаты в Logus отключён: не задан LogusApi:PaymentTransactionCode");

                var payload = new
                {
                    payments = new object[]
                    {
                        new
                        {
                            reservationGenericNo,
                            amount,
                            transactionCode = _opts.PaymentTransactionCode,
                            uniqueId = bankTransactionId,
                            comment = $"Киоск само заселения, банк. транзакция {bankTransactionId}"
                        }
                    }
                };

                using var resp = await PostWithAuthAsync("Reservation/AddPayment", JsonContent(payload), ct);
                if (resp.IsSuccessStatusCode) return Result<bool>.Success(true);

                var body = await resp.Content.ReadAsStringAsync(ct);
                var logusError = ParseLogusError(body);
                var errorMsg = logusError != null
                    ? $"{logusError.ExceptionMessage ?? logusError.Message} ({logusError.ExceptionType})"
                    : $"HTTP {(int)resp.StatusCode}";
                return Result<bool>.BusinessFailure($"Logus отклонил проведение оплаты: {errorMsg}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой Reservation/AddPayment");
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        // ====================================================================
        //  QuickSearch — заезды на дату
        //  Контракт сверен с LogusPMSClient Реджисты (getBookings):
        //  POST /api/Reservation/QuickSearch
        //  { "propertyId", "arrivalDateFrom": "yyyy-MM-dd", "arrivalDateTo": ... }
        //  Ответ: массив броней (genericNo, mainGuest, даты, суммы).
        // ====================================================================

        public async Task<Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>> GetArrivalsAsync(
            DateTime arrivalDate, CancellationToken ct)
        {
            try
            {
                var day = arrivalDate.ToString("yyyy-MM-dd");
                var payload = new
                {
                    propertyId = _opts.PropertyId,
                    arrivalDateFrom = day,
                    arrivalDateTo = day
                };

                using var resp = await PostWithAuthAsync("Reservation/QuickSearch", JsonContent(payload), ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var logusError = ParseLogusError(json);
                    var errorMsg = logusError != null
                        ? $"{logusError.ExceptionMessage ?? logusError.Message} ({logusError.ExceptionType})"
                        : $"HTTP {(int)resp.StatusCode}";
                    return Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>.BusinessFailure(
                        $"Logus отклонил поиск заездов: {errorMsg}");
                }

                // Корень может быть массивом или обёрткой — разбираем толерантно
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        root = items;
                    else if (root.TryGetProperty("bookings", out var bookings) && bookings.ValueKind == JsonValueKind.Array)
                        root = bookings;
                    else
                        return Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>.BusinessFailure(
                            "Неожиданный формат ответа QuickSearch");
                }

                if (root.ValueKind != JsonValueKind.Array)
                    return Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>.BusinessFailure(
                        "Неожиданный формат ответа QuickSearch");

                var result = new System.Collections.Generic.List<ArrivalInfo>();
                foreach (var el in root.EnumerateArray())
                {
                    string lastName = null, firstName = null;
                    if (el.TryGetProperty("mainGuest", out var guest) && guest.ValueKind == JsonValueKind.Object)
                    {
                        lastName = GetStringOrNull(guest, "lastName");
                        firstName = GetStringOrNull(guest, "firstName");
                    }

                    result.Add(new ArrivalInfo
                    {
                        GenericNo = GetStringOrNull(el, "genericNo"),
                        Guid = GetStringOrNull(el, "guid"),
                        LastName = lastName,
                        FirstName = firstName,
                        ArrivalDate = GetDateOrNull(el, "arrivalDate") ?? arrivalDate,
                        DepartureDate = GetDateOrNull(el, "departureDate") ?? arrivalDate,
                        StayAmount = GetDecimalOrNull(el, "localCurrencyStayAmount"),
                        Balance = GetDecimalOrNull(el, "localCurrencyBalance")
                    });
                }

                return Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>.Success(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой Reservation/QuickSearch");
                return Result<System.Collections.Generic.IReadOnlyList<ArrivalInfo>>.SystemFailure(ex.Message);
            }
        }

        // ====================================================================
        //  Гостевые документы: GuestProfile + UpdateGuests
        //  Контракт сверен с рабочим клиентом Реджисты (логи 2024-11-28):
        //    POST /api/GuestProfile                          — создать профиль
        //    POST /api/Reservation/{"{guid}"}/UpdateGuests   — привязать к брони
        // ====================================================================

        public async Task<Result<ReservationDetails>> GetReservationAsync(
            string reservationGenericNo, CancellationToken ct)
        {
            if (!_opts.SendGuestDocuments)
                return Result<ReservationDetails>.BusinessFailure(
                    "Передача паспортных данных в Logus отключена (LogusApi:SendGuestDocuments=false)");

            try
            {
                var url = $"Reservation/{Uri.EscapeDataString(reservationGenericNo)}";
                using var resp = await SendWithAuthAsync(HttpMethod.Get, url, null, ct);
                if (!resp.IsSuccessStatusCode)
                    return Result<ReservationDetails>.SystemFailure($"HTTP {(int)resp.StatusCode}");

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var details = ReservationDetails.FromJson(doc);

                return details == null
                    ? Result<ReservationDetails>.BusinessFailure(
                        "В ответе Logus не найден Guid брони")
                    : Result<ReservationDetails>.Success(details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой GET Reservation/{No}", reservationGenericNo);
                return Result<ReservationDetails>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<GuestProfileRef>> CreateGuestProfileAsync(
            GuestDocumentData guest, CancellationToken ct)
        {
            if (!_opts.SendGuestDocuments)
                return Result<GuestProfileRef>.BusinessFailure(
                    "Передача паспортных данных в Logus отключена (LogusApi:SendGuestDocuments=false)");

            try
            {
                string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

                var payload = new
                {
                    Status = "OPEN",
                    guest.LastName,
                    guest.FirstName,
                    guest.MiddleName,
                    Phones = (string)null,
                    CountryCode = guest.CountryCode,
                    DocumentData = new
                    {
                        DocumentTypeCode = _opts.DocumentTypeCode,
                        DocumentTypeName = _opts.DocumentTypeName,
                        DocumentSeries = guest.DocumentSeries ?? "",
                        DocumentNumber = guest.DocumentNumber ?? "",
                        IssueDate = Date(guest.IssueDate),
                        ExpirationDate = "",
                        DepartmentCode = guest.DepartmentCode ?? "",
                        IssuerInfo = guest.IssuedBy ?? "",
                        RegistrationDate = ""
                    },
                    guest.Sex,
                    BirthCity = guest.BirthPlace,
                    AccountType = "GP",
                    NoPost = false,
                    IsDeleted = false,
                    CountryName = (string)null,
                    Region = (string)null,
                    District = (string)null,
                    guest.City,
                    guest.Street,
                    guest.HouseNo,
                    BuildingNo = guest.BuildingNo ?? "",
                    FlatNo = guest.FlatNo ?? "",
                    Id = (string)null
                };

                using var resp = await PostWithAuthAsync("GuestProfile", JsonContent(payload), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    return Result<GuestProfileRef>.BusinessFailure(
                        $"GuestProfile отклонён: HTTP {(int)resp.StatusCode} {TruncateBody(body)}");
                }

                // Ответ толерантно: ищем personId и profileGuid в разных вариантах полей.
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string Find(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                    }
                    return null;
                }

                var profileRef = new GuestProfileRef
                {
                    PersonId = Find("Id", "id", "PersonId", "personId"),
                    ProfileGuid = Find("Guid", "guid", "ProfileGuid", "profileGuid")
                };

                if (string.IsNullOrEmpty(profileRef.PersonId) &&
                    string.IsNullOrEmpty(profileRef.ProfileGuid))
                {
                    return Result<GuestProfileRef>.BusinessFailure(
                        $"В ответе GuestProfile нет идентификаторов: {TruncateBody(json)}");
                }

                return Result<GuestProfileRef>.Success(profileRef);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой POST GuestProfile");
                return Result<GuestProfileRef>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<bool>> UpdateReservationGuestsAsync(
            string reservationGuid, GuestDocumentData mainGuest,
            GuestProfileRef profile, CancellationToken ct)
        {
            if (!_opts.SendGuestDocuments)
                return Result<bool>.BusinessFailure(
                    "Передача паспортных данных в Logus отключена (LogusApi:SendGuestDocuments=false)");

            try
            {
                string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

                var payload = new
                {
                    Guid = reservationGuid,
                    MainGuest = (string)null,
                    ReservationGuests = new object[]
                    {
                        new
                        {
                            Guid = (string)null,
                            MainProfileGuid = profile?.ProfileGuid,
                            Id = profile?.PersonId,
                            Email = (string)null,
                            mainGuest.FirstName,
                            mainGuest.MiddleName,
                            mainGuest.LastName,
                            GenericNo = (string)null,
                            Phone = (string)null,
                            BirthDate = Date(mainGuest.BirthDate),
                            mainGuest.Sex,
                            CountryCode = mainGuest.CountryCode,
                            CitizenshipCountryCode = mainGuest.CountryCode,
                            BirthCountryCode = "",
                            BirthCity = mainGuest.BirthPlace,
                            DocumentData = new
                            {
                                DocumentTypeCode = _opts.DocumentTypeCode,
                                DocumentTypeName = _opts.DocumentTypeName,
                                DocumentSeries = mainGuest.DocumentSeries ?? "",
                                DocumentNumber = mainGuest.DocumentNumber ?? "",
                                IssueDate = Date(mainGuest.IssueDate),
                                ExpirationDate = "",
                                DepartmentCode = mainGuest.DepartmentCode ?? "",
                                IssuerInfo = mainGuest.IssuedBy ?? "",
                                RegistrationDate = ""
                            },
                            CountryName = (string)null,
                            Region = (string)null,
                            District = (string)null,
                            mainGuest.City,
                            mainGuest.Street,
                            mainGuest.HouseNo,
                            BuildingNo = mainGuest.BuildingNo ?? "",
                            FlatNo = mainGuest.FlatNo ?? "",
                            Zip = "",
                            ActualArrivalDate = (string)null,
                            ActualDepartureDate = (string)null
                        }
                    }
                };

                var url = $"Reservation/{Uri.EscapeDataString(reservationGuid)}/UpdateGuests";
                using var resp = await PostWithAuthAsync(url, JsonContent(payload), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    return Result<bool>.BusinessFailure(
                        $"UpdateGuests отклонён: HTTP {(int)resp.StatusCode} {TruncateBody(body)}");
                }

                return Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой POST Reservation/{Guid}/UpdateGuests", reservationGuid);
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        /// <summary>
        /// Обрезка тела ответа для сообщения об ошибке.
        /// Раньше здесь была опечатка: при длине больше 200 возвращалась вся
        /// строка целиком, то есть обрезки не происходило. Длинный HTML-ответ
        /// Logus уходил в лог и в уведомление персоналу целиком.
        /// </summary>
        private static string TruncateBody(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 200 ? s : s[..200] + "…";
        }

        public async Task<bool> PingAsync(CancellationToken ct)
        {
            try
            {
                // Лёгкая проверка живости + авторизации: Account принимает GET
                var url = $"Account?userName={Uri.EscapeDataString(_opts.Login ?? "")}" +
                          $"&password={Uri.EscapeDataString(_opts.Password ?? "")}";
                using var resp = await _http.GetAsync(url, ct);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Logus ping не прошёл");
                return false;
            }
        }

        private static string GetStringOrNull(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;        private static DateTime? GetDateOrNull(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(v.GetString(), out var d) ? d : null;

        private static decimal? GetDecimalOrNull(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) &&
            (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String) &&
            decimal.TryParse(v.ToString(), out var d) ? d : null;

        private static StringContent JsonContent(object payload) =>
            new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        private static LogusErrorResponse ParseLogusError(string body)
        {
            try
            {
                return JsonSerializer.Deserialize<LogusErrorResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }
    }
}
