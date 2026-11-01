namespace HotelKiosk.Application.Options
{
    public class CheckInStateMachineOptions
    {
        public const string SectionName = "CheckInStateMachine";

        public int MaxRetries { get; set; } = 3;
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Запасной дедлайн ожидания оплаты по QR, если по какой-то причине
        /// не известно время истечения самого QR (QrExpiresAt). В норме
        /// реальным дедлайном служит QrExpiresAt из ответа банка/QrPaymentOptions.QrLifetimeMinutes —
        /// это значение только подстраховка. Держите его согласованным с
        /// QrPaymentOptions.QrLifetimeMinutes (в секундах).
        /// </summary>
        public int PaymentCheckTimeoutSeconds { get; set; } = 300;

        /// <summary>Пауза между опросами статуса QR-заказа, пока гость сканирует и подтверждает оплату.</summary>
        public int PaymentPollIntervalSeconds { get; set; } = 3;

        /// <summary>Сколько раз повторить один запрос статуса при транспортной ошибке (не при "ещё не оплачено").</summary>
        public int PaymentCheckRetries { get; set; } = 2;
        public int CompensationRetries { get; set; } = 3;
    }
}
