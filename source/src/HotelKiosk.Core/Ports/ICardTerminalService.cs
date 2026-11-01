using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Контракт банковского пинпада (оплата картой на киоске).
    ///
    /// Реализация — универсальный HTTP-адаптер к локальному агенту терминала
    /// на киоск-ПК (секция CardTerminal в appsettings): тонкая прослойка между
    /// софтом терминала банка и киоском. Контракт агента:
    ///   POST {ChargePath}  {"orderId","amount"}  → {"status","transactionId","rrn","error"}
    ///   GET  {StatusPath}?orderId=...            → та же форма ответа
    ///   POST {RefundPath}  {"transactionId","amount"}
    ///   GET  {HealthPath}
    /// где status: CONFIRMED | PENDING | DECLINED, amount — рубли (десятичное).
    /// </summary>
    public interface ICardTerminalService
    {
        /// <summary>
        /// Списать сумму с карты гостя. Success=true означает «операция принята
        /// терминалом»: если RRN пуст — операция ещё идёт (PENDING), реальное
        /// подтверждение ждём через CheckPaymentStatusAsync.
        /// </summary>
        Task<Result<PaymentResult>> ProcessCardPaymentAsync(
            decimal amount, string orderId, CancellationToken ct);

        /// <summary>Статус операции по orderId.</summary>
        Task<Result<PaymentStatus>> CheckPaymentStatusAsync(
            string orderId, CancellationToken ct);

        /// <summary>Возврат по карте (компенсация при сбое заселения).</summary>
        Task<Result<bool>> RefundPaymentAsync(
            string transactionId, decimal amount, CancellationToken ct);

        /// <summary>Жив ли агент/терминал — для активности кнопки «Карта».</summary>
        Task<bool> PingAsync(CancellationToken ct);
    }
}
