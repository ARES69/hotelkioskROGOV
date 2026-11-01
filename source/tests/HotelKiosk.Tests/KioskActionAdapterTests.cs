using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Application.Services;
using HotelKiosk.Core.Models;
using HotelKiosk.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Тесты адаптера протокола MONE (KioskActionAdapter):
    /// маппинг действий на порты и коды ошибок каталога MONE.
    /// </summary>
    public class KioskActionAdapterTests
    {
        private readonly FakeRegulaService _regula = new();
        private readonly FakeLogusService _logus = new() { ReservationGuid = "guid-123" };
        private readonly FakeVisionLineService _visionLine = new();
        private readonly FakeAlfaBankService _alfaBank = new();
        private readonly FakeCardTerminalService _card = new();
        private readonly FakeNotificationService _notifications = new();
        private readonly FakeCheckInSessionStore _store = new();

        private KioskActionAdapter Create() => new(
            _regula, _logus, _visionLine, _alfaBank, _card,
            _notifications, _store, NullLogger<KioskActionAdapter>.Instance);

        private static JsonElement Payload(string json) =>
            JsonDocument.Parse(json).RootElement;

        private const string S1 = "session-A";

        /// <summary>
        /// Без UnsafeRelaxedJsonEscaping кириллица сериализуется как \uXXXX,
        /// и ассерты по русским подстрокам не находят текст.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ===== preflight =====

        [Fact]
        public async Task Preflight_ReturnsReadyAndFourDeviceBooleans()
        {
            var r = await Create().ExecuteAsync("system.preflight",
                Payload("{}"), S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"ready\":", json);
            foreach (var d in new[] { "logus", "vision", "crt", "regula" })
                Assert.Contains($"\"{d}\":", json);
        }

        // ===== booking.search =====

        [Fact]
        public async Task BookingSearch_Found_MapsRequiredBookingFields()
        {
            var r = await Create().ExecuteAsync("booking.search",
                Payload("{\"code\":\"134268\",\"surname\":\"Зотова\"}"),
                S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"found\":true", json);
            Assert.Contains("\"id\":\"134268\"", json);
            Assert.Contains("\"room\":\"101\"", json);
            Assert.Contains("\"dueAmount\":5000", json);
            Assert.Contains("\"maxGuests\":1", json);
        }

        [Fact]
        public async Task BookingSearch_NotFound_ThrowsBookingNotFound()
        {
            _logus.FailFind = true;
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("booking.search",
                    Payload("{\"code\":\"X\",\"surname\":\"Y\"}"), S1, 1, CancellationToken.None));
            Assert.Equal("BOOKING_NOT_FOUND", ex.Code);
        }

        // ===== scanner.scan =====

        [Fact]
        public async Task ScannerScan_EchoesSequenceAndPageType()
        {
            var a = Create();
            var r = await a.ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\"}"),
                S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"accepted\":true", json);
            Assert.Contains("\"sequence\":1", json);
            Assert.Contains("passport_page_2", json);
            Assert.Equal(JsonValueKind.String,
                JsonDocument.Parse(json).RootElement.GetProperty("documentKey").ValueKind);
        }

        // ===== scanner.scan с кадром камеры (payload.image) =====

        [Fact]
        public async Task Arrivals_MapsAndListsToday()
        {
            var a = Create();
            var r = await a.ExecuteAsync("booking.arrivals",
                Payload("{\"date\":\"2026-08-20\"}"), S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r, JsonOptions);
            Assert.Contains("20001", json);
            Assert.Contains("Иванов", json);
            Assert.Contains("\"date\":\"2026-08-20\"", json);
        }

        [Fact]
        public async Task Arrivals_DefaultsToToday_WhenDateOmitted()
        {
            var a = Create();
            var r = await a.ExecuteAsync("booking.arrivals",
                Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Contains(DateTime.Today.ToString("yyyy-MM-dd"),
                JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task Arrivals_BadDate_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("booking.arrivals",
                    Payload("{\"date\":\"не-дата\"}"), S1, 1, CancellationToken.None));

            Assert.Equal("BOOKING_DATES_INVALID", ex.Code);
        }

        [Fact]
        public async Task Arrivals_UnsupportedByPms_ThrowsPmsOffline()
        {
            // PMS без операции: UI прячет кнопку по pms.missing, но и прямой
            // вызов должен дать внятную ошибку, а не пустой список
            _logus.Capabilities = _logus.Capabilities with { CanListArrivals = false };

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("booking.arrivals",
                    Payload("{}"), S1, 1, CancellationToken.None));

            Assert.Equal("PMS_OFFLINE", ex.Code);
        }

        [Fact]
        public async Task ScannerScan_WithPhoto_UsesRegulaPhotoRecognition_NotReader()
        {
            // Схема «фронтальная камера»: кадр приходит в payload.image,
            // считыватель не дергаем
            var r = await Create().ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\",\"image\":\"QUJD\"}"),
                S1, 1, CancellationToken.None);

            Assert.Equal(1, _regula.PhotoCalls);
            Assert.Equal(0, _regula.ScanCalls);
            Assert.Equal("QUJD", _regula.LastPhoto);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"accepted\":true", json);
            Assert.Contains("4500123456", json);
        }

        [Fact]
        public async Task GuestUpsert_PmsWithoutProfileSupport_ContinuesWithoutError()
        {
            // У части PMS нет эндпоинтов профиля гостя. Заселение обязано
            // продолжиться, персонал внесёт паспорт вручную.
            _logus.Capabilities = _logus.Capabilities with
            {
                Vendor = "shelter",
                CanSendGuestDocuments = false
            };

            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("pms.guest.upsert",
                Payload("{\"index\":0,\"lastNameRu\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            Assert.Equal(0, _logus.GuestProfileCalls);   // в PMS не ходили
            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"ok\":true", json);
            Assert.Contains("guestDocsSent\":false", json);
            Assert.Single(_notifications.SentMessages);
        }

        [Fact]
        public async Task Preflight_ReportsPmsCapabilitiesAndMissingOperations()
        {
            _logus.Capabilities = _logus.Capabilities with
            {
                Vendor = "shelter",
                CanListArrivals = false
            };

            var r = await Create().ExecuteAsync("system.preflight",
                Payload("{}"), S1, 1, CancellationToken.None);

            // Сериализуем с UnsafeRelaxedJsonEscaping: иначе кириллица
            // уедет как \uXXXX и подстрока не найдётся
            var json = JsonSerializer.Serialize(r, JsonOptions);
            Assert.Contains("\"vendor\":\"shelter\"", json);
            Assert.Contains("список заездов", json);
            Assert.Contains("\"ready\":true", json);
        }

        [Fact]
        public async Task Preflight_NotReady_WhenPmsCannotCheckIn()
        {
            // PMS без заселения: показать гостю «киоск не готов» ДО того,
            // как он введёт номер брони
            _logus.Capabilities = _logus.Capabilities with { CanCheckIn = false };

            var r = await Create().ExecuteAsync("system.preflight",
                Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Contains("\"ready\":false", JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task ScannerScan_WithEmptyPhoto_FallsBackToReader()
        {
            // Пустой кадр — не повод падать: уходим на считыватель
            await Create().ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\",\"image\":\"\"}"),
                S1, 1, CancellationToken.None);

            Assert.Equal(0, _regula.PhotoCalls);
            Assert.Equal(1, _regula.ScanCalls);
        }

        [Fact]
        public async Task ScannerScan_WithoutPhoto_UsesReader()
        {
            await Create().ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\"}"),
                S1, 1, CancellationToken.None);

            Assert.Equal(0, _regula.PhotoCalls);
            Assert.Equal(1, _regula.ScanCalls);
        }

        [Fact]
        public async Task ScannerScan_PhotoRecognitionFails_ThrowsOcrFieldMissing()
        {
            _regula.PhotoResult = Result<PassportData>.BusinessFailure("Паспорт не распознан");

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("scanner.scan",
                    Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\",\"image\":\"QUJD\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("OCR_FIELD_MISSING", ex.Code);
        }

        [Fact]
        public async Task ScannerScan_SameDocumentSecondGuest_DuplicateDocument()
        {
            var a = Create();
            await a.ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\"}"),
                S1, 1, CancellationToken.None);

            // Фейк всегда возвращает один паспорт → второй гость с тем же документом
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("scanner.scan",
                    Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"second\"}"),
                    S1, 1, CancellationToken.None));
            Assert.Equal("OCR_DUPLICATE_DOCUMENT", ex.Code);
        }

        // ===== pms.checkin / finalize =====

        [Fact]
        public async Task CheckIn_WithoutBookingSearch_Throws()
        {
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("pms.checkin",
                    Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None));
            Assert.Equal("BOOKING_NOT_FOUND", ex.Code);
        }

        [Fact]
        public async Task FullRegistrationChain_CheckinOnce_VisionNoop_FinalizeNotifies()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("scanner.scan",
                Payload("{\"sequence\":1,\"pageType\":\"passport_page_2\",\"purpose\":\"primary\"}"),
                S1, 1, CancellationToken.None);
            await a.ExecuteAsync("pms.guest.upsert",
                Payload("{\"index\":0}"), S1, 1, CancellationToken.None);
            // До заселения — оплата (шлагбаум в pms.checkin)
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var ci = await a.ExecuteAsync("pms.checkin",
                Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None);
            Assert.Contains("\"checkinId\":", JsonSerializer.Serialize(ci));
            Assert.Equal(1, _logus.SelfCheckInCalls);

            var vp = await a.ExecuteAsync("vision.prepare",
                Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None);
            Assert.Contains("\"accessId\":", JsonSerializer.Serialize(vp));

            var cw = await a.ExecuteAsync("card.write",
                Payload("{\"uid\":\"U1\",\"index\":0,\"room\":\"101\"}"), S1, 1, CancellationToken.None);
            Assert.Contains("\"written\":true", JsonSerializer.Serialize(cw));
            Assert.True(_visionLine.EncodeCalls >= 1);

            var fin = await a.ExecuteAsync("pms.finalize",
                Payload("{\"cards\":1}"), S1, 1, CancellationToken.None);
            Assert.Contains("\"finalizeId\":", JsonSerializer.Serialize(fin));
            Assert.Single(_notifications.SentMessages);   // успех персоналу
        }

        [Fact]
        public async Task CardWrite_EncodeFails_CardWriteFailedCode()
        {
            _visionLine.FailEncode = true;
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                S1, 1, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("card.write",
                    Payload("{\"uid\":\"U1\",\"index\":0,\"room\":\"101\"}"), S1, 1, CancellationToken.None));
            Assert.Equal("CARD_WRITE_FAILED", ex.Code);
        }

        // ===== шлагбаум оплаты перед заселением =====

        [Fact]
        public async Task CheckIn_WithoutPayment_RefusesAndDoesNotTouchPms()
        {
            // Ключевая дыра: pms.checkin шёл в PMS без проверки оплаты.
            // Заезд без денег — прямой убыток отеля.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("PAYMENT_NOT_CONFIRMED", ex.Code);
            Assert.Equal(0, _logus.SelfCheckInCalls);
        }

        [Fact]
        public async Task CheckIn_UnconfirmedPayment_Refuses()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = false;   // гость ещё не оплатил
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("PAYMENT_NOT_CONFIRMED", ex.Code);
            Assert.Equal(0, _logus.SelfCheckInCalls);
        }

        [Fact]
        public async Task CheckIn_ConfirmedPayment_Passes()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                S1, 1, CancellationToken.None);

            Assert.Equal(1, _logus.SelfCheckInCalls);
        }

        [Fact]
        public async Task CheckIn_CardPayment_VerifiedAtTerminal()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);

            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                S1, 1, CancellationToken.None);

            // Статус спрошен у терминала, а не у банка
            Assert.Equal(1, _card.StatusCalls);
            Assert.Equal(1, _logus.SelfCheckInCalls);
        }

        [Fact]
        public async Task CheckIn_AfterRefund_Refuses()
        {
            // Деньги вернули — заселение в бронь идти не должно
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            // После возврата _payTxId пуст, шлагбаум это ловит
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("PAYMENT_NOT_CONFIRMED", ex.Code);
        }

        [Fact]
        public async Task CheckIn_ZeroPriceBooking_AllowedWithoutPayment()
        {
            // Бесплатное проживание (комплимент, трансфер) — оплаты нет и не нужно
            _logus.PriceOverride = 0m;
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                S1, 1, CancellationToken.None);

            Assert.Equal(1, _logus.SelfCheckInCalls);
        }

        [Fact]
        public async Task CardWrite_BeforeCheckIn_Refuses()
        {
            // Ключ в бронь без заселения — та же дыра, что и с оплатой
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("card.write",
                    Payload("{\"uid\":\"U1\",\"index\":0,\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("PMS_CHECKIN_UNKNOWN", ex.Code);
            Assert.Equal(0, _visionLine.EncodeCalls);
        }

        [Fact]
        public async Task CardWrite_WithoutBooking_Refuses()
        {
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                Create().ExecuteAsync("card.write",
                    Payload("{\"uid\":\"U1\",\"index\":0,\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            Assert.Equal("BOOKING_NOT_FOUND", ex.Code);
            Assert.Equal(0, _visionLine.EncodeCalls);
        }

        // ===== payment =====

        [Fact]
        public async Task PaymentCreate_Qr_WithoutPayment_DoesNotRequireRefund()
        {
            // Регрессия: QR-заказ создан, но гость ещё не заплатил. Чекпоинт
            // помечался «требуется возврат», и персонал искал бы деньги,
            // которых никто не платил.
            _alfaBank.NoRrn = true;   // нет RRN → подтверждения нет
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);

            Assert.NotNull(saved);
            Assert.Equal("TX-1", saved.PaymentTransactionId);   // заказ есть
            Assert.False(saved.PaymentConfirmed);                // но денег нет
            Assert.Equal(0m, saved.PaidAmount);
            Assert.False(saved.RequiresCompensation);            // возврат не нужен
            Assert.Equal(CompensationAction.None, saved.RequiredCompensation);
        }

        [Fact]
        public async Task PaymentConfirmed_ThenSessionSaved_RequiresRefund()
        {
            // Обратный случай: оплата подтверждена, заселение не завершено —
            // чекпоинт обязан требовать возврата
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.status",
                Payload("{\"orderId\":\"TX-1\"}"), S1, 1, CancellationToken.None);

            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                S1, 1, CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.True(saved.PaymentConfirmed);
            Assert.Equal(5000m, saved.PaidAmount);
            Assert.True(saved.RequiresCompensation);
        }

        [Fact]
        public async Task RecoveryInspect_AbandonedQrWithoutPayment_NotRefundRequired()
        {
            // Брошенный QR (гость ушёл, не заплатив) не должен выглядеть
            // как «гость заплатил, верните деньги»
            await _store.SaveAsync(new CheckInContext
            {
                ReservationId = "R-9",
                PaymentTransactionId = "TX-9",
                PaymentConfirmed = false,
                PaidAmount = 0m,
                RequiresCompensation = false
            }, CancellationToken.None);

            var r = await Create().ExecuteAsync("recovery.inspect",
                Payload("{}"), S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("conflict", json);
            Assert.DoesNotContain("refund_required", json);
        }

        [Fact]
        public async Task PaymentCreate_Qr_RoutesToBank_StatusRoutesToBank()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            var created = await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            var json = JsonSerializer.Serialize(created);
            Assert.Contains("\"orderId\":\"TX-1\"", json);
            Assert.Contains("\"confirmed\":true", json);   // RRN пришёл сразу

            await a.ExecuteAsync("payment.status",
                Payload("{\"orderId\":\"TX-1\"}"), S1, 1, CancellationToken.None);
            Assert.True(_alfaBank.StatusCheckCalls >= 1);
            Assert.Equal(0, _card.StatusCalls);
        }

        [Fact]
        public async Task PaymentCreate_Card_RoutesToTerminal()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            var created = await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);
            Assert.Contains("CARD-TX-1", JsonSerializer.Serialize(created));
            Assert.Equal(1, _card.ChargeCalls);
            Assert.Equal(0, _alfaBank.StatusCheckCalls);
        }

        [Fact]
        public async Task PaymentStatus_AmountMismatch_StopsCheckInAndAlertsStaff()
        {
            // Терминал/банк мог списать не ту сумму. Заселять по цене брони
            // в этом случае нельзя: отель либо теряет деньги, либо берёт лишнее.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            _alfaBank.NoRrn = true;
            _alfaBank.StatusConfirmed = true;
            _alfaBank.Amount = 100m;          // списано 100 вместо 5000

            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("payment.status",
                    Payload("{\"orderId\":\"TX-1\"}"), S1, 1, CancellationToken.None));
            Assert.Equal("PAYMENT_AMOUNT_MISMATCH", ex.Code);

            // Деньги не признаны полученными — шлагбаум заселения закрыт.
            var gate = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin",
                    Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None));
            Assert.Equal("PAYMENT_NOT_CONFIRMED", gate.Code);

            Assert.Contains(_notifications.SentMessages,
                m => m.Contains("не совпала"));
        }

        [Fact]
        public async Task PaymentStatus_AmountInKopecks_IsAccepted()
        {
            // Шлюзы отдают сумму то в рублях, то в копейках. Копейки не должны
            // выглядеть как расхождение и блокировать заселение.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            _alfaBank.NoRrn = true;
            _alfaBank.StatusConfirmed = true;
            _alfaBank.Amount = 500000m;       // 5000 ₽ в копейках

            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.status",
                Payload("{\"orderId\":\"TX-1\"}"), S1, 1, CancellationToken.None);

            Assert.Contains("\"confirmed\":true", JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task PaymentCreate_Repeated_ReturnsSameOrderWithoutSecondCharge()
        {
            // Повторный payment.create (двойной клик, ретрай UI) не должен
            // приводить ко второму заказу или второму списанию.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            _card.StatusConfirmed = false;

            var first = await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);
            var second = await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);

            Assert.Equal(1, _card.ChargeCalls);
            Assert.Contains("CARD-TX-1", JsonSerializer.Serialize(first));
            Assert.Contains("CARD-TX-1", JsonSerializer.Serialize(second));
        }

        [Fact]
        public async Task PaymentCreate_AfterPaid_DoesNotChargeTwice()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            _card.ConfirmImmediately = true;

            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);
            var second = await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);

            Assert.Equal(1, _card.ChargeCalls);
            Assert.Contains("\"alreadyPaid\":true", JsonSerializer.Serialize(second));
        }

        [Fact]
        public async Task PaymentCancel_Qr_Unpaid_CancelsOrderAtBank()
        {
            // Гость не оплатил: денег нет — это отмена заказа, а не возврат
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = false;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Equal(1, _alfaBank.CancelUnpaidCalls);
            Assert.Equal(0, _alfaBank.RefundCalls);
            Assert.Contains("\"refunded\":false", JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task PaymentCancel_Qr_Paid_RefundsAtBank()
        {
            // Ключевой случай: деньги списаны, гость отменяет. Раньше здесь
            // возврат не происходил вообще — деньги оставались у отеля.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Equal(1, _alfaBank.RefundCalls);
            Assert.Equal(0, _alfaBank.CancelUnpaidCalls);
            Assert.Contains("\"refunded\":true", JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task PaymentCancel_Card_Paid_RefundsAtTerminal()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"card\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Equal(1, _card.RefundCalls);
            Assert.Contains("\"refunded\":true", JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task PaymentCancel_RefundFails_ReportsErrorAndNotifiesStaff()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            _alfaBank.FailRefund = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("\"ok\":false", json);
            Assert.Contains("PAYMENT_REFUND_FAILED", json);
            Assert.Single(_notifications.SentMessages);
        }

        [Fact]
        public async Task PaymentCancel_Repeated_DoesNotRefundTwice()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.StatusConfirmed = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            // Второй вызов не должен снова списать деньги
            Assert.Equal(1, _alfaBank.RefundCalls);
        }

        [Fact]
        public async Task PaymentCancel_StatusUnknown_DoesNotRefundOrCancel()
        {
            // Статус не удалось получить: ни возврата, ни отмены — и то и другое
            // может навредить гостю. Разбирается персонал.
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            _alfaBank.ThrowOnStatusCheck = true;
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var r = await a.ExecuteAsync("payment.cancel", Payload("{}"), S1, 1, CancellationToken.None);

            Assert.Equal(0, _alfaBank.RefundCalls);
            Assert.Equal(0, _alfaBank.CancelUnpaidCalls);
            var json = JsonSerializer.Serialize(r);
            Assert.Contains("PAYMENT_STATUS_UNKNOWN", json);
            Assert.Contains("\"ok\":false", json);
            Assert.Single(_notifications.SentMessages);
        }

        [Fact]
        public async Task PaymentCreate_SavesCheckpointRequiringCompensation()
        {
            // После оплаты чекпоинт должен требовать компенсации: если процесс
            // упадёт, персонал увидит, что деньги списаны
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.True(saved.RequiresCompensation);
            Assert.Equal(CompensationAction.RefundPayment, saved.RequiredCompensation);
            Assert.Equal("TX-1", saved.PaymentTransactionId);
        }

        [Fact]
        public async Task CheckIn_SavesCheckpoint_BeforeMutatingLogus()
        {
            // Чекпоинт пишется ДО заселения в PMS: к моменту сбоя мы ещё
            // не знаем, чем кончилось заселение
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None);

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.True(saved.RequiresCompensation);
            Assert.Equal("101", saved.RoomNumber);
        }

        [Fact]
        public async Task CheckIn_Fails_KeepsCheckpointForManualReview()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            _logus.FailUpdate = true;   // SelfCheckInAsync вернёт SystemFailure

            await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"),
                    S1, 1, CancellationToken.None));

            var saved = await _store.LoadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.True(saved.RequiresCompensation);
            Assert.Equal(CheckInState.Failed, saved.CurrentState);
            Assert.False(string.IsNullOrEmpty(saved.LastError));
        }

        [Fact]
        public async Task Finalize_ClearsCheckpointAndResetsPaymentState()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("payment.create",
                Payload("{\"method\":\"qr\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("pms.checkin", Payload("{\"room\":\"101\"}"), S1, 1, CancellationToken.None);
            await a.ExecuteAsync("pms.finalize", Payload("{\"cards\":1}"), S1, 1, CancellationToken.None);

            Assert.Null(await _store.LoadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task RecoveryInspect_PaidButNotCheckedIn_AsksForRefund()
        {
            // Единственное состояние, где гость может не получить деньги:
            // оплата подтверждена, заселение не завершено
            await _store.SaveAsync(new CheckInContext
            {
                ReservationId = "R-9",
                PaymentTransactionId = "TX-9",
                PaidAmount = 5000,
                RequiresCompensation = true,
                RequiredCompensation = CompensationAction.RefundPayment,
                CurrentState = CheckInState.EncodingKeyCard
            }, CancellationToken.None);

            var r = await Create().ExecuteAsync("recovery.inspect",
                Payload("{}"), S1, 1, CancellationToken.None);

            var json = JsonSerializer.Serialize(r);
            Assert.Contains("refund_required", json);
            Assert.Contains("TX-9", json);
            Assert.Single(_notifications.SentMessages);
            Assert.Contains("возврат", _notifications.SentMessages[0].ToLowerInvariant());
        }

        [Fact]
        public async Task RecoveryInspect_NotPaid_ReportsConflict()
        {
            await _store.SaveAsync(new CheckInContext { ReservationId = "R-9" }, CancellationToken.None);
            var r = await Create().ExecuteAsync("recovery.inspect",
                Payload("{}"), S1, 1, CancellationToken.None);
            Assert.Contains("\"status\":\"conflict\"", JsonSerializer.Serialize(r));
            Assert.Single(_notifications.SentMessages);
        }

        [Fact]
        public async Task NewSessionId_ResetsState_BookingRequiredAgain()
        {
            var a = Create();
            await a.ExecuteAsync("booking.search",
                Payload("{\"code\":\"BR-001\",\"surname\":\"Иванов\"}"), S1, 1, CancellationToken.None);

            // Новый гость: другой sessionId → состояние сброшено
            var ex = await Assert.ThrowsAsync<KioskActionException>(() =>
                a.ExecuteAsync("pms.checkin",
                    Payload("{\"room\":\"101\"}"), "session-B", 1, CancellationToken.None));
            Assert.Equal("BOOKING_NOT_FOUND", ex.Code);
        }
    }
}
