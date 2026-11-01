using System;
using System.IO;
using System.Linq;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Проверяет конфиги, которые реально отдаются оператору киоска.
    ///
    /// Смысл в том, что поставка едет с пустыми секретами намеренно (appsettings.json
    /// в .gitignore). Тест фиксирует два свойства: json валиден, и валидатор
    /// находит ровно те блокеры, о которых мы говорим в README, — чтобы оператор
    /// не получил «пустой» киоск молча.
    /// </summary>
    public class ShippedConfigurationTests
    {
        private static readonly string RepoRoot = FindRepoRoot();

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HotelKiosk.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);   // тесты запускаются изнутри solution
            return dir.FullName;
        }

        private static IConfiguration LoadUiConfig(params string[] extraFiles)
        {
            var builder = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepoRoot, "src", "HotelKiosk.UI", "appsettings.json"),
                    optional: false);

            foreach (var f in extraFiles)
                builder.AddJsonFile(Path.Combine(RepoRoot, "src", "HotelKiosk.UI", f), optional: false);

            return builder.Build();
        }

        [Fact]
        public void UiAppsettings_IsValidJson_AndParses()
        {
            // Неявная проверка: AddJsonFile с optional:false бросит исключение,
            // если файл битый или его нет. Дополнительно убеждаемся, что
            // обязательные секции на месте.
            var c = LoadUiConfig();

            foreach (var section in new[] { "Pms", "LogusApi", "Regula", "VisionLine", "Telegram",
                                            "AlfaBankQr", "CardTerminal",
                                            "CheckInStateMachine", "Serilog" })
                Assert.True(c.GetSection(section).Exists(), $"секция {section} отсутствует");
        }

        [Fact]
        public void UiAppsettings_DefaultsToLogusProvider()
        {
            // По умолчанию — боевой Logus. Молчаливый откат с неизвестного
            // кода PMS на Logus выглядел бы как «бронь не найдена»
            var c = LoadUiConfig();

            Assert.Equal("logus", c["Pms:Provider"]);
        }

        [Fact]
        public void UiExample_IsValidJson_AndHasNoRealSecrets()
        {
            var c = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepoRoot, "src", "HotelKiosk.UI", "appsettings.example.json"),
                    optional: false)
                .Build();

            // В шаблоне не должно быть настоящих кредов
            var secrets = new[] { "LogusApi:Password", "LogusApi:Login", "AlfaBankQr:Password",
                                  "Telegram:BotToken", "VisionLine:Token" };

            foreach (var key in secrets)
            {
                var v = c[key];
                if (string.IsNullOrEmpty(v)) continue;
                Assert.StartsWith("YOUR_", v);
            }
        }

        [Fact]
        public void ShippedUiConfig_ReportsExpectedBlockers()
        {
            var issues = KioskConfigurationValidator.Validate(LoadUiConfig());

            // Поставка едет с камерой: Regula:AgentScanPath пуст намеренно,
            // и валидатор не должен считать это блокером
            Assert.DoesNotContain(issues, i => i.Key == "Regula:AgentScanPath");

            // Эти функции не работают в поставке как есть
            Assert.Contains(issues, i => i.Severity == ConfigIssueSeverity.Error
                                         && i.Key == "AlfaBankQr:Login / AlfaBankQr:Password");
            Assert.Contains(issues, i => i.Severity == ConfigIssueSeverity.Error
                                         && i.Key == "Telegram:BotToken / Telegram:ChatId");

            // И этот ключ: оплата пройдёт, но не попадёт на folio
            Assert.Contains(issues, i => i.Severity == ConfigIssueSeverity.Warning
                                         && i.Key == "LogusApi:PaymentTransactionCode");
        }

        [Fact]
        public void ShippedUiConfig_HasNoHardcodedPasswords()
        {
            var c = LoadUiConfig();

            foreach (var key in new[] { "LogusApi:Password", "LogusApi:Login", "AlfaBankQr:Password",
                                        "AlfaBankQr:Login", "Telegram:BotToken", "Telegram:ChatId",
                                        "VisionLine:Token", "VisionLine:IssuerId" })
            {
                Assert.True(string.IsNullOrWhiteSpace(c[key]),
                    $"{key} не должен содержать значение в поставляемом appsettings.json");
            }
        }

        [Fact]
        public void ShippedUiConfig_DeclaresEveryOptionClassKey()
        {
            // Регрессия на «мёртвый» конфиг: секция есть, а код её не читает.
            // Проверяем ПРИСУТСТВИЕ ключа, а не значение: секреты намеренно пустые.
            var c = LoadUiConfig();
            var declared = c.AsEnumerable()
                .Where(kv => kv.Value != null || c[kv.Key] != null)
                .Select(kv => kv.Key)
                .ToHashSet();

            foreach (var key in new[]
                     {
                         "LogusApi:BaseUrl", "LogusApi:Login", "LogusApi:TimeoutSeconds",
                         "LogusApi:PropertyId", "LogusApi:PaymentTransactionCode",
                         "LogusApi:SendGuestDocuments", "LogusApi:DocumentTypeCode",
                         "Regula:DocumentReaderUrl", "Regula:Scenario", "Regula:ProcessPath",
                         "Regula:HealthPath", "Regula:AgentScanPath", "Regula:DateFormat",
                         "Regula:TimeoutSeconds", "Regula:CaptureSource",
                         "Regula:CameraWidth", "Regula:CameraHeight",
                         "Regula:CameraJpegQuality", "Regula:CameraCountdownSeconds",
                         "VisionLine:BaseUrl", "VisionLine:Token", "VisionLine:IssueKeyCardPath",
                         "VisionLine:ReadKeyCardPath", "VisionLine:HealthPath",
                         "VisionLine:TimeoutSeconds", "VisionLine:HealthMonitorEnabled",
                         "Telegram:Enabled", "Telegram:BotToken", "Telegram:ChatId",
                         "AlfaBankQr:TestMode", "AlfaBankQr:BaseUrl", "AlfaBankQr:TestBaseUrl",
                         "AlfaBankQr:ReturnUrl", "AlfaBankQr:QrLifetimeMinutes",
                         "AlfaBankQr:PollIntervalSeconds", "AlfaBankQr:RequestTimeoutSeconds",
                         "CardTerminal:Enabled", "CardTerminal:BaseUrl", "CardTerminal:ChargePath",
                         "CardTerminal:StatusPath", "CardTerminal:RefundPath",
                         "CardTerminal:HealthPath", "CardTerminal:TimeoutSeconds",
                         "CardTerminal:PollIntervalSeconds",
                         "CheckInStateMachine:MaxRetries", "CheckInStateMachine:TimeoutSeconds",
                         "CheckInStateMachine:CompensationRetries",
                         "CheckInStateMachine:PaymentCheckTimeoutSeconds",
                         "CheckInStateMachine:PaymentPollIntervalSeconds",
                         "CheckInStateMachine:PaymentCheckRetries",
                         "Serilog:MinimumLevel"
                     })
            {
                Assert.Contains(key, declared);
            }
        }

        [Fact]
        public void NoMachineSpecificConfigCommitted()
        {
            // appsettings.NOTEPC.json с портом 8080 молча перекрывал правильные
            // 8020 на машине с именем NOTEPC. Такие файлы создаются локально.
            var uiDir = Path.Combine(RepoRoot, "src", "HotelKiosk.UI");
            var machineSpecific = Directory.GetFiles(uiDir, "appsettings.*.json")
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.EndsWith("example.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.DoesNotContain(machineSpecific,
                n => n.Equals("appsettings.NOTEPC.json", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void LocalAgentAppsettings_IsValidJson_AndHasNoCredentials()
        {
            var c = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepoRoot, "src", "HotelKiosk.LocalAgent", "appsettings.json"),
                    optional: false)
                .Build();

            Assert.True(string.IsNullOrWhiteSpace(c["LogusApi:Password"]));
            Assert.True(string.IsNullOrWhiteSpace(c["LogusApi:Login"]));

            // Раньше секции Regula не было вовсе — код молча брал дефолты класса
            Assert.True(c.GetSection("Regula").Exists());
        }
    }
}
