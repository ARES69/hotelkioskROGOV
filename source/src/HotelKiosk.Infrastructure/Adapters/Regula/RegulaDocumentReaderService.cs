using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Exceptions;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelKiosk.Infrastructure.Adapters.Regula
{
    /// <summary>
    /// Настройки подключения к Regula Document Reader Web API
    /// (https://docs.regulaforensics.com/develop/doc-reader-sdk/web-service).
    /// </summary>
    public class RegulaOptions
    {
        public const string SectionName = "Regula";

        /// <summary>Базовый URL сервиса (по умолчанию локальный на 8080).</summary>
        public string DocumentReaderUrl { get; set; } = "http://localhost:8080/api";

        /// <summary>Сценарий обработки (Scenario из спецификации Regula).</summary>
        public string Scenario { get; set; } = "FullProcess";

        /// <summary>Путь эндпоинта обработки (относительно BaseUrl).</summary>
        public string ProcessPath { get; set; } = "process";

        /// <summary>Путь health-check (GET).</summary>
        public string HealthPath { get; set; } = "ping";

        /// <summary>
        /// Путь агента захвата изображения с физического считывателя (GET,
        /// ответ {"image":"&lt;base64&gt;"}). Web API обрабатывает только готовые
        /// картинки — снимок с устройства делает локальный агент. Пусто =
        /// захват не настроен, сканирование вернёт понятную ошибку.
        /// </summary>
        public string AgentScanPath { get; set; } = "";

        /// <summary>Лицензия (base64), если выдаётся на запрос, а не установлена на сервере.</summary>
        public string LicenseKey { get; set; }

        /// <summary>
        /// Источник кадра: "camera" — фронтальная камера киоска, гость с паспортом
        /// в руках попадает в рамку; "reader" — физический считыватель (AgentScanPath).
        /// Пусто = определяется автоматически: если AgentScanPath задан, используется
        /// считыватель, иначе камера.
        /// </summary>
        public string CaptureSource { get; set; } = "";

        /// <summary>
        /// Ширина кадра, отдаваемого в Regula. 0 = настройки камеры по умолчанию.
        /// Слишком большой кадр заметно замедляет OCR, поэтому для распознавания
        /// паспорта вполне хватает 1280 px по ширине.
        /// </summary>
        public int CameraWidth { get; set; } = 1280;

        /// <summary>Высота кадра, отдаваемого в Regula. 0 = настройки камеры по умолчанию.</summary>
        public int CameraHeight { get; set; } = 960;

        /// <summary>Качество JPEG кадра, 0.1–1.0. Ниже 0.6 MRZ читается нестабильно.</summary>
        public double CameraJpegQuality { get; set; } = 0.9;

        /// <summary>
        /// Обратный отсчёт до съёмки, секунд. Даёт гостю время занять позицию
        /// и не даёт снимку «на ходу».
        /// </summary>
        public int CameraCountdownSeconds { get; set; } = 3;

        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>Формат дат в ответе Regula.</summary>
        public string DateFormat { get; set; } = "yyyy-MM-dd";
    }

    /// <summary>
    /// Адаптер к Regula Document Reader Web API.
    ///
    /// Контракт — официальная OpenAPI-спецификация v8:
    ///   POST {base}/process  — JSON: processParam.scenario + List[].ImageData.image (base64)
    ///   GET  {base}/ping     — живость сервиса
    /// Ответ разбираем толерантно: текстовые поля ищем в контейнере
    /// result_type=36 (TEXT) по числовым fieldType (e-text-field-type):
    ///   DOCUMENT_NUMBER=2, DATE_OF_EXPIRY=3, DATE_OF_ISSUE=4, DATE_OF_BIRTH=5,
    ///   PLACE_OF_BIRTH=6, SURNAME=8, GIVEN_NAMES=9, SEX=12,
    ///   ISSUING_STATE_CODE=1, SURNAME_AND_GIVEN_NAMES=25,
    ///   DOCUMENT_SERIES=56, AUTHORITY_CODE=73, MIDDLE_NAME=146.
    /// </summary>
    public class RegulaDocumentReaderService : IRegulaService
    {
        private readonly HttpClient _http;
        private readonly ILogger<RegulaDocumentReaderService> _logger;
        private readonly RegulaOptions _opts;

        public RegulaDocumentReaderService(
            HttpClient http, IOptions<RegulaOptions> opts, ILogger<RegulaDocumentReaderService> logger)
        {
            _http = http;
            _opts = opts.Value;
            _logger = logger;
            _http.BaseAddress = new Uri(_opts.DocumentReaderUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        }

        public async Task<Result<PassportData>> ScanPassportAsync(CancellationToken ct)
        {
            try
            {
                // 1. Захват изображения документа (агент считывателя).
                var imageB64 = await CaptureImageAsync(ct);
                if (imageB64 == null)
                    return Result<PassportData>.BusinessFailure(
                        "Захват изображения не настроен: задайте Regula:AgentScanPath");

                return await ProcessAsync(imageB64, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой сканирования паспорта");
                return Result<PassportData>.SystemFailure(ex.Message);
            }
        }

        /// <summary>
        /// Распознавание по кадру с камеры киоска (схема «фронтальная камера»).
        /// Тот же /process и тот же разбор, что и для считывателя, — отличается
        /// только источник изображения.
        /// </summary>
        public async Task<Result<PassportData>> RecognizeFromPhotoAsync(
            string imageBase64, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imageBase64))
                return Result<PassportData>.BusinessFailure("Кадр с камеры пуст");

            // data:image/jpeg;base64,... → голый base64 (Regula ждёт именно его)
            var payload = imageBase64;
            var comma = payload.IndexOf(',');
            if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                payload = payload[(comma + 1)..];

            try
            {
                return await ProcessAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой распознавания паспорта по фото с камеры");
                return Result<PassportData>.SystemFailure(ex.Message);
            }
        }

        /// <summary>POST /process с готовым кадром + разбор результата.</summary>
        private async Task<Result<PassportData>> ProcessAsync(string imageBase64, CancellationToken ct)
        {
            using var resp = await _http.PostAsync(_opts.ProcessPath.TrimStart('/'),
                BuildProcessRequest(imageBase64), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return Result<PassportData>.SystemFailure(
                    $"Regula HTTP {(int)resp.StatusCode}" +
                    ((int)resp.StatusCode == 403 ? ": проблема с лицензией" : ""));

            var passport = ParseTextResult(body);
            if (passport == null || string.IsNullOrEmpty(passport.PassportNumber))
                return Result<PassportData>.BusinessFailure("Паспорт не распознан");

            // ПДн в журнал не пишем: ФИО и номер паспорта живут в сессии, а лог
            // переживает её и уходит в диагностику. Для разбора хватает страны:
            // по ней видно, что распознавание сработало.
            _logger.LogInformation(
                "Паспорт распознан: страна {Country}", passport.CountryCode);
            return Result<PassportData>.Success(passport);
        }

        public async Task<bool> PingAsync(CancellationToken ct)
        {
            try
            {
                using var r = await _http.GetAsync(_opts.HealthPath.TrimStart('/'), ct);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ====================================================================
        //  Внутреннее
        // ====================================================================

        private async Task<string> CaptureImageAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opts.AgentScanPath))
                return null;

            using var resp = await _http.GetAsync(_opts.AgentScanPath.TrimStart('/'), ct);
            if (!resp.IsSuccessStatusCode)
                throw new SystemInfrastructureException(
                    $"Агент считывателя HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var name in new[] { "image", "Image", "imageBase64", "data" })
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();

            return null;
        }

        private HttpContent BuildProcessRequest(string imageBase64)
        {
            var request = new Dictionary<string, object>
            {
                ["processParam"] = new Dictionary<string, object>
                {
                    ["scenario"] = _opts.Scenario,
                    // 33 = STATUS (статусы проверки), 36 = TEXT (поля со всех источников)
                    ["resultTypeOutput"] = new[] { 33, 36 },
                    ["dateFormat"] = _opts.DateFormat,
                    ["noGraphics"] = true
                },
                ["List"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["ImageData"] = new Dictionary<string, object> { ["image"] = imageBase64 },
                        ["light"] = 6,
                        ["page_idx"] = 0
                    }
                }
            };

            if (!string.IsNullOrEmpty(_opts.LicenseKey))
                request["systemInfo"] = new Dictionary<string, object> { ["license"] = _opts.LicenseKey };

            return new StringContent(JsonSerializer.Serialize(request),
                Encoding.UTF8, "application/json");
        }

        /// <summary>Толерантный разбор TEXT-контейнера (result_type=36).</summary>
        private PassportData ParseTextResult(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ContainerList", out var cl))
                return null;
            if (!cl.TryGetProperty("containers", out var containers))
                return null;

            foreach (var container in containers.EnumerateArray())
            {
                var rt = container.TryGetProperty("result_type", out var rtEl)
                    ? rtEl.GetInt32() : -1;
                if (rt != 36) continue;

                var fields = new Dictionary<int, string>();
                if (container.TryGetProperty("Text", out var text) &&
                    text.TryGetProperty("fieldList", out var fieldList))
                {
                    foreach (var f in fieldList.EnumerateArray())
                    {
                        var type = f.TryGetProperty("fieldType", out var ftEl) ? ftEl.GetInt32() : -1;
                        var value = GetString(f, "value") ?? GetString(f, "valueOriginal");
                        if (type >= 0 && !string.IsNullOrWhiteSpace(value))
                            fields[type] = value.Trim();
                    }
                }

                return ToPassportData(fields);
            }

            return null;
        }

        private static string? GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static DateTime? GetDate(Dictionary<int, string> f, params int[] types)
        {
            foreach (var t in types)
                if (f.TryGetValue(t, out var s) &&
                    DateTime.TryParse(s, out var d))
                    return d;
            return null;
        }

        private PassportData ToPassportData(Dictionary<int, string> f)
        {
            string F(params int[] types)
            {
                foreach (var t in types)
                    if (f.TryGetValue(t, out var s)) return s;
                return null;
            }

            var series = F(56);                       // DOCUMENT_SERIES
            var number = F(2, 27);                    // DOCUMENT_NUMBER | PASSPORT_NUMBER
            var surname = F(8);                       // SURNAME
            var given = F(9);                         // GIVEN_NAMES
            var middle = F(146);                      // MIDDLE_NAME
            var fullName = F(25);                     // SURNAME_AND_GIVEN_NAMES

            if (string.IsNullOrEmpty(fullName))
            {
                var parts = new[] { surname, given }.Where(s => !string.IsNullOrEmpty(s));
                fullName = string.Join(" ", parts);
            }

            var passportNumber = $"{series} {number}".Trim();
            if (string.IsNullOrEmpty(passportNumber))
                passportNumber = number ?? fullName;  // fallback: что-то да вернуть

            // Имя/отчество: если отчество не пришло отдельным полем — это
            // второй и последующие токены GIVEN_NAMES.
            string firstName = null;
            var givenTokens = given?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (givenTokens != null && givenTokens.Length > 0)
            {
                firstName = givenTokens[0];
                if (string.IsNullOrEmpty(middle) && givenTokens.Length > 1)
                    middle = string.Join(' ', givenTokens[1..]);
            }

            var country = F(1);                       // ISSUING_STATE_CODE ("RUS")
            var sexRaw = F(12);                       // SEX

            return new PassportData(
                PassportNumber: passportNumber,
                FullName: fullName,
                DateOfBirth: GetDate(f, 5),
                Series: series,
                FirstName: firstName,
                LastName: surname,
                MiddleName: middle,
                Sex: NormalizeSex(sexRaw),
                BirthPlace: F(6),
                CountryCode: string.IsNullOrEmpty(country) ? "RUS" : country.ToUpperInvariant(),
                IssueDate: GetDate(f, 4),
                DepartmentCode: F(73),
                IssuedBy: F(24));
        }

        private static string NormalizeSex(string sex)
        {
            if (string.IsNullOrEmpty(sex)) return null;
            return char.ToUpperInvariant(sex.Trim()[0]) switch
            {
                'M' or 'М' => "M",
                'F' or 'Ж' or 'T' => "F",
                _ => null
            };
        }
    }
}
