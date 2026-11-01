using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.VisionLine
{
    /// <summary>
    /// Настройки подключения к VisionLine через локальный HTTP-мост.
    ///
    /// ВАЖНО: сервер VisionLine (192.168.4.55) не говорит по HTTP — порт 4000
    /// это закрытый PMS-протокол ASSA ABLOY (его слушает локальный клиент
    /// Logus на стойке), 27015 — Lock Service. Киоск работает с мостом:
    /// HTTP-сервис на киоск-ПК, внутри которого живёт клиент VisionLine.
    /// Разведка: scripts/find-visionline-client.ps1 (запуск на стойке).
    /// </summary>
    public class VisionLineOptions
    {
        public const string SectionName = "VisionLine";

        /// <summary>Адрес моста (например, локальный сервис на киоск-ПК).</summary>
        public string BaseUrl { get; set; } = "http://localhost:27015";

        /// <summary>Токен/ключ API, если сервер требует авторизацию (Bearer).</summary>
        public string Token { get; set; }

        /// <summary>Идентификатор источника ключей (issuer), если используется.</summary>
        public string IssuerId { get; set; }

        /// <summary>Путь записи карты. По умолчанию — как у модуля Logus Keylock.Rest.</summary>
        public string IssueKeyCardPath { get; set; } = "/IssueKeyCard";

        /// <summary>Путь чтения карты.</summary>
        public string ReadKeyCardPath { get; set; } = "/ReadKeyCard";

        /// <summary>Путь проверки живости сервера.</summary>
        public string HealthPath { get; set; } = "/health";

        public int TimeoutSeconds { get; set; } = 20;

        /// <summary>
        /// Включить фоновый монитор живости VisionLine
        /// (VisionLineHealthMonitor: 3 неудачных health-check подряд →
        /// перезапуск процесса/службы энкодера). Требует прав администратора
        /// на перезапуск службы; по умолчанию выключен.
        /// </summary>
        public bool HealthMonitorEnabled { get; set; } = false;
    }

    /// <summary>
    /// Запись ключ-карт через HTTP REST-интерфейс сервера VisionLine
    /// (192.168.4.55:27015).
    ///
    /// Контракт REST-интерфейса VisionLine закрыт (выдаётся интеграторам
    /// ASSA ABLOY), поэтому пути эндпоинтов вынесены в конфиг: если ваш
    /// сервер отвечает по другим путям — поменяйте IssueKeyCardPath /
    /// ReadKeyCardPath / HealthPath в appsettings, код пересобирать не нужно.
    /// Формат запроса совместим с модулем Logus Keylock.Rest (JSON,
    /// поля room/checkIn/checkOut), ответ разбирается толерантно
    /// (cardUid | uid | id).
    /// </summary>
    public class VisionLineService : IVisionLineService
    {
        private readonly HttpClient _http;
        private readonly ILogger<VisionLineService> _logger;
        private readonly VisionLineOptions _opts;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public VisionLineService(
            HttpClient http, IOptions<VisionLineOptions> opts, ILogger<VisionLineService> logger)
        {
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _http = http;

            _http.BaseAddress = new Uri(_opts.BaseUrl);
            _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);

            if (!string.IsNullOrEmpty(_opts.Token))
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opts.Token);
        }

        public async Task<Result<KeyCardResult>> EncodeKeyCardAsync(
            string roomNumber, DateTime checkIn, DateTime checkOut, CancellationToken ct)
        {
            try
            {
                var payload = new
                {
                    room = roomNumber,
                    checkIn = checkIn.ToString("yyyy-MM-ddTHH:mm:ss"),
                    checkOut = checkOut.ToString("yyyy-MM-ddTHH:mm:ss"),
                    issuerId = _opts.IssuerId
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                _logger.LogInformation(
                    "Запись ключ-карты. Room={Room}, {CheckIn} - {CheckOut}",
                    roomNumber, payload.checkIn, payload.checkOut);

                using var resp = await _http.PostAsync(_opts.IssueKeyCardPath, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "VisionLine вернул HTTP {Status}: {Body}",
                        (int)resp.StatusCode, body);
                    return Result<KeyCardResult>.SystemFailure(
                        $"VisionLine HTTP {(int)resp.StatusCode}: {Truncate(body)}");
                }

                var dto = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<VisionLineEncodeResponse>(body, JsonOpts);

                if (dto == null || string.IsNullOrEmpty(dto.CardUid))
                {
                    _logger.LogWarning("VisionLine ответил без идентификатора карты: {Body}", body);
                    return Result<KeyCardResult>.SystemFailure(
                        $"VisionLine: пустой идентификатор карты: {Truncate(body)}");
                }

                _logger.LogInformation("Ключ-карта записана. CardUid={CardUid}", dto.CardUid);
                return Result<KeyCardResult>.Success(
                    new KeyCardResult(true, dto.CardUid, null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой записи ключ-карты VisionLine");
                return Result<KeyCardResult>.SystemFailure(ex.Message);
            }
        }

        public async Task<bool> PingAsync(CancellationToken ct)
        {
            try
            {
                using var resp = await _http.GetAsync(_opts.HealthPath, ct);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VisionLine health-check не прошёл");
                return false;
            }
        }

        private static string Truncate(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200] + "…");

        private class VisionLineEncodeResponse
        {
            /// <summary>Толерантный разбор: сервер может вернуть cardUid / uid / id.</summary>
            [JsonPropertyName("cardUid")]
            public string CardUidDirect { get; set; }

            [JsonPropertyName("uid")]
            public string Uid { get; set; }

            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonIgnore]
            public string CardUid => CardUidDirect ?? Uid ?? Id;
        }
    }
}
