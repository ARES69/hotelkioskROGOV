using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace HotelKiosk.Infrastructure.Configuration
{
    /// <summary>
    /// Собирает Serilog из секции конфигурации.
    ///
    /// Почему не ReadFrom.Configuration: этот путь находит синки через
    /// DependencyContext и в данном окружении не находит НИ ОДНОГО — секция
    /// «Serilog» при этом читается без единой ошибки, приложение молча стартует
    /// вообще без логирования (проверено: неизвестный синк тоже не бросает
    /// исключение, файл лога не появляется). Раньше это маскировалось тем, что
    /// путь и уровень были захардкожены в коде.
    ///
    /// Поэтому значения секции разбираем явно. Поведение при отсутствии/ошибке
    /// секции — прежние значения: Information + logs/kiosk-.log + консоль.
    /// </summary>
    public static class KioskLogging
    {
        public const string SectionName = "Serilog";

        /// <summary>Значения по умолчанию, если секции нет или она не разбирается.</summary>
        public static LoggerConfiguration CreateDefault() =>
            new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(DefaultLogPath, rollingInterval: RollingInterval.Day)
                .WriteTo.Console();

        public const string DefaultLogPath = "logs/kiosk-.log";

        private static LoggerConfiguration CreateWithFallback(string path) =>
            new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(path, rollingInterval: RollingInterval.Day, shared: true)
                .WriteTo.Console();

        /// <summary>
        /// Собирает логгер по секции Serilog. Никогда не бросает: при любой
        /// проблеме возвращает рабочую конфигурацию по умолчанию, потому что
        /// отсутствие лога не должно останавливать киоск.
        ///
        /// defaultLogPath передаётся абсолютным: путь из конфига относительный,
        /// а при автозапуске киоска рабочий каталог не гарантирован — лог
        /// оказался бы в произвольном месте (раньше он просто не появлялся).
        /// </summary>
        public static LoggerConfiguration Create(IConfiguration configuration,
                                                 string appBaseDirectory = null,
                                                 string defaultLogPath = null)
        {
            var loggerConfig = new LoggerConfiguration();

            try
            {
                var section = configuration?.GetSection(SectionName);
                if (section == null || !section.Exists())
                    return CreateWithFallback(Resolve(appBaseDirectory, defaultLogPath ?? DefaultLogPath));

                var level = ParseLevel(section["MinimumLevel"], LogEventLevel.Information);
                loggerConfig.MinimumLevel.Is(level);

                // Переопределения уровня по источникам: {"Microsoft":"Warning"}
                var overrides = section.GetSection("MinimumLevelOverride");
                if (overrides.Exists())
                    foreach (var child in overrides.GetChildren())
                        loggerConfig.MinimumLevel.Override(child.Key,
                            ParseLevel(child.Value, LogEventLevel.Information));

                // Синки. Каждый разбирается явно: неизвестные имена пропускаем
                // с предупреждением, а не молча теряем.
                foreach (var sink in section.GetSection("WriteTo").GetChildren())
                {
                    var name = (sink["Name"] ?? "").Trim();

                    switch (name.ToLowerInvariant())
                    {
                        case "file":
                        {
                            var path = sink["Args:path"];
                            if (string.IsNullOrWhiteSpace(path))
                                path = Resolve(appBaseDirectory, defaultLogPath ?? DefaultLogPath);

                            loggerConfig.WriteTo.File(
                                Resolve(appBaseDirectory, path),
                                rollingInterval: ParseRollingInterval(sink["Args:rollingInterval"]),
                                retainedFileCountLimit: ParseNullableInt(sink["Args:retainedFileCountLimit"])
                                    ?? 31,
                                shared: true);
                            break;
                        }

                        case "console":
                            loggerConfig.WriteTo.Console();
                            break;

                        default:
                            // Пишем через Serilog.Debugging, чтобы оператор увидел
                            // опечатку в секции, а не «лог просто не пишется»
                            Serilog.Debugging.SelfLog.WriteLine(
                                "Неизвестный синк Serilog: \"{0}\". Ожидаются File или Console.", name);
                            break;
                    }
                }

                return loggerConfig;
            }
            catch (Exception ex)
            {
                Serilog.Debugging.SelfLog.WriteLine(
                    "Секция Serilog не разобрана: " + ex.Message);

                return CreateWithFallback(Resolve(appBaseDirectory, defaultLogPath ?? DefaultLogPath));
            }
        }

        /// <summary>Относительный путь привязываем к папке приложения.</summary>
        private static string Resolve(string appBaseDirectory, string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                string.IsNullOrWhiteSpace(appBaseDirectory))
                return path;

            return Path.Combine(appBaseDirectory, path);
        }

        private static LogEventLevel ParseLevel(string value, LogEventLevel fallback) =>
            !string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<LogEventLevel>(value.Trim(), ignoreCase: true, out var level)
                ? level
                : fallback;

        private static RollingInterval ParseRollingInterval(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<RollingInterval>(value.Trim(), ignoreCase: true, out var interval)
                ? interval
                : RollingInterval.Day;

        private static int? ParseNullableInt(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value.Trim(), out var parsed) && parsed > 0
                ? parsed
                : null;
    }
}
