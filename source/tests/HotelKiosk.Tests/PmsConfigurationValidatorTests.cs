using System.Collections.Generic;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Валидатор конфигурации PMS: главная задача — не дать киоску стартовать
    /// с PMS, у которой не описаны обязательные операции.
    /// </summary>
    public class PmsConfigurationValidatorTests
    {
        private static IConfiguration Build(Dictionary<string, string> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        /// <summary>Минимально рабочая универсальная PMS.</summary>
        private static Dictionary<string, string> WorkingGeneric() => new()
        {
            ["Pms:Provider"] = "generic",
            ["Pms:Generic:BaseUrl"] = "http://pms.test/api",
            ["Pms:Generic:AuthMode"] = "basic",
            ["Pms:Generic:Login"] = "kiosk",
            ["Pms:Generic:PaymentTransactionCode"] = "CASHLESS",

            ["Pms:Generic:Authenticate:Path"] = "reservations/search",
            ["Pms:Generic:Authenticate:Paths:ReservationId"] = "data.id",
            ["Pms:Generic:Authenticate:Paths:ReservationArrival"] = "data.arrival",
            ["Pms:Generic:Authenticate:Paths:ReservationDeparture"] = "data.departure",
            ["Pms:Generic:Authenticate:Paths:ReservationPrice"] = "data.price",

            ["Pms:Generic:CheckIn:Path"] = "reservations/checkin",
            ["Pms:Generic:Cancel:Path"] = "reservations/cancel",
            ["Pms:Generic:AddPayment:Path"] = "reservations/payments",
            ["Pms:Generic:Arrivals:Path"] = "arrivals",
            ["Pms:Generic:ReservationDetails:Path"] = "reservations",
            ["Pms:Generic:Ping:Path"] = "health"
        };

        private static bool HasError(IReadOnlyList<ConfigIssue> issues, string key) =>
            issues.Any(i => i.Severity == ConfigIssueSeverity.Error && i.Key == key);

        private static bool HasWarning(IReadOnlyList<ConfigIssue> issues, string key) =>
            issues.Any(i => i.Severity == ConfigIssueSeverity.Warning && i.Key == key);

        [Fact]
        public void NoDuplicateIssues()
        {
            // Регрессия: ValidateLogus вызывалась и напрямую, и из ValidatePms,
            // из-за чего каждая проблема Logus попадала в отчёт дважды
            var issues = KioskConfigurationValidator.Validate(
                Build(new Dictionary<string, string>()));

            var keys = issues.Select(i => i.Key).ToArray();
            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        [Fact]
        public void LogusProvider_ValidatesLogusSectionOnce()
        {
            var issues = KioskConfigurationValidator.Validate(Build(new Dictionary<string, string>
            {
                ["Pms:Provider"] = "logus"
            }));

            Assert.Single(issues, i => i.Key == "LogusApi:Login / LogusApi:Password");
            Assert.DoesNotContain(issues, i => i.Key.StartsWith("Pms:Generic"));
        }

        [Fact]
        public void GenericProvider_SkipsLogusSection()
        {
            // При выборе generic секция LogusApi не используется и не проверяется
            var cfg = WorkingGeneric();
            cfg["LogusApi:Login"] = "";
            cfg["LogusApi:Password"] = "";

            var issues = KioskConfigurationValidator.Validate(Build(cfg));

            Assert.DoesNotContain(issues, i => i.Key.StartsWith("LogusApi"));
        }

        [Fact]
        public void UnknownProvider_IsError()
        {
            // Молчаливый откат на Logus выглядел бы как «бронь не найдена»
            var issues = KioskConfigurationValidator.Validate(Build(new Dictionary<string, string>
            {
                ["Pms:Provider"] = "shelter"
            }));

            Assert.True(HasError(issues, "Pms:Provider"));
        }

        [Fact]
        public void WorkingGeneric_HasNoPmsBlockers()
        {
            var issues = KioskConfigurationValidator.Validate(Build(WorkingGeneric()));

            Assert.False(HasError(issues, "Pms:Generic:BaseUrl"));
            Assert.False(HasError(issues, "Pms:Generic:Authenticate:Path"));
            Assert.False(HasError(issues, "Pms:Generic:CheckIn:Path"));
        }

        [Fact]
        public void Generic_WithoutSearchOperation_IsError()
        {
            // Без поиска брони заселение невозможно
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:Authenticate:Path"] = "";

            Assert.True(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:Authenticate:Path"));
        }

        [Fact]
        public void Generic_WithoutCheckInOperation_IsError()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:CheckIn:Path"] = "";

            Assert.True(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:CheckIn:Path"));
        }

        [Fact]
        public void Generic_WithoutBaseUrl_IsError()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:BaseUrl"] = "";

            Assert.True(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:BaseUrl"));
        }

        [Fact]
        public void Generic_WithoutAuthLogin_IsWarning()
        {
            // Не блокер: PMS может быть без авторизации, но так чаще опечатка
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:Login"] = "";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:Login"));
        }

        [Fact]
        public void Generic_WithoutToken_IsWarning()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:AuthMode"] = "bearer";
            cfg["Pms:Generic:Token"] = "";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:Token"));
        }

        [Fact]
        public void Generic_UnknownAuthMode_IsWarning()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:AuthMode"] = "jwt-in-query";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:AuthMode"));
        }

        [Fact]
        public void Generic_MissingResponsePaths_AreWarnings()
        {
            // Стартует, но поля придут пустыми — об этом должен знать оператор
            var cfg = WorkingGeneric();
            cfg.Remove("Pms:Generic:Authenticate:Paths:ReservationId");

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:Authenticate:Paths:ReservationId"));
        }

        [Fact]
        public void Generic_MissingOptionalOperations_AreWarningsNotErrors()
        {
            // PMS может не уметь список заездов или паспортные данные —
            // это не повод блокировать старт
            var cfg = WorkingGeneric();
            cfg.Remove("Pms:Generic:Arrivals:Path");
            cfg.Remove("Pms:Generic:ReservationDetails:Path");

            var issues = KioskConfigurationValidator.Validate(Build(cfg));

            Assert.True(HasWarning(issues, "Pms:Generic"));
            Assert.Contains("список заездов", string.Join(" ", issues.Select(i => i.Message)));
            Assert.Contains("данные брони", string.Join(" ", issues.Select(i => i.Message)));
        }

        [Fact]
        public void Generic_TransactionCodeWithoutPaymentUrl_IsWarning()
        {
            // Код есть, а эндпоинта нет: оплата уйдёт гостю, но не в PMS
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:AddPayment:Path"] = "";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:AddPayment:Path"));
        }

        [Fact]
        public void Generic_GuestDocsEnabledWithoutEndpoint_IsWarning()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:SendGuestDocuments"] = "true";
            cfg["Pms:Generic:UpsertGuest:Path"] = "";

            Assert.True(HasWarning(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:UpsertGuest:Path"));
        }

        [Fact]
        public void Generic_MalformedBaseUrl_IsError()
        {
            var cfg = WorkingGeneric();
            cfg["Pms:Generic:BaseUrl"] = "pms.test/api";   // без схемы

            Assert.True(HasError(KioskConfigurationValidator.Validate(Build(cfg)),
                "Pms:Generic:BaseUrl"));
        }

        [Fact]
        public void DefaultProvider_IsLogus_AndChecksLogusSection()
        {
            // Пустой конфиг = боевой Logus, а не универсальный адаптер
            var issues = KioskConfigurationValidator.Validate(Build(new Dictionary<string, string>()));

            Assert.True(HasError(issues, "LogusApi:Login / LogusApi:Password"));
            Assert.False(HasError(issues, "Pms:Generic:Authenticate:Path"));
        }

        [Fact]
        public void ExplicitLogusProvider_IgnoresGenericSection()
        {
            var cfg = new Dictionary<string, string>
            {
                ["Pms:Provider"] = "logus",
                ["LogusApi:BaseUrl"] = "http://192.168.99.9:8020/api",
                ["LogusApi:Login"] = "webapi",
                ["LogusApi:Password"] = "secret",

                // Эта секция игнорируется при выборе logus
                ["Pms:Generic:BaseUrl"] = ""
            };

            var issues = KioskConfigurationValidator.Validate(Build(cfg));

            Assert.False(HasError(issues, "Pms:Generic:BaseUrl"));
            Assert.False(HasError(issues, "LogusApi:Login / LogusApi:Password"));
        }
    }
}
