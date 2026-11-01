using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Infrastructure.Adapters.Regula;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Тесты адаптера Regula Document Reader Web API (контракт v8):
    /// формат запроса /api/process и толерантный разбор TEXT-контейнера
    /// (result_type=36) через подставной HttpMessageHandler.
    /// </summary>
    public class RegulaDocumentReaderServiceTests
    {
        private const string BaseUrl = "http://localhost:8080/api";

        private static (RegulaDocumentReaderService Svc, MockHttpHandler H)
            Create(string agentScanResponse = null, string processResponse = null,
                   HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var handler = new MockHttpHandler(agentScanResponse, processResponse, statusCode);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions
                {
                    DocumentReaderUrl = BaseUrl,
                    AgentScanPath = "device-scan",
                    ProcessPath = "process",
                    HealthPath = "ping",
                    TimeoutSeconds = 5
                }),
                NullLogger<RegulaDocumentReaderService>.Instance);
            return (svc, handler);
        }

        private const string FullTextContainer = """
        {
          "ProcessingFinished": 1,
          "ContainerList": {
            "containers": [
              { "result_type": 33 },
              {
                "result_type": 36,
                "Text": {
                  "fieldList": [
                    { "fieldType": 56, "fieldName": "Document_Series", "value": "45 17" },
                    { "fieldType": 2,  "fieldName": "Document_Number", "value": "123456" },
                    { "fieldType": 8,  "fieldName": "Surname", "value": "ИВАНОВ" },
                    { "fieldType": 9,  "fieldName": "Given_Names", "value": "ИВАН ИВАНОВИЧ" },
                    { "fieldType": 12, "fieldName": "Sex", "value": "M" },
                    { "fieldType": 5,  "fieldName": "Date_Of_Birth", "value": "1990-01-01" },
                    { "fieldType": 4,  "fieldName": "Date_Of_Issue", "value": "2015-05-20" },
                    { "fieldType": 73, "fieldName": "Authority_Code", "value": "770-001" },
                    { "fieldType": 24, "fieldName": "Authority", "value": "ОУФМС ПО МОСКВЕ" },
                    { "fieldType": 6,  "fieldName": "Place_Of_Birth", "value": "г. Москва" },
                    { "fieldType": 1,  "fieldName": "Issuing_State_Code", "value": "RUS" }
                  ]
                }
              }
            ]
          }
        }
        """;

        [Fact]
        public async Task ScanPassport_ParsesAllFieldsFromTextContainer()
        {
            var (svc, h) = Create(
                agentScanResponse: "{\"image\":\"QUJD\"}",
                processResponse: FullTextContainer);

            var r = await svc.ScanPassportAsync(CancellationToken.None);

            Assert.True(r.IsSuccess);
            Assert.Equal("45 17 123456", r.Value.PassportNumber);
            Assert.Equal("45 17", r.Value.Series);
            Assert.Equal("ИВАНОВ", r.Value.LastName);
            // Имя — первый токен GIVEN_NAMES, отчество — остаток
            Assert.Equal("ИВАН", r.Value.FirstName);
            Assert.Equal("ИВАНОВИЧ", r.Value.MiddleName);
            Assert.Equal("M", r.Value.Sex);
            Assert.NotNull(r.Value.DateOfBirth);
            Assert.Equal(1990, r.Value.DateOfBirth!.Value.Year);
            Assert.Equal(new DateTime(2015, 5, 20), r.Value.IssueDate);
            Assert.Equal("770-001", r.Value.DepartmentCode);
            Assert.Equal("ОУФМС ПО МОСКВЕ", r.Value.IssuedBy);
            Assert.Equal("г. Москва", r.Value.BirthPlace);
            Assert.Equal("RUS", r.Value.CountryCode);
        }

        [Fact]
        public async Task ScanPassport_SendsProcessRequestWithScenarioAndImage()
        {
            var (svc, h) = Create(
                agentScanResponse: "{\"image\":\"QUJD\"}",
                processResponse: FullTextContainer);

            await svc.ScanPassportAsync(CancellationToken.None);

            // Захват → GET device-scan, обработка → POST process
            Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
            Assert.EndsWith("device-scan", h.Requests[0].Url.ToString());
            Assert.Equal(HttpMethod.Post, h.Requests[1].Method);

            var body = JsonDocument.Parse(h.Requests[1].Body).RootElement;
            Assert.Equal("FullProcess", body.GetProperty("processParam").GetProperty("scenario").GetString());
            var image = body.GetProperty("List")[0]
                .GetProperty("ImageData").GetProperty("image").GetString();
            Assert.Equal("QUJD", image);
        }

        [Fact]
        public async Task ScanPassport_AgentNotConfigured_ReturnsBusinessFailure()
        {
            var handler = new MockHttpHandler(null, FullTextContainer);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions { DocumentReaderUrl = BaseUrl }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            var r = await svc.ScanPassportAsync(CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("AgentScanPath", r.Error);
        }

        [Fact]
        public async Task ScanPassport_NoDocumentNumber_ReturnsBusinessFailure()
        {
            var empty = """{"ContainerList":{"containers":[{"result_type":36,"Text":{"fieldList":[]}}]}}""";
            var (svc, _) = Create("{\"image\":\"QUJD\"}", empty);

            var r = await svc.ScanPassportAsync(CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("не распознан", r.Error);
        }

        [Fact]
        public async Task ScanPassport_LicenseProblem_ReturnsSystemFailureWithHint()
        {
            var (svc, _) = Create(
                "{\"image\":\"QUJD\"}", "{}", HttpStatusCode.Forbidden);

            var r = await svc.ScanPassportAsync(CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("лицензи", r.Error);
        }

        // ===== камера: распознавание по кадру с фронтальной камеры =====

        [Fact]
        public async Task RecognizeFromPhoto_PostsToProcessWithoutAgentCapture()
        {
            // Ключевое отличие схемы «камера»: агента-считывателя нет вообще,
            // поэтому единственный запрос — POST /process с кадром из payload.image
            var handler = new MockHttpHandler(null, FullTextContainer);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions
                {
                    DocumentReaderUrl = BaseUrl,
                    AgentScanPath = "",            // считывателя нет
                    ProcessPath = "process",
                    TimeoutSeconds = 5
                }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            var r = await svc.RecognizeFromPhotoAsync("QUJD", CancellationToken.None);

            Assert.True(r.IsSuccess);
            Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);

            var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
            var image = body.GetProperty("List")[0]
                .GetProperty("ImageData").GetProperty("image").GetString();
            Assert.Equal("QUJD", image);
        }

        [Theory]
        [InlineData("data:image/jpeg;base64,QUJD", "QUJD")]
        [InlineData("data:image/png;base64,QUJD", "QUJD")]
        [InlineData("QUJD", "QUJD")]
        public async Task RecognizeFromPhoto_StripsDataUriPrefix(string input, string expected)
        {
            // canvas.toDataURL отдаёт data:-префикс, Regula ждёт голый base64
            var handler = new MockHttpHandler(null, FullTextContainer);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions { DocumentReaderUrl = BaseUrl }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            await svc.RecognizeFromPhotoAsync(input, CancellationToken.None);

            var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
            var image = body.GetProperty("List")[0]
                .GetProperty("ImageData").GetProperty("image").GetString();
            Assert.Equal(expected, image);
        }

        [Fact]
        public async Task RecognizeFromPhoto_EmptyImage_ReturnsBusinessFailureWithoutRequest()
        {
            var handler = new MockHttpHandler(null, FullTextContainer);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions { DocumentReaderUrl = BaseUrl }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            var r = await svc.RecognizeFromPhotoAsync("   ", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Empty(handler.Requests);   // не тратим вызов на пустой кадр
        }

        [Fact]
        public async Task RecognizeFromPhoto_NoDocumentNumber_ReturnsBusinessFailure()
        {
            var empty = """{"ContainerList":{"containers":[{"result_type":36,"Text":{"fieldList":[]}}]}}""";
            var handler = new MockHttpHandler(null, empty);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions { DocumentReaderUrl = BaseUrl }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            var r = await svc.RecognizeFromPhotoAsync("QUJD", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("не распознан", r.Error);
        }

        [Fact]
        public async Task RecognizeFromPhoto_LicenseProblem_ReturnsSystemFailureWithHint()
        {
            var handler = new MockHttpHandler(null, "{}", HttpStatusCode.Forbidden);
            var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            var svc = new RegulaDocumentReaderService(
                http, Options.Create(new RegulaOptions { DocumentReaderUrl = BaseUrl }),
                NullLogger<RegulaDocumentReaderService>.Instance);

            var r = await svc.RecognizeFromPhotoAsync("QUJD", CancellationToken.None);

            Assert.False(r.IsSuccess);
            Assert.Contains("лицензи", r.Error);
        }

        [Fact]
        public async Task Ping_HealthOk_ReturnsTrue()
        {
            var (svc, h) = Create();
            Assert.True(await svc.PingAsync(CancellationToken.None));
            Assert.EndsWith("ping", h.Requests[0].Url.ToString());
        }

        private class MockHttpHandler : HttpMessageHandler
        {
            private readonly string _agentScanResponse;
            private readonly string _processResponse;
            private readonly HttpStatusCode _statusCode;

            public MockHttpHandler(string agentScanResponse, string processResponse,
                HttpStatusCode statusCode = HttpStatusCode.OK)
            {
                _agentScanResponse = agentScanResponse;
                _processResponse = processResponse;
                _statusCode = statusCode;
            }

            public List<(Uri Url, HttpMethod Method, string Body)> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content == null
                    ? "" : await request.Content.ReadAsStringAsync(ct);
                Requests.Add((request.RequestUri ?? new Uri("http://x"), request.Method, body));

                if (request.RequestUri?.AbsolutePath.EndsWith("device-scan") == true)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_agentScanResponse ?? "{}")
                    };

                if (request.RequestUri?.AbsolutePath.EndsWith("ping") == true)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("\"OK\"") };

                return new HttpResponseMessage(_statusCode)
                    { Content = new StringContent(_processResponse ?? "{}") };
            }
        }
    }
}
