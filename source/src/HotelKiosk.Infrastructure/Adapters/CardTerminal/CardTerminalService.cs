using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.CardTerminal
{
    /// <summary>
    /// Настройки локального агента пинпада (секция CardTerminal).
    /// </summary>
    public class CardTerminalOptions
    {
        public const string SectionName = "CardTerminal";

        /// <summary>Оплата картой включена. false — бридж вернёт отказ без запросов.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Адрес локального агента терминала на киоск-ПК.</summary>
        public string BaseUrl { get; set; } = "http://localhost:8090";

        /// <summary>Путь списания (POST).</summary>
        public string ChargePath { get; set; } = "/payment/charge";

        /// <summary>Путь статуса операции (GET, orderId в query).</summary>
        public string StatusPath { get; set; } = "/payment/status";

        /// <summary>Путь возврата (POST).</summary>
        public string RefundPath { get; set; } = "/payment/refund";

        /// <summary>Путь health-check.</summary>
        public string HealthPath { get; set; } = "/health";

        /// <summary>
        /// Таймаут HTTP-запроса. Списание — длинная операция (гость вводит PIN),
        /// держим запас относительно времени ответа агента.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 90;

        /// <summary>Интервал опроса статуса PENDING-операции.</summary>
        public int PollIntervalSeconds { get; set; } = 3;
    }

    /// <summary>
    /// Универсальный HTTP-адаптер к агенту пинпада на киоск-ПК.
    /// Контракт задан нами (см. ICardTerminalService) — любой банк подключается
    /// прослойкой-агентом, реализующей 4 эндпоинта. Разбор ответа толерантный:
    /// transactionId | transaction_id | txnId, статус в любом регистре.
    /// </summary>
    public class CardTerminalService : ICardTerminalService
    {
        private readonly HttpClient _http;
        private readonly ILogger<CardTerminalService> _logger;
        private readonly CardTerminalOptions _opts;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public CardTerminalService(
            HttpClient http, IOptions<CardTerminalOptions> opts, ILogger<CardTerminalService> logger)
        {
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _http = http;

            _http.BaseAddress = new Uri(_opts.BaseUrl);
            _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        }

        public async Task<Result<PaymentResult>> ProcessCardPaymentAsync(
            decimal amount, string orderId, CancellationToken ct)
        {
            if (!_opts.Enabled)
                return Result<PaymentResult>.BusinessFailure(
                    "Оплата картой отключена (CardTerminal:Enabled=false)");

            try
            {
                var payload = new { orderId, amount };
                var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                _logger.LogInformation("Пинпад: списание {Amount} ₽, orderId={OrderId}", amount, orderId);

                using var resp = await _http.PostAsync(_opts.ChargePath, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return Result<PaymentResult>.SystemFailure(
                        $"Терминал HTTP {(int)resp.StatusCode}: {Trunc(body)}");

                var dto = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CardOpResponse>(body, JsonOpts);

                if (dto == null)
                    return Result<PaymentResult>.SystemFailure($"Пустой ответ терминала: {Trunc(body)}");

                var status = NormalizeStatus(dto.Status);
                switch (status)
                {
                    case "CONFIRMED":
                        _logger.LogInformation("Пинпад: оплата {OrderId} подтверждена сразу", orderId);
                        return Result<PaymentResult>.Success(new PaymentResult(
                            Success: true,
                            TransactionId: dto.TransactionId ?? orderId,
                            RRN: dto.Rrn,
                            ErrorMessage: null));

                    case "DECLINED":
                        _logger.LogWarning("Пинпад: отказ по {OrderId}: {Error}", orderId, dto.Error);
                        return Result<PaymentResult>.BusinessFailure(
                            $"Оплата отклонена терминалом: {dto.Error ?? "без причины"}");

                    default: // PENDING или неизвестный статус — подтверждение через опрос
                        return Result<PaymentResult>.Success(new PaymentResult(
                            Success: true,
                            TransactionId: dto.TransactionId ?? orderId,
                            RRN: null,
                            ErrorMessage: null));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой обращения к пинпаду");
                return Result<PaymentResult>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string orderId, CancellationToken ct)
        {
            if (!_opts.Enabled)
                return Result<PaymentStatus>.SystemFailure("Оплата картой отключена");

            try
            {
                var url = $"{_opts.StatusPath}?orderId={Uri.EscapeDataString(orderId)}";
                using var resp = await _http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return Result<PaymentStatus>.SystemFailure(
                        $"Терминал HTTP {(int)resp.StatusCode}: {Trunc(body)}");

                var dto = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CardOpResponse>(body, JsonOpts);

                if (dto == null)
                    return Result<PaymentStatus>.SystemFailure("Пустой ответ статуса терминала");

                var confirmed = NormalizeStatus(dto.Status) == "CONFIRMED";
                return Result<PaymentStatus>.Success(
                    new PaymentStatus(confirmed, dto.Rrn, dto.TransactionId ?? orderId,
                        ParseAmount(dto.Amount)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой статуса пинпада для {OrderId}", orderId);
                return Result<PaymentStatus>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<bool>> RefundPaymentAsync(
            string transactionId, decimal amount, CancellationToken ct)
        {
            if (!_opts.Enabled)
                return Result<bool>.BusinessFailure("Оплата картой отключена");

            try
            {
                var payload = new { transactionId, amount };
                var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var resp = await _http.PostAsync(_opts.RefundPath, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return Result<bool>.BusinessFailure(
                        $"Возврат отклонён терминалом: HTTP {(int)resp.StatusCode} {Trunc(body)}");

                // Статус разбираем так же, как в ProcessCardPaymentAsync: агент
                // может ответить 200 с {"status":"DECLINED"}. Раньше тело не
                // читалось вовсе, и отказ терминала считался успешным возвратом:
                // деньги у гостя не возвращались, а компенсация помечалась
                // выполненной и не повторялась.
                if (!string.IsNullOrWhiteSpace(body) && LooksLikeJson(body))
                {
                    CardOpResponse dto = null;
                    try
                    {
                        dto = JsonSerializer.Deserialize<CardOpResponse>(body, JsonOpts);
                    }
                    catch (JsonException)
                    {
                        dto = null;
                    }

                    if (dto?.Status != null)
                    {
                        var status = NormalizeStatus(dto.Status);
                        if (status == "DECLINED")
                        {
                            _logger.LogWarning(
                                "Пинпад отклонил возврат {TxId}: {Error}", transactionId, dto.Error);
                            return Result<bool>.BusinessFailure(
                                $"Возврат отклонён терминалом: {dto.Error ?? "без причины"}");
                        }

                        if (status != "CONFIRMED")
                        {
                            // PENDING — возврат ещё не завершён, но и не отказан
                            _logger.LogInformation(
                                "Пинпад вернул статус {Status} по возврату {TxId}", status, transactionId);
                            return Result<bool>.BusinessFailure(
                                $"Терминал не подтвердил возврат (статус {status}). " +
                                "Требуется проверка на стойке.");
                        }
                    }
                }

                _logger.LogInformation("Пинпад: возврат {TxId} на {Amount} руб. подтверждён",
                    transactionId, amount);
                return Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой возврата через пинпад {TxId}", transactionId);
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        public async Task<bool> PingAsync(CancellationToken ct)
        {
            if (!_opts.Enabled) return false;
            try
            {
                using var resp = await _http.GetAsync(_opts.HealthPath, ct);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Пинпад health-check не прошёл");
                return false;
            }
        }

        private static string NormalizeStatus(string s) =>
            string.IsNullOrWhiteSpace(s) ? "PENDING" : s.Trim().ToUpperInvariant();

        private static bool LooksLikeJson(string body)
        {
            var trimmed = body?.TrimStart();
            return !string.IsNullOrEmpty(trimmed) && (trimmed[0] == '{' || trimmed[0] == '[');
        }

        /// <summary>
        /// Сумма из ответа агента терминала. Поле может прийти числом или
        /// строкой; отсутствие поля — не ошибка: сверка тогда пропускается.
        /// </summary>
        private static decimal? ParseAmount(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var number))
                return number;

            if (el.ValueKind == JsonValueKind.String &&
                decimal.TryParse(el.GetString(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var text))
                return text;

            return null;
        }

        /// <summary>
        /// Обрезка ответа агента для сообщения об ошибке.
        /// Раньше здесь была опечатка: при длине больше 200 возвращалась
        /// вся строка целиком, так что обрезки не происходило вовсе.
        /// </summary>
        private static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 200 ? s : s[..200] + "…";
        }

        private class CardOpResponse
        {
            /// <summary>CONFIRMED | PENDING | DECLINED (любой регистр).</summary>
            public string Status { get; set; }

            [JsonPropertyName("transactionId")]
            public string TransactionIdDirect { get; set; }

            [JsonPropertyName("transaction_id")]
            public string TransactionIdSnake { get; set; }

            [JsonPropertyName("txnId")]
            public string TxnId { get; set; }

            [JsonIgnore]
            public string TransactionId => TransactionIdDirect ?? TransactionIdSnake ?? TxnId;

            public string Rrn { get; set; }
            public string Error { get; set; }

            /// <summary>Сумма операции; агент может не прислать поле.</summary>
            public JsonElement Amount { get; set; }
        }
    }
}
