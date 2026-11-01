using System;
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
    /// Компенсация — финансовая операция, поэтому проверяем не только happy path:
    /// главное здесь правило «не списать деньги дважды» и «не рисковать возвратом,
    /// когда статус неизвестен».
    /// </summary>
    public class PaymentCompensationServiceTests
    {
        private readonly FakeAlfaBankService _alfa = new();
        private readonly FakeCardTerminalService _card = new();
        private readonly FakeNotificationService _notifications = new();
        private readonly FakeCheckInSessionStore _store = new();

        private PaymentCompensationService Create(
            string method = "qr", string txId = "TX-1", decimal amount = 5000,
            bool alreadyRefunded = false) =>
            new(_alfa, _card, _notifications, _store,
                NullLogger<PaymentCompensationService>.Instance)
            {
                PaymentMethod = method,
                TransactionId = txId,
                Amount = amount,
                AlreadyRefunded = alreadyRefunded
            };

        [Fact]
        public async Task Refund_Qr_CallsBank()
        {
            var ok = await Create().RefundAsync("BR-1", CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(1, _alfa.RefundCalls);
            Assert.Equal(0, _card.RefundCalls);
        }

        [Fact]
        public async Task Refund_Card_CallsTerminal()
        {
            var ok = await Create(method: "card").RefundAsync("BR-1", CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(1, _card.RefundCalls);
            Assert.Equal(0, _alfa.RefundCalls);
        }

        [Fact]
        public async Task Refund_WithoutTransaction_DoesNothing()
        {
            var ok = await Create(txId: "").RefundAsync("BR-1", CancellationToken.None);

            Assert.False(ok);
            Assert.Equal(0, _alfa.RefundCalls);
        }

        [Fact]
        public async Task Refund_CalledTwice_RefundsOnce()
        {
            // Ключевая защита: ретрай или вторая попытка отмены не должна
            // списать деньги гостя дважды
            var service = Create(alreadyRefunded: true);

            var ok = await service.RefundAsync("BR-1", CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(0, _alfa.RefundCalls);
        }

        [Fact]
        public async Task Refund_Fails_ReturnsFalse_AndNotifiesStaff()
        {
            _alfa.FailRefund = true;

            var ok = await Create().RefundAsync("BR-1", CancellationToken.None);

            Assert.False(ok);
            Assert.Single(_notifications.SentMessages);
            Assert.Contains("НЕ выполнен", _notifications.SentMessages[0]);
        }

        [Fact]
        public async Task Refund_Fails_LeavesCheckpointIntact()
        {
            // Возврат не прошёл — чекпоинт должен остаться, иначе персонал
            // при следующем запуске не увидит незакрытую оплату
            _alfa.FailRefund = true;
            await _store.SaveAsync(new CheckInContext
            {
                ReservationId = "BR-1",
                PaymentTransactionId = "TX-1",
                PaidAmount = 5000,
                RequiresCompensation = true
            }, CancellationToken.None);

            await Create().RefundAsync("BR-1", CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Equal("TX-1", saved.PaymentTransactionId);
            Assert.True(saved.RequiresCompensation);
        }

        [Fact]
        public async Task Refund_Succeeds_MarksCheckpointForManualReview()
        {
            // Деньги вернулись, но бронь в Logus не тронута — персонал должен
            // её закрыть, поэтому флаг компенсации остаётся
            await _store.SaveAsync(new CheckInContext
            {
                ReservationId = "BR-1",
                PaymentTransactionId = "TX-1",
                PaidAmount = 5000,
                RequiresCompensation = true
            }, CancellationToken.None);

            await Create().RefundAsync("BR-1", CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Null(saved.PaymentTransactionId);
            Assert.Equal(0, saved.PaidAmount);
            Assert.False(saved.PaymentConfirmed);
            Assert.Equal(CompensationAction.CancelLogusReservation, saved.RequiredCompensation);
            Assert.Equal(CheckInState.PendingManualReview, saved.CurrentState);
        }

        [Fact]
        public async Task CancelUnpaid_Qr_CancelsOrder()
        {
            await Create().CancelUnpaidAsync(CancellationToken.None);
            Assert.Equal(1, _alfa.CancelUnpaidCalls);
        }

        [Fact]
        public async Task CancelUnpaid_Card_DoesNothing()
        {
            // Карточную операцию терминал откатывает сам; отменять нечего
            await Create(method: "card").CancelUnpaidAsync(CancellationToken.None);
            Assert.Equal(0, _alfa.CancelUnpaidCalls);
        }

        [Fact]
        public async Task CancelUnpaid_WithoutTransaction_DoesNothing()
        {
            await Create(txId: "").CancelUnpaidAsync(CancellationToken.None);
            Assert.Equal(0, _alfa.CancelUnpaidCalls);
        }

        [Fact]
        public async Task CancelUnpaid_Failure_DoesNotThrow()
        {
            // Сбой отмены не должен ломать заселение
            _alfa.FailCancelUnpaid = true;
            await Create().CancelUnpaidAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Refund_WhenNoCheckpoint_WritesMinimalContext()
        {
            // Чекапоинта нет (например, его удалили) — компенсация всё равно
            // должна оставить след, иначе возврат будет «ничей»
            await Create().RefundAsync("BR-1", CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Equal("BR-1", saved.ReservationId);
            Assert.Equal(CheckInState.PendingManualReview, saved.CurrentState);
        }
    }
}
