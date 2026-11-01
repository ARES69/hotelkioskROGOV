using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using HotelKiosk.Infrastructure.Adapters.GenericPms;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.UI
{
    /// <summary>
    /// Диагностика киоска без гостя: проверяет каждую интеграцию и печатает
    /// отчёт, который можно унести на стойку.
    ///
    /// Зачем это нужно. Настройка универсальной PMS (Pms:Generic) — это ручная
    /// работа с чужим API: опечатка в пути поля или в URL даст «бронь не найдена»
    /// уже при живом госте. Проверка заранее превращает это в понятную строку
    /// в отчёте.
    ///
    /// Запуск: HotelKiosk.UI.exe --selftest [путь к отчёту]
    /// </summary>
    public static class SelfTest
    {
        private const string Switch = "--selftest";

        public static bool IsRequested(string[] args) =>
            args is { Length: > 0 } && args.Any(a =>
                a.Equals(Switch, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Прогоняет проверки и возвращает готовый отчёт.
        /// Каждая проверка независима: сбой одной не мешает остальным.
        /// </summary>
        public static async Task<string> RunAsync(
            IConfiguration configuration,
            IPmsProvider pms,
            IRegulaService regula,
            IVisionLineService visionLine,
            PmsSelection selection,
            ILogger logger,
            CancellationToken ct)
        {
            var report = new List<string>
            {
                "=== Диагностика киоска HotelKiosk ===",
                $"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Компьютер: {Environment.MachineName}",
                $"Папка приложения: {AppContext.BaseDirectory}",
                ""
            };

            report.Add(await CheckPmsAsync(pms, selection, configuration, ct));
            report.Add(await CheckAsync("Распознавание паспорта (Regula)",
                () => regula.PingAsync(ct),
                "Web API не отвечает — гость не сможет отсканировать паспорт"));
            report.Add(await CheckAsync("Ключ-карты (VisionLine)",
                () => visionLine.PingAsync(ct),
                "Сервер ключей недоступен — записать карту нечем"));

            var issues = KioskConfigurationValidator.Validate(configuration);
            report.Add(RenderConfigIssues(issues));

            return string.Join(Environment.NewLine, report);
        }

        /// <summary>
        /// PMS проверяем подробнее остальных: здесь ручная настройка чужого
        /// контракта, и именно тут чаще всего опечатка.
        /// </summary>
        private static async Task<string> CheckPmsAsync(
            IPmsProvider pms, PmsSelection selection, IConfiguration configuration,
            CancellationToken ct)
        {
            var lines = new List<string>
            {
                $"--- PMS: {(string.IsNullOrWhiteSpace(selection.DisplayName) ? selection.Provider : selection.DisplayName)} ---",
                $"Адаптер: {selection.Provider}"
            };

            var caps = pms.Capabilities;

            lines.Add(caps.Describe());

            var missing = caps.MissingOptional;
            if (missing.Length > 0)
                lines.Add("Не поддерживается (заселение продолжит работать): " +
                          string.Join(", ", missing));

            lines.Add(await CheckAsync("  Живость PMS", () => pms.PingAsync(ct),
                "PMS не отвечает — бронь не найдётся"));

            // Контракт универсального адаптера печатаем целиком: оператор
            // сверяет его с документацией своей PMS
            if (selection.IsGeneric)
                lines.Add(RenderGenericContract(configuration));

            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        /// <summary>
        /// Сводка по описанным в конфиге операциям PMS: что описано, чем
        /// вызывается и какие поля читаются. Это то, что чаще всего сверяют
        /// с документацией PMS.
        /// </summary>
        private static string RenderGenericContract(IConfiguration configuration)
        {
            var operations = new (string Title, string Key)[]
            {
                ("Поиск брони",    "Authenticate"),
                ("Заселение",      "CheckIn"),
                ("Отмена брони",   "Cancel"),
                ("Постинг оплаты", "AddPayment"),
                ("Заезды на дату", "Arrivals"),
                ("Данные брони",   "ReservationDetails"),
                ("Профиль гостя",  "UpsertGuest"),
                ("Привязка гостя", "AttachGuest"),
                ("Health-check",   "Ping")
            };

            var baseUrl = configuration?["Pms:Generic:BaseUrl"];
            var authMode = configuration?["Pms:Generic:AuthMode"];

            var lines = new List<string>
            {
                "",
                "  Описанный контракт (сверьте с документацией вашей PMS):",
                $"    Адрес:   {baseUrl ?? "— не задан"}",
                $"    Авторизация: {authMode ?? "— не задана"}"
            };

            foreach (var (title, key) in operations)
            {
                var path = configuration?[$"Pms:Generic:{key}:Path"];
                var method = configuration?[$"Pms:Generic:{key}:Method"];
                var enabled = configuration?[$"Pms:Generic:{key}:Enabled"];

                var state = string.IsNullOrWhiteSpace(path)
                    ? "НЕ ОПИСАНА — будет отключена"
                    : $"{method} {path}";

                if (enabled == "false") state += " (Enabled=false)";

                lines.Add($"    {title,-18} {state}");
            }

            lines.Add("");
            lines.Add("    Пути в ответе (пусто = поле не читается):");
            lines.AddRange(RenderPaths(configuration));

            return string.Join(Environment.NewLine, lines);
        }

        private static IEnumerable<string> RenderPaths(IConfiguration configuration)
        {
            if (configuration == null) yield break;

            var interesting = new (string Label, string Key)[]
            {
                ("номер брони",        "Pms:Generic:Authenticate:Paths:ReservationId"),
                ("номер комнаты",      "Pms:Generic:Authenticate:Paths:ReservationRoomCode"),
                ("заезд",              "Pms:Generic:Authenticate:Paths:ReservationArrival"),
                ("выезд",              "Pms:Generic:Authenticate:Paths:ReservationDeparture"),
                ("сумма",              "Pms:Generic:Authenticate:Paths:ReservationPrice"),
                ("фамилия",            "Pms:Generic:Authenticate:Paths:GuestLastName"),
                ("свободные номера",   "Pms:Generic:Authenticate:Paths:AvailableRooms"),
                ("код типа в номере",  "Pms:Generic:Authenticate:Paths:RoomCode"),
                ("занятость номера",   "Pms:Generic:Authenticate:Paths:RoomOccupied"),
                ("guid брони",         "Pms:Generic:ReservationDetails:Paths:ReservationGuid"),
                ("personId гостя",     "Pms:Generic:UpsertGuest:Paths:PersonId"),
                ("массив заездов",     "Pms:Generic:Arrivals:Paths:ArrivalsArray")
            };

            foreach (var (label, key) in interesting)
            {
                var value = configuration[key];
                yield return $"      {(string.IsNullOrWhiteSpace(value) ? "—" : value),-34} {label}";
            }
        }

        private static async Task<string> CheckAsync(
            string title, Func<Task<bool>> probe, string hint)
        {
            var watch = Stopwatch.StartNew();

            try
            {
                // Проба ограничена по времени: сетевой висящий хост не должен
                // превращать диагностику в зависание
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var task = probe();
                var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(16), cts.Token));

                if (done != task)
                    return $"{title,-34} ТАЙМАУТ (>15 с) — {hint}";

                var ok = await task;
                watch.Stop();

                return ok
                    ? $"{title,-34} ОК ({watch.ElapsedMilliseconds} мс)"
                    : $"{title,-34} НЕ ОТВЕЧАЕТ — {hint}";
            }
            catch (OperationCanceledException)
            {
                return $"{title,-34} ТАЙМАУТ — {hint}";
            }
            catch (Exception ex)
            {
                return $"{title,-34} ОШИБКА: {ex.Message}";
            }
        }

        private static string RenderConfigIssues(IReadOnlyList<ConfigIssue> issues)
        {
            if (issues.Count == 0)
                return "--- Конфигурация ---" + Environment.NewLine +
                       "Заполнена полностью, блокеров нет.";

            var lines = new List<string> { "--- Конфигурация ---" };

            var errors = issues.Where(i => i.Severity == ConfigIssueSeverity.Error).ToArray();
            var warnings = issues.Where(i => i.Severity == ConfigIssueSeverity.Warning).ToArray();

            if (errors.Length > 0)
            {
                lines.Add($"НЕ РАБОТАЕТ ({errors.Length}):");
                lines.AddRange(errors.Select(i => $"  {i.Key}\n      {i.Message}"));
                lines.Add("");
            }

            if (warnings.Length > 0)
            {
                lines.Add($"ПРЕДУПРЕЖДЕНИЯ ({warnings.Length}):");
                lines.AddRange(warnings.Select(i => $"  {i.Key}\n      {i.Message}"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
