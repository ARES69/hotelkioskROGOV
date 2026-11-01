using System;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Application.Services
{
    /// <summary>
    /// Возврат денег гостю, если заселение сорвалось после подтверждённой оплаты.
    ///
    /// Почему это отдельный класс, а не метод в адаптере: возврат — это
    /// финансовая операция, и её нужно уметь позвать из разных мест (адаптер
    /// протокола MONE и разбор чекпоинта при старте), с одинаковыми правилами:
    ///
    ///   1. Возвращаем ТОЛЬКО подтверждённую оплату. Неоплаченный QR-заказ
    ///      не возвращается — его надо отменить (CancelUnpaidOrderAsync).
    ///   2. Повторный вызов для уже возвращённой транзакции ничего не делает.
    ///   3. Сам возврат не роняет заселение: ошибка уходит в лог и в
    ///      уведомление стойке, но не вылетает наружу.
    ///
    /// До появления этого класса адаптер протокола MONE умел только отменять
    /// неоплаченный QR-заказ: если деньги уже списаны, а заселение упало,
    /// возврат не происходил вовсе, а чекпоинт об этом не сообщал.
    /// </summary>
    public class PaymentCompensationService
    {
        private readonly IAlfaBankService _alfaBank;
        private readonly ICardTerminalService _cardTerminal;
        private readonly INotificationService _notifications;
        private readonly ICheckInSessionStore _sessionStore;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        /// <summary>Способ оплаты сессии: "qr" или "card". Влияет на адресата возврата.</summary>
        public string PaymentMethod { get; set; } = "qr";

        /// <summary>ID транзакции, по которой нужно сделать возврат (пусто = возвращать нечего).</summary>
        public string TransactionId { get; set; }

        /// <summary>Сумма к возврату, рубли.</summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Возврат по этой транзакции уже выполнен. Ставится из сессии адаптера,
        /// чтобы повторный вызов (ретрай, вторая попытка отмены) не списал деньги дважды.
        /// </summary>
        public bool AlreadyRefunded { get; set; }

        public PaymentCompensationService(
            IAlfaBankService alfaBank,
            ICardTerminalService cardTerminal,
            INotificationService notifications,
            ICheckInSessionStore sessionStore,
            Microsoft.Extensions.Logging.ILogger logger = null)
        {
            _alfaBank = alfaBank;
            _cardTerminal = cardTerminal;
            _notifications = notifications;
            _sessionStore = sessionStore;

            // ILogger (без generic) принимается, чтобы вызывающая сторона могла
            // передать свой логгер без обёртки на каждый вызов
            _logger = logger;
        }

        /// <summary>
        /// Списание прошло, оплата подтверждена, но заселение не завершилось —
        /// гость должен получить деньги назад.
        ///
        /// Возвращает true, если возврат выполнен (или уже был выполнен).
        /// false — если возвращать нечего либо возврат не удался; в обоих
        /// случаях персонал уведомлён.
        /// </summary>
        public async Task<bool> RefundAsync(string reservationId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(TransactionId))
            {
                _logger?.LogInformation(
                    "Возврат не требуется: транзакции нет (бронь {Reservation})", reservationId);
                return false;
            }

            // Защита от двойного возврата: повторный клик по «Отмена» или ретрай
            // после сбоя не должен списать деньги дважды.
            if (AlreadyRefunded)
            {
                _logger?.LogInformation(
                    "Возврат {TxId} уже выполнен ранее — повторный вызов пропущен", TransactionId);
                return true;
            }

            Result<bool> result = PaymentMethod == "card"
                ? await _cardTerminal.RefundPaymentAsync(TransactionId, Amount, ct)
                : await _alfaBank.RefundPaymentAsync(TransactionId, Amount, ct);

            if (!result.IsSuccess)
            {
                _logger?.LogError(
                    "Возврат {TxId} на {Amount} руб. не выполнен: {Error}. Требуется ручной возврат.",
                    TransactionId, Amount, result.Error);

                await NotifyStaffAsync(
                    $"🛑 Киоск: гость оплатил {_price()}, но заселение не завершилось. " +
                    $"Автоматический возврат НЕ выполнен ({result.Error}). " +
                    $"Бронь {reservationId}, транзакция {TransactionId}. " +
                    "Верните деньги вручную и проверьте состояние брони в Logus.",
                    ct);

                return false;
            }

            AlreadyRefunded = true;
            _logger?.LogInformation(
                "Выполнен возврат {TxId} на {Amount} руб. (бронь {Reservation}, метод {Method})",
                TransactionId, Amount, reservationId, PaymentMethod);

            await NotifyStaffAsync(
                $"✅ Киоск: оплата {_price()} возвращена гостю (бронь {reservationId}, " +
                $"транзакция {TransactionId}). Бронь в Logus не тронута — завершите заселение вручную.",
                ct);

            // Чекпоинт помечаем: персонал при следующем запуске увидит, что
            // деньги вернулись и осталось только закрыть бронь.
            await MarkRefundedAsync(reservationId, ct);

            return true;
        }

        /// <summary>
        /// Неоплаченный QR-заказ: не возврат, а отмена, чтобы гость не смог
        /// заплатить уже отменённый заказ. Для карточных операций не вызывается —
        /// их терминал откатывает сам.
        /// </summary>
        public async Task CancelUnpaidAsync(CancellationToken ct)
        {
            if (PaymentMethod == "card" || string.IsNullOrWhiteSpace(TransactionId))
                return;

            try
            {
                var cancel = await _alfaBank.CancelUnpaidOrderAsync(TransactionId, ct);
                if (!cancel.IsSuccess)
                    _logger?.LogWarning(
                        "Не удалось отменить неоплаченный QR-заказ {TxId}: {Error}",
                        TransactionId, cancel.Error);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Сбой отмены неоплаченного QR-заказа {TxId}", TransactionId);
            }
        }

        /// <summary>Отметка в чекпоинте, что возврат уже сделан.</summary>
        private async Task MarkRefundedAsync(string reservationId, CancellationToken ct)
        {
            try
            {
                var ctx = await _sessionStore.LoadAsync(ct);
                if (ctx == null)
                {
                    ctx = new CheckInContext
                    {
                        ReservationId = reservationId,
                        RequiresCompensation = true,
                        RequiredCompensation = CompensationAction.CancelLogusReservation,
                        CurrentState = CheckInState.PendingManualReview
                    };
                }

                ctx.PaymentTransactionId = null;
                ctx.PaidAmount = 0;
                ctx.PaymentConfirmed = false;
                ctx.RequiresCompensation = true;
                // Деньги вернулись — остаётся только закрыть бронь в Logus
                ctx.RequiredCompensation = CompensationAction.CancelLogusReservation;
                ctx.CurrentState = CheckInState.PendingManualReview;
                ctx.LastError = "Оплата возвращена автоматически, бронь требует ручной обработки";

                await _sessionStore.SaveAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                // Чекпоинт — вспомогательный: его сбой не должен превращать
                // успешный возврат в ошибку заселения
                _logger?.LogWarning(ex,
                    "Возврат выполнен, но отметку в чекпоинте сохранить не удалось");
            }
        }

        private async Task NotifyStaffAsync(string message, CancellationToken ct)
        {
            try
            {
                await _notifications.NotifyStaffAsync(message, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Не удалось уведомить стойку о компенсации");
            }
        }

        private string _price() => $"{Amount:0.##} руб.";
    }
}
