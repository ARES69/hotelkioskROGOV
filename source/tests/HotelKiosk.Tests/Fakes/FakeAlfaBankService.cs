using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeAlfaBankService : IAlfaBankService
    {
        public bool FailPayment { get; set; }
        public bool NoRrn { get; set; }
        public bool TimeoutOnPayment { get; set; }
        public bool FailRefund { get; set; }
        public bool FailCancelUnpaid { get; set; }

        /// <summary>Статус платежа недоступен (сетевой сбой) — не то же, что «не оплачено».</summary>
        public bool ThrowOnStatusCheck { get; set; }
        public int RefundCalls { get; private set; }
        public int CancelUnpaidCalls { get; private set; }
        public int StatusCheckCalls { get; private set; }

        /// <summary>Номер последнего созданного заказа (idempotencyKey).</summary>
        public string LastOrderId { get; private set; }

        /// <summary>Для тестирования поллинга QR: статус платежа.</summary>
        public bool? StatusConfirmed { get; set; }

        /// <summary>Сумма в ответе статуса; null — банк поле не прислал.</summary>
        public decimal? Amount { get; set; }

        /// <summary>
        /// Сколько первых вызовов CheckPaymentStatusAsync вернуть неподтверждённый статус.
        /// 0 — задержки нет: при StatusConfirmed=true подтверждается сразу,
        /// при StatusConfirmed=false не подтверждается никогда.
        /// </summary>
        public int StatusSwitchDelay { get; set; } = 0;

        private int _statusCheckCalls;

        public Task<Result<PaymentResult>> ProcessPaymentAsync(
            decimal amount, string idempotencyKey, CancellationToken ct)
        {
            LastOrderId = idempotencyKey;

            if (TimeoutOnPayment)
            {
                return Task.Delay(Timeout.Infinite, ct)
                    .ContinueWith<Result<PaymentResult>>(_ => null, ct);
            }
            if (FailPayment)
                return Task.FromResult(Result<PaymentResult>.BusinessFailure("Declined"));
            return Task.FromResult(Result<PaymentResult>.Success(
                new PaymentResult(true, "TX-1", NoRrn ? null : "RRN-1", null)));
        }

        public Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string txId, CancellationToken ct)
        {
            StatusCheckCalls++;
            _statusCheckCalls++;

            // Статус недоступен: транспортный сбой. Адаптер в этом случае
            // не должен рисковать возвратом, поэтому отдельно моделируем.
            if (ThrowOnStatusCheck)
                throw new System.InvalidOperationException("status unavailable");

            bool confirmed;
            if (StatusConfirmed.HasValue)
            {
                // Первые StatusSwitchDelay вызовов — неподтверждённый статус,
                // затем статус становится равным StatusConfirmed.
                confirmed = StatusConfirmed.Value && _statusCheckCalls > StatusSwitchDelay;
            }
            else
            {
                confirmed = true;
            }

            return Task.FromResult(Result<PaymentStatus>.Success(
                new PaymentStatus(confirmed, confirmed ? "RRN-1" : null, txId, Amount)));
        }

        public Task<Result<bool>> RefundPaymentAsync(
            string txId, decimal amount, CancellationToken ct)
        {
            RefundCalls++;
            if (FailRefund)
                return Task.FromResult(Result<bool>.SystemFailure("Refund failed"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<Result<bool>> CancelUnpaidOrderAsync(string orderId, CancellationToken ct)
        {
            CancelUnpaidCalls++;
            if (FailCancelUnpaid)
                return Task.FromResult(Result<bool>.SystemFailure("Cancel failed"));
            return Task.FromResult(Result<bool>.Success(true));
        }
    }
}
