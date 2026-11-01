using System.Collections.Generic;
using System.Linq;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HotelKiosk.Tests
{
    public class KioskConfigurationValidatorTests
    {
        private static IConfiguration Build(Dictionary<string, string> values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

        /// <summary>Конфиг боевого отеля: все обязательные ключи заполнены.</summary>
        private static Dictionary<string, string> FullyConfigured() => new()
        {
            ["LogusApi:BaseUrl"] = "http://192.168.99.9:8020/api",
            ["LogusApi:Login"] = "webapi",
            ["LogusApi:Password"] = "secret",
            ["LogusApi:PaymentTransactionCode"] = "CASHLESS",
            ["Regula:DocumentReaderUrl"] = "http://localhost:8080/api",
            ["Regula:AgentScanPath"] = "scan",
            ["Regula:CaptureSource"] = "reader",
            ["VisionLine:BaseUrl"] = "http://192.168.4.55:27015",
            ["VisionLine:Token"] = "token",
            ["AlfaBankQr:Login"] = "login",
            ["AlfaBankQr:Password"] = "password",
            ["AlfaBankQr:TestMode"] = "false",
            ["AlfaBankQr:PollIntervalSeconds"] = "5",
            ["Telegram:Enabled"] = "true",
            ["Telegram:BotToken"] = "123:ABC",
            ["Telegram:ChatId"] = "-100123",
            ["CardTerminal:Enabled"] = "true",
            ["CardTerminal:BaseUrl"] = "http://localhost:8090"
        };

        private static bool HasError(IReadOnlyList<ConfigIssue> issues, string key) =>
            issues.Any(i => i.Severity == ConfigIssueSeverity.Error && i.Key == key);

        private static bool HasWarning(IReadOnlyList<ConfigIssue> issues, string key) =>
            issues.Any(i => i.Severity == ConfigIssueSeverity.Warning && i.Key == key);

        [Fact]
        public void FullyConfigured_NoIssues()
        {
            var issues = KioskConfigurationValidator.Validate(Build(FullyConfigured()));
            Assert.Empty(issues);
        }

        [Fact]
        public void EmptyConfig_ReportsEveryBlocker()
        {
            var issues = KioskConfigurationValidator.Validate(Build(new Dictionary<string, string>()));

            // Пустая поставка: эти ключи роняли флоу гостя по дороге
            Assert.True(HasError(issues, "AlfaBankQr:Login / AlfaBankQr:Password"));
            Assert.True(HasError(issues, "Telegram:BotToken / Telegram:ChatId"));
            Assert.True(HasError(issues, "LogusApi:Login / LogusApi:Password"));
            Assert.True(HasError(issues, "LogusApi:BaseUrl"));

            // Оплата дойдёт до гостя, но не попадёт на folio брони — предупреждение
            Assert.True(HasWarning(issues, "LogusApi:PaymentTransactionCode"));
        }

        [Fact]
        public void TelegramDisabled_NoTelegramError()
        {
            var cfg = FullyConfigured();
            cfg["Telegram:Enabled"] = "false";
            cfg["Telegram:BotToken"] = "";
            cfg["Telegram:ChatId"] = "";

            Assert.DoesNotContain(
                KioskConfigurationValidator.Validate(Build(cfg)),
                i => i.Key.Contains("Telegram"));
        }

        [Fact]
        public void CardTerminalEnabledWithoutBaseUrlKey_UsesClassDefault_NoError()
        {
            var cfg = FullyConfigured();
            cfg.Remove("CardTerminal:BaseUrl");

            Assert.False(HasError(
                KioskConfigurationValidator.Validate(Build(cfg)), "CardTerminal:BaseUrl"));
        }

        // ===== источник кадра: камера / считыватель =====

        [Fact]
        public void CaptureSourceCamera_NoAgentScanPathNeeded()
        {
            // Фронтальная камера: агент-считыватель не участвует, поэтому пустой
            // AgentScanPath — это норма, а не блокер
            var cfg = FullyConfigured();
            cfg["Regula:CaptureSource"] = "camera";
            cfg["Regula:AgentScanPath"] = "";

            Assert.False(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Regula:AgentScanPath"));
        }

        [Fact]
        public void CaptureSourceReader_WithoutAgentScanPath_IsError()
        {
            var cfg = FullyConfigured();
            cfg["Regula:CaptureSource"] = "reader";
            cfg["Regula:AgentScanPath"] = "";

            Assert.True(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Regula:AgentScanPath"));
        }

        [Fact]
        public void UnknownCaptureSource_IsWarning()
        {
            var cfg = FullyConfigured();
            cfg["Regula:CaptureSource"] = "webcam";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Regula:CaptureSource"));
        }

        [Fact]
        public void LowJpegQuality_IsWarning()
        {
            var cfg = FullyConfigured();
            cfg["Regula:CameraJpegQuality"] = "0.4";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Regula:CameraJpegQuality"));
        }

        [Fact]
        public void OutOfRangeJpegQuality_IsWarning()
        {
            var cfg = FullyConfigured();
            cfg["Regula:CameraJpegQuality"] = "1.5";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Regula:CameraJpegQuality"));
        }

        [Fact]
        public void CardTerminalEnabledWithGarbageBaseUrl_IsError()
        {
            var cfg = FullyConfigured();
            cfg["CardTerminal:BaseUrl"] = "localhost:8090";   // без схемы — HttpClient это не примет

            Assert.True(HasError(
                KioskConfigurationValidator.Validate(Build(cfg)), "CardTerminal:BaseUrl"));
        }

        [Fact]
        public void CardTerminalDisabled_IsWarningOnly()
        {
            var cfg = FullyConfigured();
            cfg["CardTerminal:Enabled"] = "false";

            var issues = KioskConfigurationValidator.Validate(Build(cfg));
            Assert.True(HasWarning(issues, "CardTerminal:Enabled"));
            Assert.DoesNotContain(issues, i => i.Severity == ConfigIssueSeverity.Error);
        }

        [Fact]
        public void AlfaBankTestMode_IsWarning()
        {
            var cfg = FullyConfigured();
            cfg["AlfaBankQr:TestMode"] = "true";

            var issues = KioskConfigurationValidator.Validate(Build(cfg));
            Assert.True(HasWarning(issues, "AlfaBankQr:TestMode"));
            Assert.DoesNotContain(issues, i => i.Severity == ConfigIssueSeverity.Error
                                              && i.Key == "AlfaBankQr:TestMode");
        }

        [Fact]
        public void NonPositivePollInterval_IsError()
        {
            var cfg = FullyConfigured();
            cfg["AlfaBankQr:PollIntervalSeconds"] = "0";

            Assert.True(HasError(
                KioskConfigurationValidator.Validate(Build(cfg)), "AlfaBankQr:PollIntervalSeconds"));
        }

        [Fact]
        public void FormatForOperator_GroupsErrorsBeforeWarnings()
        {
            var cfg = FullyConfigured();
            cfg["Regula:AgentScanPath"] = "";
            cfg["AlfaBankQr:TestMode"] = "true";

            var text = KioskConfigurationValidator.FormatForOperator(
                KioskConfigurationValidator.Validate(Build(cfg)));

            Assert.NotNull(text);
            Assert.Contains("НЕ РАБОТАЕТ", text);
            Assert.Contains("ПРЕДУПРЕЖДЕНИЯ", text);
            Assert.True(text.IndexOf("НЕ РАБОТАЕТ") < text.IndexOf("ПРЕДУПРЕЖДЕНИЯ"));
        }

        [Fact]
        public void FormatForOperator_NoIssues_ReturnsNull()
        {
            Assert.Null(KioskConfigurationValidator.FormatForOperator(
                KioskConfigurationValidator.Validate(Build(FullyConfigured()))));
        }
    }
}
