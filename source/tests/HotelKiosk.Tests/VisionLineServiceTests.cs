using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Infrastructure.Adapters.VisionLine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Тесты REST-адаптера VisionLine (http://192.168.4.55:27015):
    /// проверяют формат запросов и толерантный разбор ответов
    /// через подставной HttpMessageHandler.
    /// </summary>
    public class VisionLineServiceTests
    {
        private const string BaseUrl = "http://192.168.4.55:27015";

        private static VisionLineService CreateService(
            MockHttpHandler handler, VisionLineOptions opts = null)
        {
            opts ??= new VisionLineOptions
            {
                BaseUrl = BaseUrl,
                IssueKeyCardPath = "/IssueKeyCard",
                HealthPath = "/health",
                TimeoutSeconds = 5,
            };

            var http = new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) };
            return new VisionLineService(
                http, Options.Create(opts), NullLogger<VisionLineService>.Instance);
        }

        // ===== EncodeKeyCardAsync =====

        [Fact]
        public async Task EncodeKeyCard_SendsJsonWithRoomAndDates()
        {
            var handler = new MockHttpHandler();
            handler.SetupJson("/IssueKeyCard", new { cardUid = "VL-777" });

            var svc = CreateService(handler);
            var result = await svc.EncodeKeyCardAsync(
                "205", new DateTime(2026, 8, 20, 14, 0, 0),
                new DateTime(2026, 8, 23, 12, 0, 0), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal("VL-777", result.Value.CardUid);

            // Проверяем запрос: POST JSON на /IssueKeyCard с room и датами
            var req = handler.Requests[0];
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("application/json", req.ContentHeaders.ContentType.MediaType);
            var body = JsonDocument.Parse(req.Body).RootElement;
            Assert.Equal("205", body.GetProperty("room").GetString());
            Assert.Equal("2026-08-20T14:00:00", body.GetProperty("checkIn").GetString());
            Assert.Equal("2026-08-23T12:00:00", body.GetProperty("checkOut").GetString());
        }

        [Theory]
        [InlineData("uid", "VL-U1")]
        [InlineData("id", "VL-I1")]
        public async Task EncodeKeyCard_ParsesAlternativeUidFields(string field, string value)
        {
            // Ответ сервера может называть идентификатор по-разному —
            // разбор должен быть толерантным
            var handler = new MockHttpHandler();
            var json = $"{{\"{field}\":\"{value}\"}}";
            handler.SetupRaw("/IssueKeyCard", json);

            var svc = CreateService(handler);
            var result = await svc.EncodeKeyCardAsync(
                "101", DateTime.Today, DateTime.Today.AddDays(2), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(value, result.Value.CardUid);
        }

        [Fact]
        public async Task EncodeKeyCard_HttpError_ReturnsSystemFailure()
        {
            var handler = new MockHttpHandler();
            handler.SetupRaw("/IssueKeyCard", "boom", HttpStatusCode.InternalServerError);

            var svc = CreateService(handler);
            var result = await svc.EncodeKeyCardAsync(
                "101", DateTime.Today, DateTime.Today, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("500", result.Error);
        }

        [Fact]
        public async Task EncodeKeyCard_NoUidInResponse_ReturnsSystemFailure()
        {
            var handler = new MockHttpHandler();
            handler.SetupRaw("/IssueKeyCard", "{\"ok\":true}");

            var svc = CreateService(handler);
            var result = await svc.EncodeKeyCardAsync(
                "101", DateTime.Today, DateTime.Today, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("идентификатор карты", result.Error);
        }

        // ===== PingAsync =====

        [Fact]
        public async Task Ping_HealthOk_ReturnsTrue()
        {
            var handler = new MockHttpHandler();
            handler.SetupRaw("/health", "{\"status\":\"ok\"}");

            var svc = CreateService(handler);
            Assert.True(await svc.PingAsync(CancellationToken.None));

            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.EndsWith("/health", handler.Requests[0].Url.ToString());
        }

        [Fact]
        public async Task Ping_ServerDown_ReturnsFalse()
        {
            var handler = new MockHttpHandler();
            handler.SetupRaw("/health", "err", HttpStatusCode.ServiceUnavailable);

            var svc = CreateService(handler);
            Assert.False(await svc.PingAsync(CancellationToken.None));
        }

        // ===== Авторизация =====

        [Fact]
        public async Task Constructor_WithToken_SetsBearerHeader()
        {
            var handler = new MockHttpHandler();
            handler.SetupJson("/IssueKeyCard", new { cardUid = "VL-1" });

            var svc = CreateService(handler, new VisionLineOptions
            {
                BaseUrl = BaseUrl,
                Token = "secret-token",
                TimeoutSeconds = 5,
            });

            await svc.EncodeKeyCardAsync(
                "101", DateTime.Today, DateTime.Today, CancellationToken.None);

            Assert.Equal("Bearer",
                handler.Requests[0].Headers.Authorization.Scheme);
            Assert.Equal("secret-token",
                handler.Requests[0].Headers.Authorization.Parameter);
        }

        // ===== MockHttpHandler =====

        private class MockHttpHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, (string Body, HttpStatusCode Code,
                string ContentType)> _responses = new();

            public List<MockRequest> Requests { get; } = new();

            public void SetupJson(string path, object payload) =>
                SetupRaw(path, JsonSerializer.Serialize(payload));

            public void SetupRaw(string path, string body,
                HttpStatusCode code = HttpStatusCode.OK,
                string contentType = "application/json")
                => _responses[path] = (body, code, contentType);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(ct);
                Requests.Add(new MockRequest(
                    request.RequestUri ?? new Uri("http://x"), request.Method,
                    body, request.Headers,
                    request.Content?.Headers));

                foreach (var kv in _responses)
                {
                    if (request.RequestUri?.AbsoluteUri.EndsWith(kv.Key) == true)
                        return new HttpResponseMessage(kv.Value.Code)
                        {
                            Content = new StringContent(
                                kv.Value.Body, null, kv.Value.ContentType)
                        };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                    { Content = new StringContent("{\"error\":\"not mocked\"}") };
            }
        }

        private record MockRequest(
            Uri Url, HttpMethod Method, string Body,
            System.Net.Http.Headers.HttpRequestHeaders Headers,
            System.Net.Http.Headers.HttpContentHeaders ContentHeaders);
    }
}
