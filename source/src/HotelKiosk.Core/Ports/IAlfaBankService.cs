using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Контракт для взаимодействия с Альфа-Банком (QR-эквайринг / СБП).
    /// </summary>
    public interface IAlfaBankService
    {
        /// <summary>Создать заказ и получить QR для оплаты.</summary>
        Task<Result<PaymentResult>> ProcessPaymentAsync(
            decimal amount, string idempotencyKey, CancellationToken cancellationToken);

        /// <summary>Проверить, оплачен ли заказ (опрос статуса).</summary>
        Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string transactionId, CancellationToken cancellationToken);

        /// <summary>Возврат уже оплаченного заказа (reverse/refund).</summary>
        Task<Result<bool>> RefundPaymentAsync(
            string transactionId, decimal amount, CancellationToken cancellationToken);

        /// <summary>
        /// Отменить ещё не оплаченный заказ (гость не отсканировал QR / отменил
        /// операцию до подтверждения платежа). Часть банков закрывает такие
        /// заказы автоматически по истечении QrLifetimeMinutes, но для гарантии
        /// вызывайте явно при отмене на экране оплаты.
        /// </summary>
        Task<Result<bool>> CancelUnpaidOrderAsync(
            string orderId, CancellationToken cancellationToken);
    }
}
