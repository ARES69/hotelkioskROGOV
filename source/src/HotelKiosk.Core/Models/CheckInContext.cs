using System;
using System.Collections.Generic;

namespace HotelKiosk.Core.Models
{
    public class CheckInContext
    {
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
        public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
        public bool IsConsumed { get; private set; }

        // Logus — данные для поиска брони через AuthenticateSelfCheckinAsync
        public string BookingCode { get; set; }
        public string ConfirmationKey { get; set; }
        public string ReservationId { get; set; }
        public string GuestId { get; set; }

        // Regula
        public string PassportNumber { get; set; }
        public string GuestName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        /// <summary>Полные данные со сканера — уходят в Logus (GuestProfile/UpdateGuests).</summary>
        public PassportData ScannedPassport { get; set; }
        /// <summary>Паспорт уже передан в Logus — защита от дублей профиля при повторах.</summary>
        public bool GuestDocsSent { get; set; }

        // Logus
        public string RoomNumber { get; set; }
        public decimal TotalAmount { get; set; }
        public DateTime CheckInDate { get; set; }
        public DateTime CheckOutDate { get; set; }

        // AlfaBank (QR)
        public string PaymentTransactionId { get; set; }
        public string RRN { get; set; }
        public decimal PaidAmount { get; set; }
        public bool PaymentConfirmed { get; set; }
        /// <summary>Оплата проведена в счёт брони Logus (AddPayment) — защита от двойного постинга.</summary>
        public bool PaymentPostedToPms { get; set; }
        public string QrPayload { get; set; }
        public string QrImageBase64 { get; set; }
        public DateTime? QrExpiresAt { get; set; }

        // VisionLine
        public string KeyCardUid { get; set; }
        public bool KeyCardEncoded { get; set; }

        // Состояние
        public CheckInState CurrentState { get; set; } = CheckInState.Initial;
        public string LastError { get; set; }
        public DateTime StateEnteredAt { get; set; } = DateTime.UtcNow;

        // Компенсация
        public bool RequiresCompensation { get; set; }
        public CompensationAction RequiredCompensation { get; set; }
        public string CancellationReason { get; set; } = "Compensation";
        public List<ExecutedStep> ExecutedSteps { get; set; } = new();

        /// <summary>
        /// Персональные данные удалены по истечении срока хранения.
        ///
        /// Такой чекпоинт сохраняет только след для ручного возврата — номер
        /// транзакции, сумму и признак «требуется компенсация». ПДн (паспорт,
        /// ФИО, дата рождения) стираются через час после закрытия сессии,
        /// сам след живёт сутки и удаляется.
        /// </summary>
        public bool Anonymized { get; set; }

        /// <summary>Когда обезличили — чтобы срок хранения следа считался от этого момента.</summary>
        public DateTime? AnonymizedAtUtc { get; set; }

        public void MarkAsConsumed()
        {
            if (IsConsumed)
                throw new InvalidOperationException(
                    $"Контекст {CorrelationId} уже использован. Создайте новый.");
            IsConsumed = true;
        }
    }

    public record ExecutedStep(string StepName, DateTime ExecutedAt, bool RequiresCompensation);
}
