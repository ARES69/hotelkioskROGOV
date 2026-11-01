namespace HotelKiosk.Infrastructure.Adapters.AlfaBank
{
    /// <summary>
    /// Настройки для оплаты по QR (СБП через Альфа-Банк).
    /// Эквайринг Альфа-Банка работает на платёжном шлюзе «Роутер»:
    ///   register.do → sbp_c2b/get.do → sbp_c2b/status.do → refund.do
    /// </summary>
    public class QrPaymentOptions
    {
        public const string SectionName = "AlfaBankQr";

        /// <summary>Боевой URL платёжного шлюза.</summary>
        public string BaseUrl { get; set; } = "https://payment.alfabank.ru/payment/rest/";

        /// <summary>Тестовый URL платёжного шлюза.</summary>
        public string TestBaseUrl { get; set; } = "https://test.payment.alfabank.ru/payment/rest/";

        /// <summary>true — использовать тестовый контур (TestBaseUrl), false — боевой (BaseUrl).</summary>
        public bool TestMode { get; set; } = true;

        /// <summary>Логин API-эквайринга (выдаётся в личном кабинете Альфа-Банка).</summary>
        public string Login { get; set; }

        /// <summary>Пароль API-эквайринга.</summary>
        public string Password { get; set; }

        /// <summary>
        /// URL, на который банк перенаправит после оплаты (обязательное поле register.do).
        /// Для киоска — любой рабочий HTTP-URL; гость его не увидит при QR-оплате.
        /// </summary>
        public string ReturnUrl { get; set; } = "https://kiosk.local/callback";

        /// <summary>Через сколько минут неоплаченный QR считается протухшим.</summary>
        public int QrLifetimeMinutes { get; set; } = 5;

        /// <summary>Интервал опроса статуса оплаты, пока гость сканирует QR.</summary>
        public int PollIntervalSeconds { get; set; } = 3;

        /// <summary>Таймаут одного HTTP-запроса к банку.</summary>
        public int RequestTimeoutSeconds { get; set; } = 15;
    }
}
