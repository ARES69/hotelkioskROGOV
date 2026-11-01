using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeCardTerminalService : ICardTerminalService
    {
        /// <summary>Агент включён (Enabled). false — имитация отключённой опции.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Жив ли агент/терминал (health-check).</summary>
        public bool Available { get; set; } = true;

        public bool FailCharge { get; set; }

        /// <summary>true — терминал подтвердил списание синхронно (RRN в charge-ответе).</summary>
        public bool ConfirmImmediately { get; set; }

        /// <summary>Статус операции при опросе (PENDING-сценарии).</summary>
        public bool StatusConfirmed { get; set; } = true;

        /// <summary>Сумма в ответе статуса; null — агент поле не прислал.</summary>
        public decimal? Amount { get; set; }

        public bool FailRefund { get; set; }

        public int ChargeCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int RefundCalls { get; private set; }
        public string LastOrderId { get; private set; }
        public decimal LastAmount { get; private set; }

        public Task<Result<PaymentResult>> ProcessCardPaymentAsync(
            decimal amount, string orderId, CancellationToken ct)
        {
            ChargeCalls++;
            LastOrderId = orderId;
            LastAmount = amount;

            if (!Enabled)
                return Task.FromResult(Result<PaymentResult>.BusinessFailure(
                    "Оплата картой отключена (CardTerminal:Enabled=false)"));
            if (FailCharge)
                return Task.FromResult(Result<PaymentResult>.BusinessFailure("Terminal declined"));

            return Task.FromResult(Result<PaymentResult>.Success(new PaymentResult(
                true, "CARD-TX-1", ConfirmImmediately ? "RRN-CARD" : null, null)));
        }

        public Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string orderId, CancellationToken ct)
        {
            StatusCalls++;
            return Task.FromResult(Result<PaymentStatus>.Success(
                new PaymentStatus(StatusConfirmed, StatusConfirmed ? "RRN-CARD" : null, orderId, Amount)));
        }

        public Task<Result<bool>> RefundPaymentAsync(
            string transactionId, decimal amount, CancellationToken ct)
        {
            RefundCalls++;
            if (FailRefund)
                return Task.FromResult(Result<bool>.SystemFailure("Terminal refund failed"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(Enabled && Available);
    }
}
