using System;

namespace HotelKiosk.Core.Models
{
    /// <summary>
    /// Данные паспорта со сканера. Базовые поля — минимум для показа на UI;
    /// остальные (серия, дата выдачи, кем выдан и т.д.) заполняются, если
    /// сканер их распознал, и уходят в Logus при заселении
    /// (GuestProfile + UpdateGuests, контракт Реджисты).
    /// </summary>
    public record PassportData(
        string PassportNumber,
        string FullName,
        DateTime? DateOfBirth,
        string Series = null,
        string FirstName = null,
        string LastName = null,
        string MiddleName = null,
        string Sex = null,
        string BirthPlace = null,
        string CountryCode = "RUS",
        DateTime? IssueDate = null,
        string DepartmentCode = null,
        string IssuedBy = null);
    public record ReservationData(
        string ReservationId, string GuestId, string RoomNumber,
        decimal TotalAmount, DateTime CheckIn, DateTime CheckOut,
        LogusReservationStatus Status);
    /// <summary>
    /// Результат вызова ProcessPaymentAsync.
    /// ВАЖНО (QR-оплата): Success=true здесь означает "заказ на оплату создан
    /// и QR готов к показу гостю", а НЕ "деньги списаны". Реального
    /// подтверждения оплаты по QR всегда нужно ждать через
    /// CheckPaymentStatusAsync (см. VerifyPaymentStatusAsync в стейт-машине) —
    /// поэтому RRN тут почти всегда null сразу после создания заказа, что
    /// корректно уводит стейт-машину в цикл ожидания подтверждения.
    /// </summary>
    public record PaymentResult(
        bool Success, string TransactionId, string RRN, string ErrorMessage,
        string QrPayload = null, string QrImageBase64 = null, DateTime? QrExpiresAt = null);
    /// <summary>
    /// Статус платежа. Amount — сумма, которую подтвердил владелец заказа
    /// (банк или терминал); null, если сервис её не вернул. Единицы измерения
    /// у разных шлюзов различаются, поэтому сверка терпима к обоим вариантам.
    /// </summary>
    public record PaymentStatus(
        bool Confirmed, string RRN, string TransactionId, decimal? Amount = null);
    public record KeyCardResult(bool Success, string CardUid, string ErrorMessage);
}
