using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Infrastructure.Adapters.AlfaBank;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Интеграционные тесты AlfaBankPaymentService — проверяют формат HTTP-запросов
    /// и парсинг ответов через подставной HttpMessageHandler.
    /// Фейки (FakeAlfaBankService) не затронуты — юнит-тесты стейт-машины
    /// продолжают работать через интерфейс.
    /// </summary>
    public class AlfaBankPaymentServiceTests
    {
        private const string TestBase = "https://test.payment.alfabank.ru/payment/rest/";

        private static AlfaBankPaymentService CreateService(
            MockHttpHandler handler, QrPaymentOptions opts = null)
        {
            opts ??= new QrPaymentOptions
            {
                TestMode = true,
                TestBaseUrl = TestBase,
                BaseUrl = "https://payment.alfabank.ru/payment/rest/",
                Login = "test-login",
                Password = "test-pass",
                ReturnUrl = "https://kiosk.local/callback",
                QrLifetimeMinutes = 5,
                RequestTimeoutSeconds = 5,
            };

            var http = new HttpClient(handler) { BaseAddress = new Uri(TestBase) };
            return new AlfaBankPaymentService(
                http,
                Options.Create(opts),
                NullLogger<AlfaBankPaymentService>.Instance);
        }

        // ===== Helper: разбирает form-urlencoded тело запроса =====

        private static Dictionary<string, string> ParseForm(string body)
        {
            var d = new Dictionary<string, string>();
            foreach (var pair in body.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq >= 0)
                    d[Uri.UnescapeDataString(pair[..eq])] =
                        Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return d;
        }

        // ===== ProcessPaymentAsync =====

        [Fact]
        public async Task ProcessPayment_SendsCorrectRegisterAndQrRequests()
        {
            // register.do → sbp_c2b/get.do
            var handler = new MockHttpHandler();
            handler.Setup("register.do",
                "{\"orderId\":\"ORD-001\",\"errorCode\":0}");
            handler.Setup("sbp_c2b/get.do",
                "{\"errorCode\":0,\"payload\":\"sbp://pay?c=...\",\"renderedQr\":\"iVBORbase64png\"}");

            var svc = CreateService(handler);
            var result = await svc.ProcessPaymentAsync(1500m, "KEY-1", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal("ORD-001", result.Value.TransactionId);
            Assert.Null(result.Value.RRN);
            Assert.Equal("sbp://pay?c=...", result.Value.QrPayload);
            Assert.Equal("iVBORbase64png", result.Value.QrImageBase64);
            Assert.NotNull(result.Value.QrExpiresAt);

            // Проверяем запрос register.do
            Assert.Equal(2, handler.Requests.Count);
            var regForm = ParseForm(handler.Requests[0].Body);
            Assert.Equal("test-login", regForm["userName"]);
            Assert.Equal("test-pass", regForm["password"]);
            Assert.Equal("KEY-1", regForm["orderNumber"]);
            Assert.Equal("150000", regForm["amount"]); // 1500 * 100
            Assert.Equal("643", regForm["currency"]);
            Assert.Equal("https://kiosk.local/callback", regForm["returnUrl"]);
            Assert.Contains("SBP", regForm["jsonParams"]);

            // Проверяем запрос sbp_c2b/get.do
            var qrForm = ParseForm(handler.Requests[1].Body);
            Assert.Equal("test-login", qrForm["userName"]);
            Assert.Equal("test-pass", qrForm["password"]);
            Assert.Equal("ORD-001", qrForm["mdOrder"]);
        }

        [Fact]
        public async Task ProcessPayment_RegisterFails_ReturnsBusinessFailure()
        {
            var handler = new MockHttpHandler();
            handler.Setup("register.do",
                "{\"errorCode\":1,\"errorMessage\":\"Invalid order number\"}");

            var svc = CreateService(handler);
            var result = await svc.ProcessPaymentAsync(5000m, "BAD", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("Банк отклонил", result.Error);
            Assert.Contains("Invalid order number", result.Error);
        }

        [Fact]
        public async Task ProcessPayment_QrFails_ReturnsBusinessFailure()
        {
            var handler = new MockHttpHandler();
            handler.Setup("register.do",
                "{\"orderId\":\"ORD-002\",\"errorCode\":0}");
            handler.Setup("sbp_c2b/get.do",
                "{\"errorCode\":7,\"errorMessage\":\"QR generation failed\"}");

            var svc = CreateService(handler);
            var result = await svc.ProcessPaymentAsync(5000m, "KEY-2", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("Не удалось получить QR", result.Error);
        }

        // ===== CheckPaymentStatusAsync =====

        [Fact]
        public async Task CheckStatus_Deposited_ReturnsConfirmed()
        {
            var handler = new MockHttpHandler();
            handler.Setup("sbp_c2b/status.do",
                "{\"transactionState\":\"DEPOSITED\",\"rrn\":\"RRN-999\"}");

            var svc = CreateService(handler);
            var result = await svc.CheckPaymentStatusAsync("ORD-001", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value.Confirmed);
            Assert.Equal("RRN-999", result.Value.RRN);

            var form = ParseForm(handler.Requests[0].Body);
            Assert.Equal("test-login", form["userName"]);
            Assert.Equal("ORD-001", form["mdOrder"]);
        }

        [Fact]
        public async Task CheckStatus_Pending_ReturnsNotConfirmed()
        {
            var handler = new MockHttpHandler();
            handler.Setup("sbp_c2b/status.do",
                "{\"transactionState\":\"PENDING\"}");

            var svc = CreateService(handler);
            var result = await svc.CheckPaymentStatusAsync("ORD-001", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.False(result.Value.Confirmed);
            Assert.Null(result.Value.RRN);
        }

        // ===== RefundPaymentAsync =====

        [Fact]
        public async Task Refund_SendsRefundDo_ReturnsSuccess()
        {
            var handler = new MockHttpHandler();
            handler.Setup("refund.do", "{\"errorCode\":0}");

            var svc = CreateService(handler);
            var result = await svc.RefundPaymentAsync("ORD-001", 1500m, CancellationToken.None);

            Assert.True(result.IsSuccess);

            var form = ParseForm(handler.Requests[0].Body);
            Assert.Equal("test-login", form["userName"]);
            Assert.Equal("test-pass", form["password"]);
            Assert.Equal("ORD-001", form["orderId"]);
            Assert.Equal("150000", form["amount"]);
        }

        [Fact]
        public async Task Refund_BankRejects_ReturnsFailure()
        {
            var handler = new MockHttpHandler();
            handler.Setup("refund.do",
                "{\"errorCode\":5,\"errorMessage\":\"Refund not allowed\"}");

            var svc = CreateService(handler);
            var result = await svc.RefundPaymentAsync("ORD-001", 1500m, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("Возврат отклонён", result.Error);
        }

        // ===== CancelUnpaidOrderAsync =====

        [Fact]
        public async Task CancelUnpaid_SendsRejectDo_ReturnsSuccess()
        {
            var handler = new MockHttpHandler();
            handler.Setup("sbp_c2b/reject.do", "{\"errorCode\":0}");

            var svc = CreateService(handler);
            var result = await svc.CancelUnpaidOrderAsync("ORD-001", CancellationToken.None);

            Assert.True(result.IsSuccess);

            var form = ParseForm(handler.Requests[0].Body);
            Assert.Equal("test-login", form["userName"]);
            Assert.Equal("test-pass", form["password"]);
            Assert.Equal("ORD-001", form["mdOrder"]);
        }

        // ===== TestMode: использует TestBaseUrl =====

        [Fact]
        public async Task Constructor_TestMode_UsesTestBaseUrl()
        {
            var handler = new MockHttpHandler();
            handler.Setup("register.do",
                "{\"orderId\":\"ORD-T\",\"errorCode\":0}");
            handler.Setup("sbp_c2b/get.do",
                "{\"errorCode\":0,\"payload\":\"sbp://...\",\"renderedQr\":\"png\"}");

            var opts = new QrPaymentOptions
            {
                TestMode = true,
                TestBaseUrl = "https://test.payment.alfabank.ru/payment/rest/",
                BaseUrl = "https://payment.alfabank.ru/payment/rest/",
                Login = "L", Password = "P",
                ReturnUrl = "https://kiosk.local/cb",
                RequestTimeoutSeconds = 5,
            };

            var http = new HttpClient(handler) { BaseAddress = new Uri(opts.TestBaseUrl) };
            var svc = new AlfaBankPaymentService(
                http, Options.Create(opts), NullLogger<AlfaBankPaymentService>.Instance);

            var result = await svc.ProcessPaymentAsync(100m, "K", CancellationToken.None);

            Assert.True(result.IsSuccess);
            // Запрос ушёл на правильный базовый URL (verify via handler)
            Assert.StartsWith("https://test.payment.alfabank.ru",
                handler.Requests[0].Url.ToString());
        }

        // ===== MockHttpHandler =====

        private class MockHttpHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, string> _responses = new();
            public List<MockRequest> Requests { get; } = new();

            public void Setup(string pathEnd, string jsonResponse)
                => _responses[pathEnd] = jsonResponse;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
                Requests.Add(new MockRequest(request.RequestUri ?? new Uri("http://x"),
                    request.Method, body));

                foreach (var kv in _responses)
                {
                    if (request.RequestUri?.AbsoluteUri.EndsWith(kv.Key) == true)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new StringContent(kv.Value) });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    { Content = new StringContent("{\"errorCode\":-1,\"errorMessage\":\"Not mocked\"}") });
            }
        }

        private record MockRequest(Uri Url, HttpMethod Method, string Body);
    }
}
