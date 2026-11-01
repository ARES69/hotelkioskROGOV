using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Notifications
{
    /// <summary>
    /// Настройки Telegram-уведомлений персоналу.
    /// Бот создаётся через @BotFather, chat_id — id группы/чата стойки
    /// (узнать: добавить бота в группу, открыть https://api.telegram.org/bot{token}/getUpdates).
    /// </summary>
    public class TelegramOptions
    {
        public const string SectionName = "Telegram";

        /// <summary>Токен бота от @BotFather. Пусто — уведомления отключены.</summary>
        public string BotToken { get; set; }

        /// <summary>Chat id получателя (группа стойки регистрации).</summary>
        public string ChatId { get; set; }

        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Отправка уведомлений персоналу через Bot API Telegram
    /// (POST https://api.telegram.org/bot{token}/sendMessage).
    /// Ошибки глотаются с логом — уведомление никогда не должно ломать заселение.
    /// </summary>
    public class TelegramNotifier : INotificationService
    {
        private readonly HttpClient _http;
        private readonly ILogger<TelegramNotifier> _logger;
        private readonly TelegramOptions _opts;
        private readonly bool _configured;

        public TelegramNotifier(
            HttpClient http, IOptions<TelegramOptions> opts, ILogger<TelegramNotifier> logger)
        {
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _http = http;
            _http.Timeout = TimeSpan.FromSeconds(10);
            _configured = _opts.Enabled &&
                          !string.IsNullOrWhiteSpace(_opts.BotToken) &&
                          !string.IsNullOrWhiteSpace(_opts.ChatId);
        }

        public async Task NotifyStaffAsync(string message, CancellationToken ct)
        {
            try
            {
                if (!_configured)
                {
                    // Раньше это уходило в LogDebug и полностью терялось. Уведомления
                    // — единственный сигнал стойке о том, что ключ не записан, поэтому
                    // молчание здесь означало бы «персонал не узнает о сбое».
                    _logger.LogWarning(
                        "Telegram-уведомление не отправлено ({Reason}): {Message}",
                        !_opts.Enabled ? "Enabled=false" : "не задан BotToken или ChatId",
                        message);
                    return;
                }

                var payload = new
                {
                    chat_id = _opts.ChatId,
                    text = message,
                    disable_web_page_preview = true
                };

                var url = $"https://api.telegram.org/bot{_opts.BotToken}/sendMessage";
                var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var resp = await _http.PostAsync(url, content, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Telegram вернул HTTP {Status}: {Body}",
                        (int)resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось отправить Telegram-уведомление");
            }
        }
    }
}
