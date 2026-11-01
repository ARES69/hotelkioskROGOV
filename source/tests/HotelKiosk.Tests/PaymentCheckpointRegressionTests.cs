using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Application.Services;
using HotelKiosk.Core.Models;
using HotelKiosk.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Регрессии, найденные ревью: подтверждение оплаты терялось, а неудачный
    /// возврат стирал транзакцию. Обе ошибки стоили денег гостю и были
    /// не видны в тестах — закрываем здесь.
    /// </summary>
    public class PaymentCheckpointRegressionTests
    {
        private readonly FakeAlfaBankService _alfa = new();
        private readonly FakeLogusService _pms = new();
        private readonly FakeCheckInSessionStore _store = new();

        private KioskActionAdapter Create() => new(
            new FakeRegulaService(), _pms, new FakeVisionLineService(),
            _alfa, new FakeCardTerminalService(), new FakeNotificationService(),
            _store, NullLogger<KioskActionAdapter>.Instance);

        private const string Session = "session-A";

        private static System.Text.Json.JsonElement Payload(string json) =>
            System.Text.Json.JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task PaymentConfirmed_PersistsCheckpointImmediately()
        {
            // Раньше подтверждение меняло только поле в памяти: на диск попадало
            // лишь при следующем шаге. Падение между оплатой и заселением
            // оставляло «возврат не нужен» при списанных деньгах.
            //
            // NoRrn — QR без синхронного подтверждения: деньги списываются
            // позже, через опрос статуса. Именно этот сценарий и терялся.
            _alfa.NoRrn = true;
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), Session, 1, CancellationToken.None);

            _alfa.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), Session, 1, CancellationToken.None);

            // До подтверждения статус платежа
            var before = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(before);
            Assert.False(before.PaymentConfirmed);
            Assert.False(before.RequiresCompensation);

            // Подтверждение пришло в payment.status — и обязано попасть на диск
            await a.ExecuteAsync("payment.status",
                Payload("{\"orderId\":\"TX-1\"}"), Session, 1, CancellationToken.None);

            var after = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(after);
            Assert.True(after.PaymentConfirmed, "подтверждение оплаты не записано в чекпоинт");
            Assert.Equal(5000m, after.PaidAmount);
            Assert.True(after.RequiresCompensation);
        }

        [Fact]
        public async Task PaymentNotConfirmed_KeepsCheckpointWithoutRefundDemand()
        {
            _alfa.NoRrn = true;   // подтверждения нет даже после опроса
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), Session, 1, CancellationToken.None);

            _alfa.StatusConfirmed = false;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), Session, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.status",
                Payload("{\"orderId\":\"TX-1\"}"), Session, 1, CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.False(saved.PaymentConfirmed);
            Assert.False(saved.RequiresCompensation);
        }

        [Fact]
        public async Task FailedRefund_KeepsTransactionForManualRetry()
        {
            // Раньше при неудачном возврате _payTxId обнулялся: компенсация
            // становилась невозможной, а повторная отмена рапортовала «отменено».
            _alfa.StatusConfirmed = true;
            _alfa.NoRrn = true;          // QR: подтверждение придёт позже
            _alfa.FailRefund = true;

            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), Session, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), Session, 1, CancellationToken.None);

            var result = await a.ExecuteAsync("payment.cancel",
                Payload("{}"), Session, 1, CancellationToken.None);

            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.Contains("PAYMENT_REFUND_FAILED", json);

            // Повторная попытка должна снова дойти до банка, а не вернуть «ок»
            var retry = await a.ExecuteAsync("payment.cancel",
                Payload("{}"), Session, 1, CancellationToken.None);
            Assert.Contains("PAYMENT_REFUND_FAILED",
                System.Text.Json.JsonSerializer.Serialize(retry));
            Assert.Equal(2, _alfa.RefundCalls);
        }

        [Fact]
        public async Task SuccessfulRefund_ClearsTransaction_AndSecondCancelIsNoop()
        {
            _alfa.StatusConfirmed = true;
            _alfa.NoRrn = true;

            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), Session, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), Session, 1, CancellationToken.None);

            await a.ExecuteAsync("payment.cancel",
                Payload("{}"), Session, 1, CancellationToken.None);
            Assert.Equal(1, _alfa.RefundCalls);

            // Возврат уже сделан — второй раз не ходим в банк
            await a.ExecuteAsync("payment.cancel",
                Payload("{}"), Session, 1, CancellationToken.None);
            Assert.Equal(1, _alfa.RefundCalls);
        }

        [Fact]
        public async Task ZeroAmountBooking_ChecksInWithoutPayment()
        {
            _pms.PriceOverride = 0m;
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), Session, 1, CancellationToken.None);

            await a.ExecuteAsync("pms.checkin",
                Payload("{\"room\":\"101\"}"), Session, 1, CancellationToken.None);

            Assert.Equal(1, _pms.SelfCheckInCalls);
            Assert.Equal(0, _alfa.RefundCalls);
        }
    }

    /// <summary>
    /// Возврат через пинпад подтверждался любым HTTP 2xx без разбора тела:
    /// агент мог ответить 200 со статусом DECLINED, и компенсация считалась
    /// выполненной, а деньги гостю не возвращались.
    /// </summary>
    public class CardTerminalRefundParsingTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _body;
            private readonly HttpStatusCode _status;

            public StubHandler(string body, HttpStatusCode status)
            {
                _body = body;
                _status = status;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json")
                });
        }

        private static async Task<Core.Models.Result<bool>> RefundAsync(
            string responseBody, HttpStatusCode status = HttpStatusCode.OK)
        {
            var options = Options_CardTerminal();
            var http = new HttpClient(new StubHandler(responseBody, status))
            {
                BaseAddress = new Uri("http://terminal.local/")
            };

            var svc = new Infrastructure.Adapters.CardTerminal.CardTerminalService(
                http, Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<Infrastructure.Adapters.CardTerminal.CardTerminalService>.Instance);

            return await svc.RefundPaymentAsync("TX-1", 5000m, CancellationToken.None);
        }

        private static Infrastructure.Adapters.CardTerminal.CardTerminalOptions Options_CardTerminal() =>
            new Infrastructure.Adapters.CardTerminal.CardTerminalOptions
            {
                Enabled = true,
                BaseUrl = "http://terminal.local/",
                RefundPath = "/payment/refund"
            };

        [Fact]
        public async Task Refund_DeclinedInBody_IsNotSuccess()
        {
            // Регрессия: HTTP 200 + status DECLINED раньше считался возвратом
            var r = await RefundAsync("{\"status\":\"DECLINED\",\"error\":\"нет средств\"}");

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task Refund_PendingInBody_IsNotSuccess()
        {
            var r = await RefundAsync("{\"status\":\"PENDING\"}");

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task Refund_ConfirmedInBody_Succeeds()
        {
            var r = await RefundAsync("{\"status\":\"CONFIRMED\",\"transactionId\":\"TX-1\"}");

            Assert.True(r.IsSuccess);
            Assert.True(r.Value);
        }

        [Fact]
        public async Task Refund_EmptyBody200_IsSuccess()
        {
            // Контракт допускает пустой ответ: агент ничего не возвращает
            var r = await RefundAsync("");

            Assert.True(r.IsSuccess);
        }

        [Fact]
        public async Task Refund_HttpError_IsBusinessFailure()
        {
            var r = await RefundAsync("{}", HttpStatusCode.InternalServerError);

            Assert.False(r.IsSuccess);
        }
    }

    /// <summary>
    /// Обрезка ответа агента была сломана в двух адаптерах одинаково:
    /// при длине больше 200 возвращалась вся строка целиком.
    /// </summary>
    public class ResponseTruncationTests
    {
        [Fact]
        public void CardTerminal_Truncates_LongBody()
        {
            var longBody = new string('x', 5000);
            var r = InvokeTrunc(typeof(Infrastructure.Adapters.CardTerminal.CardTerminalService),
                "Trunc", longBody);

            Assert.NotNull(r);
            Assert.True(r.Length < 250, $"ответ не обрезан: {r.Length} символов");
        }

        [Fact]
        public void Logus_Truncates_LongBody()
        {
            var longBody = new string('y', 5000);
            var r = InvokeTrunc(typeof(Infrastructure.Adapters.Logus.LogusApiService),
                "TruncateBody", longBody);

            Assert.NotNull(r);
            Assert.True(r.Length < 250, $"ответ не обрезан: {r.Length} символов");
        }

        [Fact]
        public void CardTerminal_KeepsShortBody()
        {
            var r = InvokeTrunc(typeof(Infrastructure.Adapters.CardTerminal.CardTerminalService),
                "Trunc", "короткая ошибка");

            Assert.Equal("короткая ошибка", r);
        }

        private static string InvokeTrunc(Type type, string method, string input)
        {
            var info = type.GetMethod(method,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);

            Assert.NotNull(info);
            return (string)info.Invoke(null, new object[] { input });
        }
    }
}
