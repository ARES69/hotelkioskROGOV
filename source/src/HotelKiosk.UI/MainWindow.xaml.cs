using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HotelKiosk.Application.Services;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using HotelKiosk.Infrastructure.Adapters.Regula;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace HotelKiosk.UI
{
    /// <summary>
    /// Хост WebView2 для фронтенда MONE.
    /// Входящие сообщения — конверт {type:"request",version:1,action,...};
    /// ответы доставляются вызовом window.KioskBridge.receive(json) в UI-потоке.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly KioskActionAdapter _actions;
        private readonly PaymentPollingPolicy _polling;
        private readonly IConfiguration _config;
        private readonly ILogger<MainWindow> _logger;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Адрес собственной страницы киоска — эталон для проверки навигации.</summary>
        private string _kioskUri = "";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Живость интеграций, объявленная странице.
        ///
        /// Фронтенд помечает устройство офлайн, если не получал свежих данных дольше
        /// CONFIG.healthTtl (15 с), и после этого отказывает в любом действии.
        /// Обновлять эти данные должен хост: единственный источник — ответ на
        /// system.preflight, который выполняется один раз при входе. Без периодической
        /// отправки киоск через ~20 с после старта переставал бы работать в боевом
        /// режиме, а в демо это скрывал переход `if(DEMO) return true`.
        /// </summary>
        private readonly IPmsProvider _pms;
        private readonly IRegulaService _regula;
        private readonly IVisionLineService _visionLine;

        /// <summary>Номер обновления: страница отбрасывает устаревшие сообщения.</summary>
        private long _healthSequence;

        public MainWindow()
        {
            InitializeComponent();

            var services = ((App)System.Windows.Application.Current).Host.Services;
            _actions = services.GetRequiredService<KioskActionAdapter>();
            _polling = services.GetRequiredService<PaymentPollingPolicy>();
            _config = services.GetRequiredService<IConfiguration>();
            _logger = services.GetRequiredService<ILogger<MainWindow>>();

            _pms = services.GetRequiredService<IPmsProvider>();
            _regula = services.GetRequiredService<IRegulaService>();
            _visionLine = services.GetRequiredService<IVisionLineService>();

            Loaded += async (_, _) => await InitializeWebViewAsync();
            Closed += (_, _) => _cts.Cancel();
        }

        private async Task InitializeWebViewAsync()
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            GrantCameraPermission();
            RestrictNavigation();

            // Тайминги оплаты отдаём в страницу ДО загрузки kiosk.html: там были
            // собственные константы PAY_POLL_MS/PAY_DEADLINE_MS, которые не
            // учитывали AlfaBankQr:PollIntervalSeconds и QrLifetimeMinutes.
            InjectRuntimeConfig();

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "kiosk.html");
            _kioskUri = new Uri(htmlPath).AbsoluteUri;

            // Демо: движок отвечает сам (mockResult), бэкенд не задействуется
            Browser.CoreWebView2.Navigate(App.IsDemoMode ? _kioskUri + "?demo=1" : _kioskUri);

            StartHealthLoop();
        }

        /// <summary>
        /// Периодически сообщает странице о живости PMS, Regula и VisionLine.
        ///
        /// Интервал заведён меньше CONFIG.healthTtl во фронтенде (15 с): если
        /// сообщение задержится, страница успеет пометить устройство офлайн.
        /// В демо не крутим — там health всегда «зелёный».
        /// </summary>
        private void StartHealthLoop()
        {
            if (App.IsDemoMode) return;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                // 10 с: вдвое чаще TTL, чтобы одна просрочка не гасила киоск
                Interval = TimeSpan.FromSeconds(10)
            };

            timer.Tick += async (_, _) =>
            {
                if (_cts.IsCancellationRequested)
                {
                    timer.Stop();
                    return;
                }

                await PushHealthAsync();
            };

            timer.Start();
        }

        private async Task PushHealthAsync()
        {
            // Замер времени каждой интеграции делаем параллельно: последовательно
            // три пинга по 15 с — это 45 с за цикл, и страница успеет всё погасить
            var pmsTask = SafePingAsync(() => _pms.PingAsync(_cts.Token), "pms");
            var regulaTask = SafePingAsync(() => _regula.PingAsync(_cts.Token), "regula");
            var visionTask = SafePingAsync(() => _visionLine.PingAsync(_cts.Token), "vision");

            await Task.WhenAll(pmsTask, regulaTask, visionTask);

            var sequence = ++_healthSequence;
            var pmsUp = await pmsTask;
            var regulaUp = await regulaTask;
            var visionUp = await visionTask;

            await PushToPageAsync($"window.KioskBridge&&KioskBridge.setDeviceStatus&&KioskBridge.setDeviceStatus(" +
                                 $"'logus',{(pmsUp ? "true" : "false")},{sequence});");
            await PushToPageAsync($"window.KioskBridge&&KioskBridge.setDeviceStatus&&KioskBridge.setDeviceStatus(" +
                                 $"'regula',{(regulaUp ? "true" : "false")},{sequence});");
            await PushToPageAsync($"window.KioskBridge&&KioskBridge.setDeviceStatus&&KioskBridge.setDeviceStatus(" +
                                 $"'vision',{(visionUp ? "true" : "false")},{sequence});");

            // Энкодер живёт на том же мосте VisionLine — отдельного пинга у него нет
            await PushToPageAsync($"window.KioskBridge&&KioskBridge.setDeviceStatus&&KioskBridge.setDeviceStatus(" +
                                 $"'crt',{(visionUp ? "true" : "false")},{sequence});");

            if (!pmsUp || !regulaUp || !visionUp)
                _logger.LogWarning(
                    "Живость интеграций: PMS={Pms}, Regula={Regula}, VisionLine={Vision}",
                    pmsUp, regulaUp, visionUp);
        }

        /// <summary>
        /// Пинг с ограничением по времени: зависшая интеграция не должна
        /// блокировать цикл health — иначе страница погаснет по таймауту.
        /// </summary>
        private static async Task<bool> SafePingAsync(Func<Task<bool>> probe, string name)
        {
            try
            {
                var task = probe();
                var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12)));

                if (done != task) return false;   // не успел — считаем недоступным

                return await task;
            }
            catch
            {
                return false;
            }
        }

        private async Task PushToPageAsync(string script)
        {
            try
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (Browser?.CoreWebView2 == null) return;
                    await Browser.CoreWebView2.ExecuteScriptAsync(script);
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось отправить health в страницу");
            }
        }

        /// <summary>
        /// Замыкает WebView на kiosk.html.
        ///
        /// Без этого страница могла уйти на внешний адрес (например, через
        /// window.location), и тот адрес получил бы право звать мост:
        /// postMessage из чужого origin обрабатывался бы как доверенный запрос
        /// к PMS, платежам и ключам. В киоске за гостем никто не сидит,
        /// поэтому такая навигация — вектор атаки, а не «случайность».
        /// </summary>
        private void RestrictNavigation()
        {
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (IsOwnPage(args.Uri)) return;

                _logger.LogError(
                    "Заблокирована навигация на внешний адрес: {Uri}", args.Uri);

                // Отменяем и возвращаемся на интерфейс киоска
                args.Cancel = true;
                Browser.CoreWebView2.Navigate(_kioskUri);
            };

            // Новые окна запрещены: внешний ресурс не должен открываться
            // поверх экрана гостя
            Browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
        }

        /// <summary>
        /// Своя страница интерфейса киоска.
        ///
        /// Разрешён только сам kiosk.html (с ?demo=1 или без) и ассеты рядом с ним
        /// в wwwroot/assets. Проверка идёт по локальному пути, а не по строке URI:
        /// «file:///C:/kiosk/wwwroot» и «C:\kiosk\wwwroot» — один и тот же путь,
        /// но разные строки. Всё, что не проходит, остаётся без доступа к мосту.
        /// </summary>
        private bool IsOwnPage(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;

            // Точное совпадение с адресом нашей страницы (с учётом ?demo=1)
            if (string.Equals(StripQuery(uri).TrimEnd('/'), _kioskUri.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            var ownDirectory = LocalDirectory(_kioskUri);
            if (string.IsNullOrEmpty(ownDirectory)) return false;

            var candidate = LocalPath(uri);
            if (string.IsNullOrEmpty(candidate)) return false;

            // Ассет лежит в assets/ рядом с kiosk.html и называется как хэш
            var prefix = ownDirectory + "/assets/";
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return Regex.IsMatch(candidate[prefix.Length..],
                @"^[0-9a-f]{12}\.[a-z]{2,4}$", RegexOptions.IgnoreCase);
        }

        private static string StripQuery(string uri) =>
            uri.Split('?')[0].Split('#')[0];

        /// <summary>Локальный путь из file-URI; для других схем — пусто.</summary>
        private static string LocalPath(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
            parsed.IsFile && parsed.LocalPath.Length > 0
                ? parsed.LocalPath.Replace('\\', '/')
                : string.Empty;

        /// <summary>Папка, содержащая kiosk.html, в виде прямого пути.</summary>
        private static string LocalDirectory(string fileUri)
        {
            var path = LocalPath(fileUri);
            if (string.IsNullOrEmpty(path)) return string.Empty;

            var cut = path.LastIndexOf('/');
            return cut > 0 ? path[..cut] : string.Empty;
        }

        /// <summary>
        /// Подкладывает window.KIOSK_CONFIG до выполнения скриптов страницы.
        /// Скрипт выполняется на каждый документ в этом WebView, поэтому значения
        /// всегда актуальны, а kiosk.html перестаёт держать собственные копии
        /// таймингов и работает с конфигом приложения.
        /// </summary>
        private void InjectRuntimeConfig()
        {
            var reg = _config.GetSection(RegulaOptions.SectionName).Get<RegulaOptions>()
                      ?? new RegulaOptions();

            // Источник кадра: явный CaptureSource, иначе считыватель при заданном
            // AgentScanPath и камера во всех остальных случаях.
            var useCamera = string.IsNullOrWhiteSpace(reg.AgentScanPath) ||
                            string.Equals(reg.CaptureSource, "camera", StringComparison.OrdinalIgnoreCase);

            var config = new
            {
                allowDemo = App.IsDemoMode,
                paymentPollMs = (int)_polling.PollInterval.TotalMilliseconds,
                paymentDeadlineMs = (int)_polling.Deadline.TotalMilliseconds,

                // Камера киоска: гость с паспортом в руках попадает в рамку
                camera = new
                {
                    enabled = useCamera,
                    width = reg.CameraWidth,
                    height = reg.CameraHeight,
                    jpegQuality = reg.CameraJpegQuality,
                    countdown = reg.CameraCountdownSeconds
                }
            };

            var script = "window.KIOSK_CONFIG = Object.assign({}, window.KIOSK_CONFIG, " +
                         JsonSerializer.Serialize(config, JsonOpts) + ");";

            try
            {
                Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
            catch (Exception ex)
            {
                // Не критично: kiosk.html имеет собственные значения по умолчанию
                _logger.LogWarning(ex, "Не удалось внедрить KIOSK_CONFIG в страницу");
            }
        }

        /// <summary>
        /// WebView2 по умолчанию спрашивает разрешение на камеру (и молча отказывает
        /// в киоске, где диалога никто не увидит). Задача госта — показать паспорт,
        /// поэтому разрешение выдаём автоматически; в лог пишем факт запроса.
        /// </summary>
        private void GrantCameraPermission()
        {
            try
            {
                Browser.CoreWebView2.PermissionRequested += (_, args) =>
                {
                    if (args.PermissionKind == CoreWebView2PermissionKind.Camera)
                    {
                        args.State = CoreWebView2PermissionState.Allow;
                        _logger.LogInformation("WebView2: разрешён доступ к камере");
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось подписаться на PermissionRequested — камера может быть недоступна");
            }
        }
        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string requestJson;
            try { requestJson = e.WebMessageAsJson; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Нечитаемое сообщение от WebView");
                return;
            }

            // Источник сообщения должен совпадать с нашей страницей. Навигация
            // уже ограничена, но это вторая линия: сообщение может прийти
            // из iframe или из документа, открытого до срабатывания проверки.
            if (!IsOwnPage(e.Source))
            {
                _logger.LogError(
                    "Сообщение в мост отклонено: источник {Source} — не kiosk.html", e.Source);
                return;
            }

            string action = "", requestId = "", operationId = "", sessionId = "";
            long epoch = 0;
            object result = null;
            string errorCode = null;

            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "request")
                    return; // не наш конверт — игнорируем

                action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
                operationId = root.TryGetProperty("operationId", out var o) ? o.GetString() ?? "" : "";
                sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
                epoch = root.TryGetProperty("epoch", out var ep) && ep.ValueKind == JsonValueKind.Number
                    ? ep.GetInt64() : 0;
                // bridgeNonce эхо-нем как есть (строка или null)
                object nonceEcho = root.TryGetProperty("bridgeNonce", out var bn)
                    ? (object)bn.Clone() : null;
                var payload = root.TryGetProperty("payload", out var pl) ? pl.Clone() : default;

                _logger.LogInformation("MONE → {Action} ({RequestId})", action, requestId);

                try
                {
                    result = await _actions.ExecuteAsync(action, payload, sessionId, epoch, _cts.Token);
                }
                catch (KioskActionException kex)
                {
                    errorCode = kex.Code;
                    _logger.LogWarning("MONE {Action} отклонено: {Code}", action, kex.Code);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    errorCode = "API_SERVER_ERROR";
                    _logger.LogError(ex, "MONE {Action}: непредвиденная ошибка", action);
                }

                // Сборка ответа с эхо корреляции
                object response = errorCode != null
                    ? new { type = "response", version = 1, action, requestId, operationId,
                            sessionId, epoch, bridgeNonce = nonceEcho, ok = false,
                            error = new { code = errorCode } }
                    : new { type = "response", version = 1, action, requestId, operationId,
                            sessionId, epoch, bridgeNonce = nonceEcho, ok = true, result };

                await DispatchReceiveAsync(JsonSerializer.Serialize(response, JsonOpts));
            }
            catch (Exception ex)
            {
                // Сырой конверт в журнал не пишем: в нём бывают фамилия гостя
                // и другие данные запроса. Для разбора хватает действия.
                _logger.LogError(ex,
                    "Сбой обработки конверта MONE: action={Action}, requestId={RequestId}",
                    action, requestId);
            }
        }

        /// <summary>Доставить ответ в JS: window.KioskBridge.receive(json).</summary>
        private async Task DispatchReceiveAsync(string json)
        {
            var call = $"window.KioskBridge&&KioskBridge.receive({json});";
            await Dispatcher.InvokeAsync(async () =>
            {
                try { await Browser.CoreWebView2.ExecuteScriptAsync(call); }
                catch (Exception ex) { _logger.LogWarning(ex, "Не доставлен ответ KioskBridge"); }
            });
        }
    }
}
