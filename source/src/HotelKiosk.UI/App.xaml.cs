using System;
using System.IO;
using System.Linq;
using System.Windows;
using HotelKiosk.Application.Services;
using HotelKiosk.Infrastructure;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace HotelKiosk.UI
{
    public partial class App : System.Windows.Application
    {
        private IHost _host;
        public IHost Host => _host;

        /// <summary>
        /// Именованный мьютекс: на стойке один киоск, но автозапуск и двойной
        /// клик по ярлыку могли поднять два процесса. Второй увидит мьютекс и
        /// выйдет, не открыв второе окно гостя и не создав второй чекпоинт.
        /// </summary>
        private Mutex _singleInstance;

        /// <summary>Демо-режим киоска: --demo в аргументах — все действия мокаются, бэкенд не нужен.</summary>
        public static bool IsDemoMode { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstance = new Mutex(initiallyOwned: true, @"Local\HotelKiosk.UI", out var isFirst);
            if (!isFirst)
            {
                // Молча выходим: модальный диалог на киоске некому закрыть, и
                // висящий процесс держал бы второй экран гостя. Вместо этого
                // поднимаем окно уже работающего экземпляра.
                TryActivateRunningInstance();
                Shutdown();
                return;
            }

            IsDemoMode = e.Args is { Length: > 0 } args &&
                args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

            // Проверка камеры: отдельное окно с живым превью и замерами,
            // без PMS и остальных интеграций. Нужна перед установкой на киоск.
            //
            // Проверка идёт ДО сборки хоста и создания MainWindow, поэтому
            // _host при этом null. Главное окно при старте создаётся
            // обработчиком StartupUri сам — отдельный поток не нужен.
            //
            // Логирование настраиваем сразу: без него диагностика чёрная —
            // если страница не отвечает, причина не видна.
            if (CameraTest.IsRequested(e.Args))
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                ConfigureLogging(BuildConfiguration());
                Log.Information("Запущена проверка камеры (--cameratest)");

                new CameraTestWindow().Show();
                return;
            }


            // Конфиг нужен до Serilog: путь файла лога и уровень берём из него же.
            var config = BuildConfiguration();
            ConfigureLogging(config);
            _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureAppConfiguration((_, cfg) =>
                {
                    // Абсолютные пути: автозапуск киоска не гарантирует рабочую папку
                    var baseDir = AppContext.BaseDirectory;
                    cfg.AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: false);
                    // Локальные переопределения (например, LogusApi:BaseUrl → локальный WebAPI).
                    // Поздние источники перекрывают ранние.
                    cfg.AddJsonFile(Path.Combine(baseDir, "appsettings.local.json"), optional: true);
                    cfg.AddJsonFile(Path.Combine(baseDir, $"appsettings.{Environment.MachineName}.json"), optional: true);
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.AddHotelKiosk(ctx.Configuration);
                })
                .Build();

            var selftest = SelfTest.IsRequested(e.Args);

            // Валидируем ту же конфигурацию, что досталась хосту (включая env/CLI),
            // иначе проверялось бы не то, что реально запустится.
            // В режиме диагностики диалог пропускаем: он блокирующий, а весь
            // смысл --selftest в том, чтобы получить отчёт без нажатий.
            if (!selftest)
                ReportConfigurationIssues(_host.Services.GetRequiredService<IConfiguration>());

            _host.Start();

            if (selftest)
            {
                await RunSelfTestAsync(e.Args);
                return;
            }

            CheckForUnrecoveredSession();

            // Главное окно создаём сами: StartupUri убран из App.xaml, иначе
            // WPF создал бы его ДО сборки хоста, и MainWindow упал бы с
            // NullReferenceException при разборе сервисов.
            new MainWindow().Show();
        }

        /// <summary>
        /// Диагностика без гостя: проверяет интеграции и сохраняет отчёт.
        /// Запуск: HotelKiosk.UI.exe --selftest [путь к файлу отчёта]
        /// </summary>
        private async Task RunSelfTestAsync(string[] args)
        {
            var services = _host.Services;
            var logger = services
                .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger("SelfTest");
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                logger, "Запрошена диагностика киоска (--selftest)");

            var report = await SelfTest.RunAsync(
                _host.Services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<HotelKiosk.Core.Ports.Pms.IPmsProvider>(),
                services.GetRequiredService<HotelKiosk.Core.Ports.IRegulaService>(),
                services.GetRequiredService<HotelKiosk.Core.Ports.IVisionLineService>(),
                services.GetRequiredService<HotelKiosk.Infrastructure.Adapters.GenericPms.PmsSelection>(),
                logger,
                CancellationToken.None);

            Log.Information("Диагностика киоска:{NewLine}{Report}", Environment.NewLine, report);

            // Отчёт по умолчанию рядом с приложением: его удобно унести
            // на стойку или приложить к письму о проблеме
            var defaultPath = Path.Combine(AppContext.BaseDirectory,
                $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var path = args.Length > 1 && !args[1].StartsWith("-", StringComparison.Ordinal)
                ? args[1]
                : defaultPath;

            try
            {
                await File.WriteAllTextAsync(path, report);
                Log.Information("Отчёт сохранён: {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось сохранить отчёт в {Path}", path);
                path = null;
            }

            // Окно гостя при диагностике не нужно и только мешает
            MessageBox.Show(report, "HotelKiosk — диагностика",
                MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
        }

        /// <summary>
        /// Поднимает окно уже работающего киоска. Второй экземпляр не показывает
        /// свой диалог, а просто уступает экран уже открытому.
        /// </summary>
        private static void TryActivateRunningInstance()
        {
            try
            {
                var current = System.Windows.Application.Current;
                if (current?.MainWindow == null) return;

                var window = current.MainWindow;
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            catch (Exception)
            {
                // Не смогли активировать — не критично, второй процесс всё равно выйдет
            }
        }

        /// <summary>
        /// Отдельная сборка конфигурации только для настройки логирования: Serilog
        /// создаётся до хоста, а секция Serilog лежит в тех же json-файлах.
        /// Валидатор ниже работает уже с конфигурацией самого хоста.
        /// </summary>
        private static IConfigurationRoot BuildConfiguration()
        {
            // Абсолютные пути: автозапуск киоска не гарантирует рабочую папку
            var baseDir = AppContext.BaseDirectory;
            return new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: false)
                .AddJsonFile(Path.Combine(baseDir, "appsettings.local.json"), optional: true)
                .AddJsonFile(Path.Combine(baseDir, $"appsettings.{Environment.MachineName}.json"), optional: true)
                .Build();
        }

        /// <summary>
        /// Секция Serilog в appsettings.json (раньше была мёртвой — путь и уровень
        /// были захардкожены). Собирается явно, а не через ReadFrom.Configuration:
        /// см. обоснование в KioskLogging.
        /// </summary>
        private static void ConfigureLogging(IConfiguration config)
        {
            // AppContext.BaseDirectory, а не Environment.CurrentDirectory: при
            // автозапуске киоска рабочая папка не гарантирована, и лог уезжал
            // в произвольное место (чаще всего не появлялся вовсе)
            Log.Logger = KioskLogging
                .Create(config, AppContext.BaseDirectory)
                .CreateLogger();
            Log.Information(
                "HotelKiosk стартует. Версия={Version}, MachineName={Machine}, Demo={Demo}, " +
                "WorkingDir={WorkingDir}",
                typeof(App).Assembly.GetName().Version?.ToString(),
                Environment.MachineName, IsDemoMode, Environment.CurrentDirectory);
        }

        /// <summary>
        /// Показывает оператору незаполненные/неверные ключи конфигурации ДО первого гостя.
        /// Раньше пустые Regula:AgentScanPath, AlfaBankQr:Login, Telegram:ChatId и
        /// LogusApi:PaymentTransactionCode приводили к тихому отказу в середине флоу.
        /// В демо-режиме (--demo) бэкенд не нужен — предупреждения не показываем.
        /// </summary>
        private static void ReportConfigurationIssues(IConfiguration config)
        {
            var issues = KioskConfigurationValidator.Validate(config);
            if (issues.Count == 0)
            {
                Log.Information("Конфигурация заполнена полностью — блокеров нет");
                return;
            }

            if (IsDemoMode)
            {
                Log.Information("Демо-режим: проверка конфигурации пропущена ({Count} замечаний)",
                    issues.Count);
                return;
            }

            foreach (var i in issues)
            {
                if (i.Severity == ConfigIssueSeverity.Error)
                    Log.Error("Конфигурация: {Key} — {Message}", i.Key, i.Message);
                else
                    Log.Warning("Конфигурация: {Key} — {Message}", i.Key, i.Message);
            }

            var errors = System.Linq.Enumerable.Count(issues, i => i.Severity == ConfigIssueSeverity.Error);
            var title = errors > 0
                ? $"HotelKiosk — не настроено ({errors} блокеров)"
                : "HotelKiosk — проверьте настройки";

            MessageBox.Show(
                KioskConfigurationValidator.FormatForOperator(issues),
                title,
                MessageBoxButton.OK,
                errors > 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }

        /// <summary>
        /// Если прошлый процесс не дошёл до конца (упал/завис/выключили ПК),
        /// на диске останется чекпоинт незавершённой сессии — persistence
        /// сам его не трогает, пока не увидит его персонал. Тихо начинать
        /// новую сессию поверх — риск потерять факт, что гость уже заплатил.
        /// </summary>
        private void CheckForUnrecoveredSession()
        {
            var store = _host.Services.GetRequiredService<HotelKiosk.Core.Ports.ICheckInSessionStore>();
            var leftover = store.LoadAsync(System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();

            if (leftover == null) return;

            MessageBox.Show(
                "Обнаружена незавершённая сессия заселения с прошлого запуска (приложение " +
                "закрылось не штатно — сбой, зависание или выключение ПК).\n\n" +
                $"Гость: {leftover.GuestName ?? "—"}\n" +
                $"Номер: {leftover.RoomNumber ?? "—"}\n" +
                $"Бронь: {leftover.ReservationId ?? "—"}\n" +
                $"Остановилось на шаге: {leftover.CurrentState}\n" +
                $"Оплата подтверждена: {(leftover.PaymentConfirmed ? "ДА" : "нет")}\n" +
                $"Карта записана: {(leftover.KeyCardEncoded ? "ДА" : "нет")}\n" +
                (string.IsNullOrEmpty(leftover.LastError) ? "" : $"Ошибка: {leftover.LastError}\n") +
                "\nПроверьте вручную (особенно оплату в личном кабинете Альфа-Банка и бронь в Logus) " +
                "перед тем как заселять этого гостя заново.",
                "HotelKiosk — незавершённая сессия",
                MessageBoxButton.OK, MessageBoxImage.Warning);

            // Не удаляем файл автоматически — пусть его явно уберёт тот, кто
            // разобрался в ситуации (например, при следующем успешном заселении
            // ClearAsync отработает сам, либо удалите session.json вручную).
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.StopAsync().GetAwaiter().GetResult();
            _host?.Dispose();
            Log.CloseAndFlush();

            // Мьютекс держим до конца: пока он не отпущен, второй процесс
            // не поднимется. Release в finally — чтобы исключение при
            // остановке хоста не оставить мьютекс захваченным.
            try
            {
                _singleInstance?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс не наш — ничего освобождать не нужно
            }
            finally
            {
                _singleInstance?.Dispose();
                _singleInstance = null;
            }

            base.OnExit(e);
        }
    }
}
