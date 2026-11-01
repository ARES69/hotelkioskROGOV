using System;
using System.Collections.Generic;
using HotelKiosk.Infrastructure.Adapters.AlfaBank;
using HotelKiosk.Infrastructure.Adapters.CardTerminal;
using HotelKiosk.Infrastructure.Adapters.GenericPms;
using HotelKiosk.Infrastructure.Adapters.Logus;
using HotelKiosk.Infrastructure.Adapters.Regula;
using HotelKiosk.Infrastructure.Adapters.VisionLine;
using HotelKiosk.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;

namespace HotelKiosk.Infrastructure.Configuration
{
    public enum ConfigIssueSeverity
    {
        /// <summary>Киоск работает, но функция недоступна или данные в Logus разойдутся.</summary>
        Warning,

        /// <summary>Функция не работает вовсе: путь гостя обрывается на этом шаге.</summary>
        Error
    }

    /// <summary>Одна проблема конфигурации: какой ключ пуст и что из-за этого не работает.</summary>
    public sealed class ConfigIssue
    {
        public ConfigIssue(ConfigIssueSeverity severity, string key, string message)
        {
            Severity = severity;
            Key = key;
            Message = message;
        }

        public ConfigIssueSeverity Severity { get; }

        /// <summary>Ключ конфигурации в нотации appsettings.json, напр. "Regula:AgentScanPath".</summary>
        public string Key { get; }

        public string Message { get; }

        public override string ToString() => $"{Severity}: {Key} — {Message}";
    }

    /// <summary>
    /// Проверяет конфигурацию киоска ДО открытия окна гостя.
    ///
    /// Зачем: все ключи оплаты/сканирования/уведомлений в поставке пустые, и
    /// раньше это приводило к тихому отказу на середине флоу — гость уже стоит
    /// перед экраном, когда выясняется, что паспорт нечем отсканировать, а QR
    /// нечем оплатить. Теперь это видно сразу на старте, списком, до первого гостя.
    ///
    /// Правила намеренно не блокирующие: Warning и Error собираются вместе и
    /// показываются оператору один раз. Решение об остановке киоска принимает
    /// человек (см. App.OnStartup).
    /// </summary>
    public static class KioskConfigurationValidator
    {
        public static IReadOnlyList<ConfigIssue> Validate(IConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var issues = new List<ConfigIssue>();

            // Проверка PMS сама решает, что проверять дальше: при Provider=logus
            // она вызывает ValidateLogus. Двойной вызов давал бы дубликаты
            // каждой проблемы в диалоге оператора.
            ValidatePms(configuration, issues);

            ValidateRegula(configuration, issues);
            ValidateAlfaBank(configuration, issues);
            ValidateVisionLine(configuration, issues);
            ValidateTelegram(configuration, issues);
            ValidateCardTerminal(configuration, issues);

            return issues;
        }

        /// <summary>Текст для MessageBox оператора: сначала то, что ломает флоу.</summary>
        public static string FormatForOperator(IReadOnlyList<ConfigIssue> issues)
        {
            if (issues == null || issues.Count == 0)
                return null;

            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (var i in issues)
                (i.Severity == ConfigIssueSeverity.Error ? errors : warnings).Add(
                    $"{i.Key}\n    {i.Message}");

            var sb = new System.Text.StringBuilder();
            if (errors.Count > 0)
                sb.AppendLine($"НЕ РАБОТАЕТ ({errors.Count}):").AppendLine()
                  .AppendLine(string.Join("\n\n", errors)).AppendLine();
            if (warnings.Count > 0)
                sb.AppendLine($"ПРЕДУПРЕЖДЕНИЯ ({warnings.Count}):").AppendLine()
                  .AppendLine(string.Join("\n\n", warnings));

            sb.AppendLine("Заполните эти ключи в appsettings.json рядом с HotelKiosk.UI.exe");
            sb.AppendLine("(или в appsettings.local.json) и перезапустите киоск.");
            return sb.ToString();
        }

        // -------------------------------------------------------------------

