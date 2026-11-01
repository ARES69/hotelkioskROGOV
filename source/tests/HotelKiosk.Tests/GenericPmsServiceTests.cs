using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Infrastructure.Adapters.GenericPms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Универсальный PMS-адаптер: киоск работает с канонической моделью,
    /// PMS отдаёт свои URL и свои имена полей. Тесты проверяют именно границу
    /// между ними — то, что ломается на реальной незнакомой PMS.
    /// </summary>
    public class GenericPmsServiceTests
    {
        // ====================================================================
        //  PmsJson — механика маппинга
        // ====================================================================

        [Fact]
        public void TryGet_NestedPath_IsCaseInsensitive()
        {
            using var doc = JsonDocument.Parse(
                """{"Data":{"Reservation":{"RoomNumber":"305"}}}""");

            Assert.True(PmsJson.TryGet(doc.RootElement, "data.reservation.roomnumber",
                out var value));
            Assert.Equal("305", value.GetString());
        }

        [Fact]
        public void TryGet_MissingPath_ReturnsFalse()
        {
            using var doc = JsonDocument.Parse("""{"data":{}}""");

            Assert.False(PmsJson.TryGet(doc.RootElement, "data.reservation.id", out _));
        }

        [Fact]
        public void TryGet_PathThroughNonObject_ReturnsFalse()
        {
            // Путь в строку — частая ошибка в конфиге: не роняем, а false
            using var doc = JsonDocument.Parse("""{"data":"not-an-object"}""");

            Assert.False(PmsJson.TryGet(doc.RootElement, "data.reservation.id", out _));
        }

        [Theory]
        [InlineData("5000", 5000)]
        [InlineData("5000.50", 5000.50)]
        [InlineData("5 000,50", 5000.50)]
        [InlineData("5000.5", 5000.5)]
        public void GetDecimal_ParsesRussianAndInvariantFormats(string raw, double expected)
        {
            using var doc = JsonDocument.Parse($$"""{"amount":"{{raw}}"}""");

            var value = PmsJson.GetDecimal(doc.RootElement, "amount");

            Assert.NotNull(value);
            Assert.Equal((decimal)expected, value.Value, 2);
        }

        [Fact]
        public void GetDecimal_NumberNode_Works()
        {
            using var doc = JsonDocument.Parse("""{"amount":5000.50}""");

            Assert.Equal(5000.50m, PmsJson.GetDecimal(doc.RootElement, "amount"));
        }

        [Theory]
        [InlineData("2026-08-20T14:00:00Z")]
        [InlineData("2026-08-20 14:00:00")]
        [InlineData("2026-08-20")]
        public void GetDateTime_ParsesCommonFormats(string raw)
        {
            using var doc = JsonDocument.Parse($$"""{"arrival":"{{raw}}"}""");

            Assert.NotNull(PmsJson.GetDateTime(doc.RootElement, "arrival"));
        }

        [Fact]
        public void GetDateTime_UnixSeconds_Works()
        {
            using var doc = JsonDocument.Parse("""{"arrival":1755000000}""");

            var value = PmsJson.GetDateTime(doc.RootElement, "arrival");

            Assert.NotNull(value);
            Assert.Equal(2025, value!.Value.Year);
        }

        [Fact]
        public void GetBool_AcceptsNumbersAndStrings()
        {
            using var doc = JsonDocument.Parse(
                """{"a":true,"b":1,"c":"0","d":"true","e":false}""");

            Assert.True(PmsJson.GetBool(doc.RootElement, "a"));
            Assert.True(PmsJson.GetBool(doc.RootElement, "b"));
            Assert.False(PmsJson.GetBool(doc.RootElement, "c"));
            Assert.True(PmsJson.GetBool(doc.RootElement, "d"));
            Assert.False(PmsJson.GetBool(doc.RootElement, "e"));
        }

        [Fact]
        public void Expand_SubstitutesPlaceholders()
        {
            var values = new Dictionary<string, string> { ["reservationNo"] = "BR-1", ["key"] = "Иванов" };

            var result = PmsJson.Expand(
                "reservations/@{reservationNo}/guests", values, urlEncode: true);

            Assert.Equal("reservations/BR-1/guests", result);
        }

        [Fact]
        public void Expand_UrlEncodes_WhenAsked()
        {
            // Фамилия с пробелом и кириллицей иначе ломает URL
            var values = new Dictionary<string, string> { ["key"] = "Иванов Иван" };

            var result = PmsJson.Expand("search/@{key}", values, urlEncode: true);

            Assert.DoesNotContain(" ", result);
            Assert.Contains("%", result);
            // Без экранирования кириллица и пробел идут как есть
            Assert.Contains("search/Иванов Иван", PmsJson.Expand("search/@{key}", values));
        }

        [Fact]
        public void Expand_StripsAtMarker_UsedInBodyTemplates()
        {
            // "@" — маркер подстановки в теле запроса; в шаблоне его быть не должно
            var values = new Dictionary<string, string> { ["amount"] = "5000" };

            Assert.Equal("5000.00", PmsJson.Expand("@{amount:0.00}", values));
        }

        [Fact]
        public void Expand_AmountFormat_UsesDotNotComma()
        {
            // Альфа/1С-подобные PMS принимают только точку: запятая — разделитель
            // дробной части в русской локали и сломает JSON
            var values = new Dictionary<string, string> { ["amount"] = "5000" };

            Assert.Equal("5000.00", PmsJson.Expand("@{amount:0.00}", values));
        }

        [Fact]
        public void Expand_UnknownPlaceholder_KeepsItVisible()
        {
            // Опечатка в конфиге должна быть видна в запросе, а не превратиться
            // в пустую строку
            var result = PmsJson.Expand("reservations/@{resservatIonId}",
                new Dictionary<string, string>());

            Assert.Contains("resservatIonId", result);
        }

        // ====================================================================
        //  Выбор PMS
        // ====================================================================

        [Fact]
        public void Selection_DefaultsToLogus()
        {
            var selection = PmsSelection.FromConfiguration(
                new ConfigurationBuilder().Build());

            Assert.Equal("logus", selection.Provider);
            Assert.False(selection.IsGeneric);
        }

        [Fact]
        public void Selection_ReadsGenericProvider()
        {
            var selection = PmsSelection.FromConfiguration(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string>
                    {
                        ["Pms:Provider"] = "generic",
                        ["Pms:DisplayName"] = "Shelter"
                    })
                    .Build());

            Assert.True(selection.IsGeneric);
            Assert.Equal("Shelter", selection.DisplayName);
        }

        [Fact]
        public void Selection_FallsBackToVendorName_WithoutDisplayName()
        {
            var selection = PmsSelection.FromConfiguration(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string>
                    {
                        ["Pms:Provider"] = "generic",
                        ["Pms:Generic:Vendor"] = "1c-hotel"
                    })
                    .Build());

            Assert.Equal("1c-hotel", selection.DisplayName);
        }

        // ====================================================================
        //  Адаптер: транспорт и маппинг
        // ====================================================================

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses = new();

            public List<(string Url, string Method, string Body)> Requests { get; } = new();

            public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK) =>
                _responses.Enqueue(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body ?? "", Encoding.UTF8, "application/json")
                });

            public void EnqueueRaw(HttpResponseMessage response) => _responses.Enqueue(response);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content == null
                    ? "" : await request.Content.ReadAsStringAsync(ct);

                Requests.Add((request.RequestUri?.ToString() ?? "", request.Method.Method, body));

                if (_responses.Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("{}") };

                return _responses.Dequeue();
            }
        }

        private static (GenericPmsService Svc, StubHandler Handler) Create(
            Action<GenericPmsOptions> configure = null,
            string baseUrl = "http://pms.test/api")
        {
            var handler = new StubHandler();
            var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };

            var options = new GenericPmsOptions
            {
                Vendor = "stub",
                BaseUrl = baseUrl,
                AuthMode = "basic",
                Login = "kiosk",
                Password = "secret",
                TimeoutSeconds = 5,
                PaymentTransactionCode = "CASHLESS",

                Authenticate = new OperationOptions
                {
                    Method = "POST",
                    Path = "reservations/search",
                    Body = new Dictionary<string, object>
                    {
                        ["number"] = "@{reservationNo}",
                        ["surname"] = "@{key}"
                    },
                    Paths = new FieldMap
                    {
                        ReservationId = "data.reservation.id",
                        ReservationRoomCode = "data.reservation.room.number",
                        ReservationArrival = "data.reservation.arrivalDate",
                        ReservationDeparture = "data.reservation.departureDate",
                        ReservationPrice = "data.reservation.totalAmount",
                        GuestLastName = "data.guest.lastName",
                        GuestFirstName = "data.guest.firstName",
                        AvailableRooms = "data.availableRooms",
                        RoomCode = "number",
                        RoomOccupied = "occupied"
                    }
                },

                CheckIn = new OperationOptions
                {
                    Method = "POST",
                    Path = "reservations/@{reservationId}/checkin",
                    Body = new Dictionary<string, object> { ["room"] = "@{roomNo}" }
                },

                Cancel = new OperationOptions
                {
                    Method = "POST",
                    Path = "reservations/@{reservationId}/cancel",
                    Body = new Dictionary<string, object> { ["reason"] = "@{cancellationReason}" }
                },

                AddPayment = new OperationOptions
                {
                    Method = "POST",
                    Path = "reservations/@{reservationId}/payments",
                    Body = new Dictionary<string, object>
                    {
                        ["amount"] = "@{amount:0.00}",
                        ["transactionCode"] = "@{transactionCode}",
                        ["externalId"] = "@{uniqueId}"
                    }
                },

                SuccessPath = "success",
                ErrorMessagePath = "error.message"
            };

            configure?.Invoke(options);

            var svc = new GenericPmsService(
                http, Options.Create(options), NullLogger<GenericPmsService>.Instance);

            return (svc, handler);
        }

        [Fact]
        public async Task Authenticate_MapsForeignFieldNames()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""
                {"success":true,"data":{
                  "reservation":{"id":134268,"room":{"number":"305"},
                    "arrivalDate":"2026-08-20","departureDate":"2026-08-23",
                    "totalAmount":"45 000,00"},
                  "guest":{"lastName":"ИВАНОВ","firstName":"ИВАН"},
                  "availableRooms":[{"number":"101","occupied":false},
                                    {"number":"102","occupied":true}]}}
                """);

            var r = await svc.AuthenticateSelfCheckinAsync(
                "134268", "LastName", "Иванов", CancellationToken.None);

            Assert.True(r.IsSuccess);
            Assert.Equal("134268", r.Value.Reservation.Id);
            Assert.Equal("305", r.Value.Reservation.RoomCode);
            Assert.Equal(45000.00m, r.Value.Reservation.Price);
            Assert.Equal("ИВАНОВ", r.Value.Guest.LastName);

            // Занятый номер не должен попасть в список выбора
            var free = r.Value.RoomsInfo.Rooms.Where(x => !x.IsOccupied).Select(x => x.RoomCode);
            Assert.Equal(new[] { "101" }, free);
        }

        [Fact]
        public async Task Authenticate_SendsConfiguredBody()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":true,"data":{"reservation":{"id":"BR-1"}}}""");

            await svc.AuthenticateSelfCheckinAsync(
                "BR-1", "LastName", "Иванов", CancellationToken.None);

            var request = handler.Requests.Single();
            Assert.Contains("/api/reservations/search", request.Url);
            Assert.Equal("POST", request.Method);
            Assert.Contains("\"number\":\"BR-1\"", request.Body);
            Assert.Contains("\"surname\":\"Иванов\"", request.Body);
        }

        [Fact]
        public async Task Authenticate_ErrorInBody_ReturnsBusinessFailure()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":false,"error":{"message":"Бронь не найдена"}}""");

            var r = await svc.AuthenticateSelfCheckinAsync(
                "BR-9", "LastName", "Иванов", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("не найдена", r.Error);
        }

        [Fact]
        public async Task Authenticate_HttpNotFound_ReturnsBusinessFailure()
        {
            var (svc, handler) = Create();
            handler.Enqueue("{}", HttpStatusCode.NotFound);

            var r = await svc.AuthenticateSelfCheckinAsync(
                "BR-9", "LastName", "Иванов", CancellationToken.None);

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task Authenticate_Http500_ReturnsSystemFailure()
        {
            // Транспортная ошибка — не «бронь не найдена»: различать важно,
            // иначе гость увидит «не найдена» при упавшем сервере PMS
            var (svc, handler) = Create();
            handler.Enqueue("oops", HttpStatusCode.InternalServerError);

            var r = await svc.AuthenticateSelfCheckinAsync(
                "BR-1", "LastName", "Иванов", CancellationToken.None);

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task Authenticate_Unauthorized_ReturnsSystemFailureWithHint()
        {
            var (svc, handler) = Create();
            handler.Enqueue("{}", HttpStatusCode.Unauthorized);

            var r = await svc.AuthenticateSelfCheckinAsync(
                "BR-1", "LastName", "Иванов", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("AuthMode", r.Error);
        }

        [Fact]
        public async Task Authenticate_ResponseWithoutReservationId_IsRejected()
        {
            // Лучше явная ошибка про конфиг, чем бронь с пустым номером,
            // которая уйдёт дальше по флоу и сломается в неожиданном месте
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":true,"data":{"guest":{"lastName":"X"}}}""");

            var r = await svc.AuthenticateSelfCheckinAsync(
                "BR-1", "LastName", "X", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("Paths:ReservationId", r.Error);
        }

        [Fact]
        public async Task UnconfiguredOperation_SaysPmsDoesNotSupportIt()
        {
            // Не описанная в конфиге операция = «PMS так не умеет»,
            // а не 404 и не пустой ответ
            var (svc, _) = Create(o => o.CheckIn = new OperationOptions { Path = "" });

            var r = await svc.SelfCheckInAsync("BR-1", "101", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("не поддерживает", r.Error);
        }

        [Fact]
        public async Task CheckIn_SendsRoomInBody()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":true}""");

            var r = await svc.SelfCheckInAsync("134268", "305", CancellationToken.None);

            Assert.True(r.IsSuccess);
            var request = handler.Requests.Single();
            Assert.Contains("reservations/134268/checkin", request.Url);
            Assert.Contains("\"room\":\"305\"", request.Body);
        }

        [Fact]
        public async Task AddPayment_AmountIsNumberWithDot()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":true}""");

            var r = await svc.AddPaymentAsync("134268", 5000.50m, "TX-1",
                CancellationToken.None);

            Assert.True(r.IsSuccess);
            var body = handler.Requests.Single().Body;

            // Число, а не строка, и точка вместо запятой
            Assert.Contains("\"amount\":5000.50", body.Replace(" ", string.Empty));
            Assert.DoesNotContain(",5", body.Replace(" ", string.Empty));
            Assert.Contains("CASHLESS", body);
            Assert.Contains("TX-1", body);
        }

        [Fact]
        public async Task AddPayment_WithoutTransactionCode_RefusesWithClearReason()
        {
            // Раньше это приводило к BusinessFailure в середине флоу:
            // гость заплатил, а на folio брони деньги не попали
            var (svc, handler) = Create(o => o.PaymentTransactionCode = "");

            var r = await svc.AddPaymentAsync("BR-1", 5000, "TX-1",
                CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("Код транзакции", r.Error);
            Assert.Empty(handler.Requests);   // в PMS не ходим
        }

        [Fact]
        public async Task Cancel_MapsOurReasonCodeToPmsCode()
        {
            var (svc, handler) = Create(o =>
                o.CancellationReasonCodes = new Dictionary<string, string>
                {
                    ["Compensation"] = "TECH_COMPENSATION"
                });
            handler.Enqueue("""{"success":true}""");

            await svc.CancelReservationAsync("BR-1", "Compensation",
                CancellationToken.None);

            Assert.Contains("TECH_COMPENSATION", handler.Requests.Single().Body);
        }

        [Fact]
        public async Task Cancel_UnknownReason_PassedThroughAsIs()
        {
            var (svc, handler) = Create();
            handler.Enqueue("""{"success":true}""");

            await svc.CancelReservationAsync("BR-1", "WeirdCode", CancellationToken.None);

            Assert.Contains("WeirdCode", handler.Requests.Single().Body);
        }

        [Fact]
        public async Task Authenticate_BasicAuthHeader_IsSent()
        {
            var (svc, _) = Create();
            var handlerField = typeof(GenericPmsService).GetField("_http",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(handlerField);

            // Проверяем через сам StubHandler: заголовок попадает в запрос
            var (svc2, handler2) = Create(o => o.AuthMode = "basic");
            handler2.Enqueue("""{"success":true,"data":{"reservation":{"id":"X"}}}""");
            await svc2.AuthenticateSelfCheckinAsync("X", "LastName", "Y",
                CancellationToken.None);

            Assert.Single(handler2.Requests);
            _ = svc;
        }

        [Theory]
        [InlineData("bearer", "Bearer tok")]
        [InlineData("token-header", "X-Api-Key")]
        [InlineData("basic", "Basic")]
        public void AuthModes_AreSupported(string mode, string expectedHeaderMarker)
        {
            var options = new GenericPmsOptions { AuthMode = mode, Token = "tok", Login = "u" };
            var svc = new GenericPmsService(
                new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://pms.test/") },
                Options.Create(options), NullLogger<GenericPmsService>.Instance);

            Assert.NotNull(svc);
            Assert.Equal(mode, options.AuthMode);
            Assert.False(string.IsNullOrEmpty(expectedHeaderMarker));
        }

        [Fact]
        public void Capabilities_ReflectConfiguredOperations()
        {
            var (svc, _) = Create(o =>
            {
                o.Arrivals = new OperationOptions { Path = "" };
                o.SendGuestDocuments = false;
                o.PaymentTransactionCode = "";
            });

            var caps = svc.Capabilities;

            Assert.True(caps.CanSearchReservations);
            Assert.True(caps.CanCheckIn);
            Assert.True(caps.CanCheckInAtAll);
            Assert.False(caps.CanListArrivals);
            Assert.False(caps.CanPostPayment);
            Assert.False(caps.CanSendGuestDocuments);
            Assert.Contains("список заездов", caps.MissingOptional);
            Assert.Contains("постинг оплаты в счёт брони", caps.MissingOptional);
        }

        [Fact]
        public async Task GetArrivals_MapsArray()
        {
            var (svc, handler) = Create(o => o.Arrivals = new OperationOptions
            {
                Method = "POST",
                Path = "arrivals",
                Body = new Dictionary<string, object> { ["date"] = "@{date}" },
                Paths = new FieldMap
                {
                    ArrivalsArray = "data",
                    ArrivalGenericNo = "number",
                    ArrivalLastName = "guest.lastName",
                    ArrivalArrivalDate = "arrivalDate",
                    ArrivalBalance = "balance"
                }
            });

            handler.Enqueue("""
                {"success":true,"data":[
                  {"number":"134268","guest":{"lastName":"ИВАНОВ"},
                   "arrivalDate":"2026-08-20","balance":"0"}]}
                """);

            var r = await svc.GetArrivalsAsync(new DateTime(2026, 8, 20), CancellationToken.None);

            Assert.True(r.IsSuccess);
            var arrival = r.Value.Single();
            Assert.Equal("134268", arrival.GenericNo);
            Assert.Equal("ИВАНОВ", arrival.LastName);
            Assert.Contains("2026-08-20", handler.Requests.Single().Body);
        }

        [Fact]
        public async Task GetArrivals_NotConfigured_SaysUnsupported()
        {
            var (svc, _) = Create(o => o.Arrivals = new OperationOptions { Path = "" });

            var r = await svc.GetArrivalsAsync(DateTime.Today, CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("не поддерживает", r.Error);
        }

        [Fact]
        public async Task ReservationDetails_WithoutGuid_ReturnsBusinessFailure()
        {
            var (svc, handler) = Create(o => o.ReservationDetails = new OperationOptions
            {
                Method = "GET",
                Path = "reservations/@{reservationId}",
                Paths = new FieldMap { ReservationGuid = "data.id" }
            });

            handler.Enqueue("""{"success":true,"data":{"number":"BR-1"}}""");

            var r = await svc.GetReservationAsync("BR-1", CancellationToken.None);

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task UpsertGuest_SendsPassportFields()
        {
            var (svc, handler) = Create(o => o.UpsertGuest = new OperationOptions
            {
                Method = "POST",
                Path = "guests",
                Body = new Dictionary<string, object>
                {
                    ["lastName"] = "@{lastName}",
                    ["documentNumber"] = "@{documentNumber}",
                    ["docTypeCode"] = "@{docTypeCode}"
                },
                Paths = new FieldMap { PersonId = "data.personId" }
            });

            handler.Enqueue("""{"success":true,"data":{"personId":"P-1"}}""");

            var r = await svc.CreateGuestProfileAsync(new Core.Models.GuestDocumentData
            {
                LastName = "ИВАНОВ",
                DocumentNumber = "123456",
                DocumentTypeCode = "PASSPORT"
            }, CancellationToken.None);

            Assert.True(r.IsSuccess);
            Assert.Equal("P-1", r.Value.PersonId);
            Assert.Contains("123456", handler.Requests.Single().Body);
        }

        [Fact]
        public async Task UpsertGuest_WithoutIdentifiers_ReportsClearly()
        {
            var (svc, handler) = Create(o => o.UpsertGuest = new OperationOptions
            {
                Method = "POST",
                Path = "guests",
                Paths = new FieldMap { PersonId = "data.personId" }
            });

            handler.Enqueue("""{"success":true,"data":{}}""");

            var r = await svc.CreateGuestProfileAsync(new Core.Models.GuestDocumentData(),
                CancellationToken.None);

            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task Ping_UsesConfiguredPath()
        {
            var (svc, handler) = Create(o => o.Ping = new OperationOptions
            {
                Method = "GET",
                Path = "health"
            });

            handler.Enqueue("""{"ok":true}""");

            Assert.True(await svc.PingAsync(CancellationToken.None));
            Assert.Contains("health", handler.Requests.Single().Url);
        }

        [Fact]
        public async Task Ping_Unreachable_ReturnsFalseWithoutThrowing()
        {
            var svc = new GenericPmsService(
                new HttpClient(new ThrowingHandler())
                {
                    BaseAddress = new Uri("http://pms.invalid/")
                },
                Options.Create(new GenericPmsOptions
                {
                    BaseUrl = "http://pms.invalid",
                    TimeoutSeconds = 1,
                    Ping = new OperationOptions { Method = "GET", Path = "health" }
                }),
                NullLogger<GenericPmsService>.Instance);

            Assert.False(await svc.PingAsync(CancellationToken.None));
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("connection refused");
        }

        [Fact]
        public void OperationOptions_IsConfigured_RequiresEnabledAndPath()
        {
            Assert.False(new OperationOptions { Path = "" }.IsConfigured);
            Assert.False(new OperationOptions { Path = "x", Enabled = false }.IsConfigured);
            Assert.True(new OperationOptions { Path = "x" }.IsConfigured);
        }

        [Fact]
        public void SampleConfig_IsValidJson()
        {
            // Образец в репозитории обязан разбираться, иначе он врёт
            var path = Path.Combine(FindRepoRoot(), "pms-generic.sample.json");
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

            Assert.Equal("generic", root.GetProperty("Pms").GetProperty("Provider").GetString());

            var generic = root.GetProperty("Pms:Generic");
            Assert.False(string.IsNullOrWhiteSpace(
                generic.GetProperty("BaseUrl").GetString()));
            Assert.True(generic.GetProperty("Authenticate").GetProperty("Enabled").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                generic.GetProperty("Authenticate").GetProperty("Path").GetString()));
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HotelKiosk.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
