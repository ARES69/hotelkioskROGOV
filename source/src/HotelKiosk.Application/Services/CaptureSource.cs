namespace HotelKiosk.Application.Services
{
    /// <summary>
    /// Как киоск получает кадр паспорта.
    ///
    /// Задаётся один раз при старте из конфигурации Regula:
    ///   CaptureSource = "camera" → фронтальная камера, гость в рамке;
    ///   CaptureSource = "reader" → физический считыватель (AgentScanPath);
    ///   CaptureSource пусто     → авто: считыватель, если AgentScanPath задан,
    ///                             иначе камера.
    ///
    /// Нужен адаптеру протокола, чтобы system.preflight сообщал «шаг
    /// сканирования настроен» до того, как гость встанет в рамку.
    /// </summary>
    public sealed class CaptureSource
    {
        public const string Camera = "camera";
        public const string Reader = "reader";

        /// <summary>Что используем: "camera" или "reader".</summary>
        public string Kind { get; init; } = Camera;

        /// <summary>Задан ли агент считывателя (Regula:AgentScanPath).</summary>
        public bool AgentScanPathConfigured { get; init; }

        public bool IsCamera =>
            string.Equals(Kind, Camera, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Можно ли хоть как-то получить кадр паспорта.</summary>
        public bool IsConfigured => IsCamera || AgentScanPathConfigured;

        /// <summary>
        /// Разбирает Regula:CaptureSource с учётом наличия AgentScanPath.
        /// </summary>
        public static CaptureSource Resolve(string captureSource, bool agentScanPathConfigured)
        {
            var configured = (captureSource ?? "").Trim();

            if (configured.Equals(Camera, System.StringComparison.OrdinalIgnoreCase))
                return new CaptureSource { Kind = Camera, AgentScanPathConfigured = agentScanPathConfigured };

            if (configured.Equals(Reader, System.StringComparison.OrdinalIgnoreCase))
                return new CaptureSource { Kind = Reader, AgentScanPathConfigured = agentScanPathConfigured };

            // Авто: считыватель, если он есть; иначе камера
            return new CaptureSource
            {
                Kind = agentScanPathConfigured ? Reader : Camera,
                AgentScanPathConfigured = agentScanPathConfigured
            };
        }

        /// <summary>Камера с настроенными параметрами — вариант по умолчанию.</summary>
        public static CaptureSource Default { get; } = new() { Kind = Camera };
    }
}
