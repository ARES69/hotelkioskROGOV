namespace HotelKiosk.Core.Models
{
    public enum CheckInState
    {
        Initial,
        ScanningPassport,
        PassportScanned,
        FetchingReservation,
        ReservationFound,
        AwaitingPayment,
        AwaitingQrPayment,
        PaymentProcessing,
        PaymentConfirmed,
        UpdatingLogus,
        LogusUpdated,
        EncodingKeyCard,
        KeyCardEncoded,
        Success,
        Failed,
        AwaitingCompensation,
        CompensationCompleted,
        PendingManualReview
    }

    public enum CompensationAction
    {
        None,
        RefundPayment,
        CancelLogusReservation,
        Both
    }
}
