using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.AlfaBank
{
    /// <summary>
    /// Оплата через СБП QR (эквайринг Альфа-Банк, платёжный шлюз «Роутер»).
    ///
    /// Поток:
    ///   1) register.do — регистрация заказа с paymentType=SBP.
    ///   2) sbp_c2b/get.do — получение payload и base64-PNG QR-кода.
    ///   3) sbp_c2b/status.do — поллинг статуса (transactionState: DEPOSITED = оплачено).
    ///   4) refund.do — возврат после списания; sbp_c2b/reject.do — отмена неоплаченного.
    ///
    /// Все запросы — form-urlencoded (application/x-www-form-urlencoded).
    /// Авторизация — userName + password в теле каждого запроса.
    /// </summary>
    public class AlfaBankPaymentService : IAlfaBankService
    {
        private readonly HttpClient _http;
        private readonly ILogger<AlfaBankPaymentService> _logger;
        private readonly QrPaymentOptions _opts;

        public AlfaBankPaymentService(
            HttpClient http, IOptions<QrPaymentOptions> opts, ILogger<AlfaBankPaymentService> logger)
        {
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _http = http;

            var baseUrl = _opts.TestMode ? _opts.TestBaseUrl : _opts.BaseUrl;
            _http.BaseAddress = new Uri(baseUrl);
            _http.Timeout = TimeSpan.FromSeconds(_opts.RequestTimeoutSeconds);
        }

        public async Task<Result<PaymentResult>> ProcessPaymentAsync(
            decimal amount, string idempotencyKey, CancellationToken ct)
        {
            // Без учётных данных банк отвергнет register.do с обезличенной ошибкой —
            // гость при этом уже смотрит на экран оплаты. Ловим это до запроса.
            if (string.IsNullOrWhiteSpace(_opts.Login) || string.IsNullOrWhiteSpace(_opts.Password))
            {
                _logger.LogError(
                    "Оплата по QR недоступна: не заданы AlfaBankQr:Login / AlfaBankQr:Password");
                return Result<PaymentResult>.BusinessFailure(
                    "Оплата по QR не настроена: отсутствуют учётные данные эквайринга Альфа-Банка");
            }

            try
            {
                // Шаг 1: регистрация заказа.
                var registerParams = new Dictionary<string, string>
                {
                    ["userName"] = _opts.Login,
                    ["password"] = _opts.Password,
                    ["orderNumber"] = idempotencyKey,
                    ["amount"] = ((long)(amount * 100)).ToString(),
                    ["currency"] = "643",
                    ["returnUrl"] = _opts.ReturnUrl,
                    ["jsonParams"] = JsonSerializer.Serialize(new
                    {
                        paymentType = "SBP",
                        sbpType = "QR"
                    }),
                };

                var regBody = await PostAsync("register.do", registerParams, ct);
                var reg = Deserialize<AlfaRegisterResponse>(regBody);

                if (reg == null || reg.ErrorCode != 0)
                {
                    _logger.LogWarning(
                        "Альфа-Банк отклонил регистрацию заказа: {Code} {Msg}",
                        reg?.ErrorCode, reg?.ErrorMessage);
                    return Result<PaymentResult>.BusinessFailure(
                        $"Банк отклонил заказ: код {reg?.ErrorCode}, {reg?.ErrorMessage}");
                }

                // Шаг 2: получение QR-кода для зарегистрированного заказа.
                var qrParams = new Dictionary<string, string>
                {
                    ["userName"] = _opts.Login,
                    ["password"] = _opts.Password,
                    ["mdOrder"] = reg.OrderId,
                };

                var qrBody = await PostAsync("sbp_c2b/get.do", qrParams, ct);
                var qr = Deserialize<AlfaQrResponse>(qrBody);

                if (qr == null || qr.ErrorCode != 0)
                {
                    _logger.LogWarning(
                        "Не удалось получить QR для заказа {OrderId}: {Code} {Msg}",
                        reg.OrderId, qr?.ErrorCode, qr?.ErrorMessage);
                    return Result<PaymentResult>.BusinessFailure(
                        $"Не удалось получить QR: {qr?.ErrorMessage}");
                }

                var expiresAt = DateTime.UtcNow.AddMinutes(_opts.QrLifetimeMinutes);

                // Success=true = "QR готов к показу", RRN=null —
                // подтверждение оплаты придёт через CheckPaymentStatusAsync.
                return Result<PaymentResult>.Success(new PaymentResult(
                    Success: true,
                    TransactionId: reg.OrderId,
                    RRN: null,
                    ErrorMessage: null,
                    QrPayload: qr.Payload,
                    QrImageBase64: qr.RenderedQr,
                    QrExpiresAt: expiresAt));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой создания QR-заказа");
                return Result<PaymentResult>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string transactionId, CancellationToken ct)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["userName"] = _opts.Login,
                    ["password"] = _opts.Password,
                    ["mdOrder"] = transactionId,
                };

                var body = await PostAsync("sbp_c2b/status.do", parameters, ct);
                var dto = Deserialize<AlfaStatusResponse>(body);

                if (dto == null)
                    return Result<PaymentStatus>.SystemFailure("Пустой ответ банка при проверке статуса");

                var confirmed = string.Equals(dto.TransactionState, "DEPOSITED", StringComparison.OrdinalIgnoreCase);
                return Result<PaymentStatus>.Success(
                    new PaymentStatus(confirmed, dto.Rrn, transactionId, ParseAmount(dto.Amount)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой проверки статуса QR-заказа");
                return Result<PaymentStatus>.SystemFailure(ex.Message);
            }
        }

        public async Task<Result<bool>> RefundPaymentAsync(
            string transactionId, decimal amount, CancellationToken ct)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["userName"] = _opts.Login,
                    ["password"] = _opts.Password,
                    ["orderId"] = transactionId,
                    ["amount"] = ((long)(amount * 100)).ToString(),
                };

                var body = await PostAsync("refund.do", parameters, ct);
                var dto = Deserialize<AlfaRefundResponse>(body);

                if (dto == null || dto.ErrorCode != 0)
                    return Result<bool>.BusinessFailure($"Возврат отклонён: {dto?.ErrorMessage}");

                return Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой возврата по QR-заказу");
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        /// <summary>
        /// Отменить ещё не оплаченный SBP-заказ (гость не отсканировал QR).
        /// </summary>
        public async Task<Result<bool>> CancelUnpaidOrderAsync(string orderId, CancellationToken ct)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["userName"] = _opts.Login,
                    ["password"] = _opts.Password,
                    ["mdOrder"] = orderId,
                };

                var body = await PostAsync("sbp_c2b/reject.do", parameters, ct);
                var dto = Deserialize<AlfaRefundResponse>(body);

                if (dto == null || dto.ErrorCode != 0)
                {
                    _logger.LogWarning(
                        "Не удалось отменить неоплаченный заказ {OrderId}: {Code} {Msg}",
                        orderId, dto?.ErrorCode, dto?.ErrorMessage);
                    return Result<bool>.BusinessFailure(
                        $"Не удалось отменить заказ: {dto?.ErrorMessage}");
                }

                return Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой отмены неоплаченного QR-заказа {OrderId}", orderId);
                return Result<bool>.SystemFailure(ex.Message);
            }
        }

        #region HTTP helpers

        private async Task<string> PostAsync(
            string path, Dictionary<string, string> parameters, CancellationToken ct)
        {
            using var resp = await _http.PostAsync(path, new FormUrlEncodedContent(parameters), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Банк вернул HTTP {Status} для {Path}: {Body}",
                    (int)resp.StatusCode, path, body);
            }

            return body;
        }

        private static T Deserialize<T>(string body) where T : class =>
            string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<T>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        /// <summary>
        /// Сумма из ответа банка. Поле может прийти числом или строкой;
        /// отсутствие поля — не ошибка: тогда сверка суммы просто пропускается.
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

        #endregion

        #region DTO (ответы платёжного шлюза «Роутер»)

        private class AlfaRegisterResponse
        {
            public int ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            public string OrderId { get; set; }
        }

        private class AlfaQrResponse
        {
            public int ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            /// <summary>СБП-payload (sbp://...).</summary>
            public string Payload { get; set; }
            /// <summary>Base64-PNG изображение QR-кода.</summary>
            public string RenderedQr { get; set; }
        }

        private class AlfaStatusResponse
        {
            public string TransactionState { get; set; }
            public string Rrn { get; set; }

            /// <summary>Сумма операции; банк может не прислать поле.</summary>
            public JsonElement Amount { get; set; }
        }

        private class AlfaRefundResponse
        {
            public int ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
        }

        #endregion
    }
}