        /// <summary>
        /// Проверка выбранной PMS.
        ///
        /// Главное здесь — не дать киоску стартовать с PMS, у которой не описаны
        /// обязательные операции: без поиска брони и заселения флоу невозможен,
        /// и гость узнал бы об этом через два шага.
        /// </summary>
        private static void ValidatePms(IConfiguration c, List<ConfigIssue> issues)
        {
            var selection = PmsSelection.FromConfiguration(c);

            // Неизвестный код PMS: молча ушли бы в Logus, и оператор увидел бы
            // «бронь не найдена», не понимая почему
            if (!selection.Provider.Equals(PmsProviders.Logus, StringComparison.OrdinalIgnoreCase) &&
                !selection.Provider.Equals(PmsProviders.Generic, StringComparison.OrdinalIgnoreCase))
            {
                Error(issues, "Pms:Provider",
                    $"неизвестный код PMS \"{selection.Provider}\". Ожидается: " +
                    $"{string.Join(" или ", PmsProviders.Known)}");
                return;
            }

            if (!selection.IsGeneric)
            {
                // Боевой адаптер Logus проверяется по своей секции LogusApi
                ValidateLogus(c, issues);
                return;
            }

            var g = c.GetSection(GenericPmsOptions.SectionName)
                .Get<GenericPmsOptions>() ?? new GenericPmsOptions();

            if (string.IsNullOrWhiteSpace(g.BaseUrl))
                Error(issues, "Pms:Generic:BaseUrl",
                    "не задан адрес WebAPI выбранной PMS");

            if (!g.Authenticate.IsConfigured)
                Error(issues, "Pms:Generic:Authenticate:Path",
                    "не задан URL поиска брони — гость не сможет начать заселение");

            if (!g.CheckIn.IsConfigured)
                Error(issues, "Pms:Generic:CheckIn:Path",
                    "не задан URL заселения — бронь не перейдёт в статус проживания");

            // Обязательные пути ответа: без них бронь не соберётся.
            // Предупреждение, а не блокер: киоск стартует, но оператор увидит,
            // что поля пустыми придут.
            var requiredPaths = new (string Field, string Value)[]
            {
                ("ReservationId", g.Authenticate.Paths.ReservationId),
                ("ReservationArrival", g.Authenticate.Paths.ReservationArrival),
                ("ReservationDeparture", g.Authenticate.Paths.ReservationDeparture),
                ("ReservationPrice", g.Authenticate.Paths.ReservationPrice)
            };

            foreach (var (field, value) in requiredPaths)
            {
                if (!string.IsNullOrWhiteSpace(value)) continue;

                Warn(issues, $"Pms:Generic:Authenticate:Paths:{field}",
                    "путь не задан — поле останется пустым. Проверьте контракт PMS");
            }

            if (!string.IsNullOrWhiteSpace(g.BaseUrl) &&
                !Uri.TryCreate(g.BaseUrl, UriKind.Absolute, out var baseUri))
                Error(issues, "Pms:Generic:BaseUrl", $"адрес некорректен: \"{g.BaseUrl}\"");

            var auth = (g.AuthMode ?? "").Trim().ToLowerInvariant();
            var knownAuth = new[] { "none", "basic", "bearer", "token-header", "token-query" };

            if (!Array.Exists(knownAuth, m => m == auth))
                Warn(issues, "Pms:Generic:AuthMode",
                    $"\"{g.AuthMode}\" не поддерживается (ожидается: {string.Join(", ", knownAuth)}). " +
                    "Запросы уйдут без авторизации");

            if (auth == "basic" && string.IsNullOrWhiteSpace(g.Login))
                Warn(issues, "Pms:Generic:Login",
                    "указан режим basic, но логин не задан — PMS отклонит все запросы");

            if ((auth == "bearer" || auth == "token-header" || auth == "token-query") &&
                string.IsNullOrWhiteSpace(g.Token))
                Warn(issues, "Pms:Generic:Token",
                    $"указан режим {auth}, но токен не задан — PMS отклонит все запросы");

            if (!string.IsNullOrWhiteSpace(g.PaymentTransactionCode) && !g.AddPayment.IsConfigured)
                Warn(issues, "Pms:Generic:AddPayment:Path",
                    "задан код транзакции, но URL постинга оплаты не описан — " +
                    "оплата не попадёт на счёт брони");

            if (g.SendGuestDocuments && !g.UpsertGuest.IsConfigured)
                Warn(issues, "Pms:Generic:UpsertGuest:Path",
                    "передача паспортных данных включена, но эндпоинт не описан. " +
                    "Либо опишите его, либо выставьте SendGuestDocuments=false");

            // Не описанные операции — не ошибка: PMS может их просто не иметь
            var unsupported = new List<string>();
            if (!g.Cancel.IsConfigured) unsupported.Add("отмена брони");
            if (!g.Arrivals.IsConfigured) unsupported.Add("список заездов");
            if (!g.ReservationDetails.IsConfigured) unsupported.Add("данные брони");
            if (!g.AttachGuest.IsConfigured) unsupported.Add("привязка гостя к брони");

            if (unsupported.Count > 0)
                Warn(issues, "Pms:Generic",
                    "эти операции не описаны и будут отключены: " +
                    string.Join(", ", unsupported) + ". Заселение продолжит работать");
        }

