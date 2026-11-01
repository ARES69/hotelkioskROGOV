using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HotelKiosk.Infrastructure.Adapters.Regula;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HotelKiosk.UI
{
    /// <summary>Запуск окна проверки камеры: HotelKiosk.UI.exe --cameratest</summary>
    public static class CameraTest
    {
        public const string Switch = "--cameratest";

        public static bool IsRequested(string[] args) =>
            args is { Length: > 0 } && args.Any(a =>
                a.Equals(Switch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Окно проверки фронтальной камеры.
    ///
    /// Зачем отдельное окно, а не раздел в киоске: проверка идёт ДО установки,
    /// без PMS, банка и Regula. Проверяется ровно та цепочка, что в бою —
    /// WebView2 + getUserMedia с разрешением из PermissionRequested, рисование
    /// в canvas и те же замеры кадра, что делает KioskCamera перед съёмкой.
    ///
    /// Результат пишется в cameratest-result.json рядом с exe: снимок экрана из
    /// WebView2 получить нельзя, браузер рисует в отдельной поверхности
    /// композитинга, а файл читается и руками, и скриптом.
    /// </summary>
    public class CameraTestWindow : Window
    {
        private readonly Microsoft.Web.WebView2.Wpf.WebView2 _browser = new();
        private readonly DispatcherTimer _pollTimer;
        private int _attempts;

        public CameraTestWindow()
        {
            Title = "Проверка камеры киоска";
            Width = 1180;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = System.Windows.Media.Brushes.Black;
            Content = _browser;

            // Опрашиваем страницу по таймеру: так не зависим от того, придёт ли
            // NavigationCompleted и долетит ли postMessage
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += (_, _) => _ = TryReadReportAsync();

            Loaded += async (_, _) => await StartAsync();
            Closed += (_, _) =>
            {
                _pollTimer.Stop();
                _cts.Cancel();
            };
        }

        private readonly System.Threading.CancellationTokenSource _cts = new();

        private async Task StartAsync()
        {
            try
            {
                await _browser.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Проверка камеры: WebView2 не запустился");
                Fail("Не удалось запустить WebView2: " + ex.GetBaseException().Message);
                return;
            }

            Log.Information("Проверка камеры: WebView2 готов");

            var core = _browser.CoreWebView2;

            // Разрешение на камеру выдаём так же, как в основном окне: в киоске
            // диалога подтверждения нет, и WebView2 иначе молча откажет
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.Camera)
                    args.State = CoreWebView2PermissionState.Allow;
            };

            // Параметры из конфигурации: проверяются те же значения, что
            // поедут на киоск
            var reg = LoadRegulaOptions();
            var json = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    allowDemo = false,
                    camera = new
                    {
                        enabled = true,
                        width = reg.CameraWidth,
                        height = reg.CameraHeight,
                        jpegQuality = reg.CameraJpegQuality,
                        countdown = reg.CameraCountdownSeconds
                    }
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            // Скрипт должен быть зарегистрирован ДО навигации, поэтому не ждём его:
            // ожидание здесь заблокировало бы переход на страницу. Но и молча
            // терять сбой нельзя — иначе камера откроется без конфигурации.
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.KIOSK_CONFIG = Object.assign({}, window.KIOSK_CONFIG, " + json + ");")
                .ContinueWith(
                    t => Log.Error(t.Exception, "Проверка камеры: не удалось внедрить конфиг"),
                    TaskContinuationOptions.OnlyOnFaulted);

            core.NavigationCompleted += (_, args) => Log.Information(
                "Проверка камеры: навигация завершена, успех={Success}, статус={Status}",
                args.IsSuccess, args.WebErrorStatus);

            var page = Path.Combine(AppContext.BaseDirectory, "wwwroot", "cameratest.html");
            if (!File.Exists(page))
            {
                Fail("Не найден файл cameratest.html:\n" + page);
                return;
            }

            core.Navigate(new Uri(page).AbsoluteUri);
            Log.Information("Проверка камеры: навигация на {Uri}", page);

            _pollTimer.Start();
        }

        /// <summary>
        /// Забирает отчёт со страницы через ExecuteScriptAsync и пишет в файл.
        /// Возвращает true, когда отчёт получен.
        /// </summary>
        private async Task<bool> TryReadReportAsync()
        {
            if (_browser.CoreWebView2 == null || _attempts >= 20) return false;

            _attempts++;

            try
            {
                var report = await _browser.CoreWebView2.ExecuteScriptAsync(
                    "window.KIOSK_CAMERA_REPORT || ''");

                if (string.IsNullOrWhiteSpace(report)) return false;

                var text = System.Text.Json.JsonSerializer.Deserialize<string>(report);
                if (string.IsNullOrWhiteSpace(text)) return false;

                var path = ReportPath();
                File.WriteAllText(path, text);
                Log.Information("Проверка камеры, попытка {Attempt}:\n{Report}", _attempts, text);

                // Опрашиваем дальше: страница дописывает отчёт по мере
                // готовности (открытие потока, замер, снимок). Останавливаемся
                // только когда состояние стало финальным.
                if (text.Contains("\"state\":\"open\"") || text.Contains("\"state\":\"error"))
                {
                    _pollTimer.Stop();
                    Log.Information("Проверка камеры завершена, отчёт в {Path}", path);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Опрос страницы проверки камеры не удался");
                return false;
            }
        }

        private static string ReportPath() =>
            Path.Combine(AppContext.BaseDirectory, "cameratest-result.json");

        private void Fail(string message)
        {
            Log.Error("Проверка камеры: {Message}", message);

            try
            {
                File.WriteAllText(ReportPath(),
                    "{\"report\":\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") +
                    "\",\"state\":\"error\"}");
            }
            catch
            {
                // Файл отчёта — вспомогательный, его сбой не важен
            }

            MessageBox.Show(this, message,
                "Проверка камеры", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Читает параметры камеры из конфигурации рядом с приложением.
        /// DI здесь недоступно: окно запускается до построения хоста.
        /// </summary>
        private static RegulaOptions LoadRegulaOptions()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                return new ConfigurationBuilder()
                    .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true)
                    .AddJsonFile(Path.Combine(baseDir, "appsettings.local.json"), optional: true)
                    .Build()
                    .GetSection(RegulaOptions.SectionName)
                    .Get<RegulaOptions>() ?? new RegulaOptions();
            }
            catch
            {
                return new RegulaOptions();
            }
        }
    }
}