        private static void ValidateLogus(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(LogusApiOptions.SectionName).Get<LogusApiOptions>() ?? new LogusApiOptions();

            // BaseUrl проверяем именно по исходному конфигу, а не по значению опции:
            // у LogusApiOptions дефолт — облако api-hms.logus.pro. Если ключ не задан,
            // киоск молча ушёл бы в чужой контур PMS вместо отеля.
            if (IsUnset(c, LogusApiOptions.SectionName, nameof(LogusApiOptions.BaseUrl)))
                Error(issues, "LogusApi:BaseUrl",
                    $"не задан адрес WebAPI — поиск брони невозможен. " +
                    $"По умолчанию подставился бы {new LogusApiOptions().BaseUrl} " +
                    "(облако Logus, не PMS отеля).");

            if (string.IsNullOrWhiteSpace(o.Login) || string.IsNullOrWhiteSpace(o.Password))
                Error(issues, "LogusApi:Login / LogusApi:Password",
                    "не заданы учётные данные WebAPI — поиск брони и заселение невозможны");

            if (string.IsNullOrWhiteSpace(o.PaymentTransactionCode))
                Warn(issues, "LogusApi:PaymentTransactionCode",
                    "код транзакции не задан — оплата не попадёт на folio брони. " +
                    "Заселение пройдёт, но потребует ручной сверки на стойке. " +
                    "Код берётся из справочника Logus.");
        }

        private static void ValidateRegula(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(RegulaOptions.SectionName).Get<RegulaOptions>() ?? new RegulaOptions();

            if (string.IsNullOrWhiteSpace(o.DocumentReaderUrl))
                Error(issues, "Regula:DocumentReaderUrl",
                    "не задан адрес Document Reader Web API");

            // Источник кадра: camera (фронтальная камера, гость с паспортом в рамке)
            // или reader (физический считыватель). При camera AgentScanPath не нужен.
            var source = (o.CaptureSource ?? "").Trim().ToLowerInvariant();
            if (source != "camera" && source != "reader")
                Warn(issues, "Regula:CaptureSource",
                    $"значение \"{o.CaptureSource}\" не распознано — используется автоопределение " +
                    "(считыватель, если задан AgentScanPath, иначе камера)");

            if (source == "reader" || (source == "" && !string.IsNullOrWhiteSpace(o.AgentScanPath)))
            {
                if (string.IsNullOrWhiteSpace(o.AgentScanPath))
                    Error(issues, "Regula:AgentScanPath",
                        "выбран источник \"reader\", но путь агента захвата не задан. " +
                        "Задайте Regula:AgentScanPath или переключите CaptureSource на \"camera\".");
            }

            if (o.CameraWidth < 320)
                Warn(issues, "Regula:CameraWidth",
                    $"ширина кадра {o.CameraWidth} px слишком мала для распознавания паспорта");

            if (o.CameraJpegQuality is <= 0 or > 1)
                Warn(issues, "Regula:CameraJpegQuality",
                    $"качество JPEG {o.CameraJpegQuality} вне диапазона 0.1–1.0, будет использовано 0.9");

            if (o.CameraJpegQuality > 0 && o.CameraJpegQuality < 0.6)
                Warn(issues, "Regula:CameraJpegQuality",
                    $"низкое качество кадра ({o.CameraJpegQuality}) — MRZ паспорта читается нестабильно");

            if (o.CameraCountdownSeconds < 0)
                Warn(issues, "Regula:CameraCountdownSeconds",
                    "отрицательный отсчёт — будет использован 0");
        }

        private static void ValidateAlfaBank(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(QrPaymentOptions.SectionName).Get<QrPaymentOptions>() ?? new QrPaymentOptions();

            if (string.IsNullOrWhiteSpace(o.Login) || string.IsNullOrWhiteSpace(o.Password))
                Error(issues, "AlfaBankQr:Login / AlfaBankQr:Password",
                    "не заданы учётные данные эквайринга Альфа-Банка — оплата по QR (СБП) невозможна");

            if (o.TestMode)
                Warn(issues, "AlfaBankQr:TestMode",
                    "включён тестовый контур эквайринга (TestBaseUrl). Перед боевым запуском выставьте false.");

            if (o.PollIntervalSeconds <= 0)
                Error(issues, "AlfaBankQr:PollIntervalSeconds",
                    "интервал опроса оплаты должен быть больше нуля");
        }

        private static void ValidateVisionLine(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(VisionLineOptions.SectionName).Get<VisionLineOptions>() ?? new VisionLineOptions();

            if (string.IsNullOrWhiteSpace(o.BaseUrl))
                Error(issues, "VisionLine:BaseUrl", "не задан адрес сервера VisionLine — ключ-карту записать нечем");

            if (string.IsNullOrWhiteSpace(o.Token))
                Warn(issues, "VisionLine:Token",
                    "токен не задан — мост VisionLine используется без авторизации. " +
                    "Если сервер его требует, запись карты будет отклонена.");
        }

        private static void ValidateTelegram(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();

            // Включено по умолчанию, но без токена/чата уведомления молча уходят в никуда —
            // персонал не узнает, что карта не записана. Это ровно тот случай, который
            // раньше проглатывался, поэтому Severity = Error, а не Warning.
            if (o.Enabled && (string.IsNullOrWhiteSpace(o.BotToken) || string.IsNullOrWhiteSpace(o.ChatId)))
                Error(issues, "Telegram:BotToken / Telegram:ChatId",
                    "уведомления включены, но токен или chat id не заданы — стойка не получит " +
                    "сообщение о незаписанной карте или сбое заселения. " +
                    "Либо заполните ключи, либо выставьте Enabled=false осознанно.");
        }

        private static void ValidateCardTerminal(IConfiguration c, List<ConfigIssue> issues)
        {
            var o = c.GetSection(CardTerminalOptions.SectionName).Get<CardTerminalOptions>()
                    ?? new CardTerminalOptions();

            if (!o.Enabled)
            {
                Warn(issues, "CardTerminal:Enabled",
                    "оплата картой выключена — гость платит только по QR (СБП).");
                return;
            }

            // Ключ, заданный пустой строкой, — это не то же самое, что отсутствие ключа:
            // во втором случае сработает разумный дефолт класса CardTerminalOptions,
            // в первом — попытка слать запросы в никуда.
            var raw = c[$"{CardTerminalOptions.SectionName}:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(raw) && !IsHttpUrl(raw))
                Error(issues, "CardTerminal:BaseUrl",
                    $"адрес агента пинпада задан некорректно: \"{raw}\" (ожидается http:// или https://)");
        }

        /// <summary>
        /// Ключ отсутствует или пуст в самом конфиге. Отличаем от случая, когда
        /// ключ не задан, но у класса опций есть вменяемый дефолт (BaseUrl пинпада,
        /// например) — такой дефолт использовать можно.
        /// </summary>
        private static bool IsUnset(IConfiguration c, string section, string key) =>
            string.IsNullOrWhiteSpace(c[$"{section}:{key}"]);

        private static bool IsHttpUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static void Error(List<ConfigIssue> issues, string key, string message) =>
            issues.Add(new ConfigIssue(ConfigIssueSeverity.Error, key, message));

        private static void Warn(List<ConfigIssue> issues, string key, string message) =>
            issues.Add(new ConfigIssue(ConfigIssueSeverity.Warning, key, message));
    }
}
