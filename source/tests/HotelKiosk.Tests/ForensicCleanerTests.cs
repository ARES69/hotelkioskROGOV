using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Infrastructure.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// ForensicCleaner обезличивает устаревший чекпоинт, а не удаляет его.
    ///
    /// Регрессия: чекпоинт незавершённой оплаты стирался целиком через час и
    /// молча. Персонал не узнавал, что гостю нужно вернуть деньги, а след для
    /// ручного возврата исчезал вместе с паспортом гостя.
    /// </summary>
    public class ForensicCleanerTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _session;

        public ForensicCleanerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hk-forensic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _session = Path.Combine(_dir, "session.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* не критично */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>Обслуживает чекпоинт обычным вызовом, без рефлексии.</summary>
        private void RunCleaner() =>
            CheckpointRetention.Process(_session, NullLogger.Instance);

        private void Write(CheckInContext ctx)
        {
            File.WriteAllText(_session,
                JsonSerializer.Serialize(ctx, new JsonSerializerOptions { WriteIndented = true }));

            // Ставим время в прошлом, чтобы файл считался устаревшим
            var age = DateTime.UtcNow.AddHours(-2);
            File.SetLastWriteTimeUtc(_session, age);
        }

        private CheckInContext Read() =>
            JsonSerializer.Deserialize<CheckInContext>(File.ReadAllText(_session));

        [Fact]
        public void FreshCheckpoint_IsNotTouched()
        {
            // Активная сессия: файл обновляется на каждом шаге
            var ctx = new CheckInContext
            {
                ReservationId = "BR-1",
                GuestName = "ИВАНОВ ИВАН",
                PassportNumber = "45 001234"
            };
            File.WriteAllText(_session,
                JsonSerializer.Serialize(ctx, new JsonSerializerOptions { WriteIndented = true }));

            RunCleaner();

            var after = Read();
            Assert.Equal("ИВАНОВ ИВАН", after.GuestName);
            Assert.False(after.Anonymized);
        }

        [Fact]
        public void StaleCheckpoint_LosesPersonalData_KeepsRefundTrace()
        {
            // Главный случай: гость оплатил, заселение не завершилось.
            // ПДн должны исчезнуть, а след для возврата — остаться.
            Write(new CheckInContext
            {
                ReservationId = "BR-1",
                GuestName = "ИВАНОВ ИВАН ИВАНОВИЧ",
                PassportNumber = "45 001234",
                ScannedPassport = new PassportData("45 001234", "ИВАНОВ ИВАН ИВАНОВИЧ", null),
                GuestId = "G-001",
                PaymentTransactionId = "TX-ABC",
                PaidAmount = 54000m,
                RequiresCompensation = true,
                RequiredCompensation = CompensationAction.RefundPayment
            });

            RunCleaner();

            Assert.True(File.Exists(_session), "след для возврата удалён вместе с ПДн");

            var after = Read();

            // ПДн стёрты
            Assert.Null(after.GuestName);
            Assert.Null(after.PassportNumber);
            Assert.Null(after.ScannedPassport);
            Assert.Null(after.GuestId);
            Assert.True(after.Anonymized);

            // След для ручного возврата сохранён
            Assert.Equal("BR-1", after.ReservationId);
            Assert.Equal("TX-ABC", after.PaymentTransactionId);
            Assert.Equal(54000m, after.PaidAmount);
            Assert.True(after.RequiresCompensation);
            Assert.Equal(CompensationAction.RefundPayment, after.RequiredCompensation);
        }

        [Fact]
        public void PersonalData_IsAbsentFromRawFile()
        {
            // Проверяем файл, а не объект: важно, что данные физически
            // не остались на диске даже в сериализованном виде
            Write(new CheckInContext
            {
                ReservationId = "BR-1",
                GuestName = "СЕКРЕТНОЕ_ИМЯ",
                PassportNumber = "99 999999",
                ScannedPassport = new PassportData("99 999999", "СЕКРЕТНОЕ_ИМЯ", null),
                PaymentTransactionId = "TX-1"
            });

            RunCleaner();

            var raw = File.ReadAllText(_session, Encoding.UTF8);

            Assert.DoesNotContain("СЕКРЕТНОЕ_ИМЯ", raw);
            Assert.DoesNotContain("99 999999", raw);
            Assert.Contains("TX-1", raw);
        }

        [Fact]
        public void AlreadyAnonymized_IsNotReprocessed()
        {
            var ctx = new CheckInContext
            {
                ReservationId = "BR-1",
                PaymentTransactionId = "TX-1",
                Anonymized = true,
                AnonymizedAtUtc = DateTime.UtcNow.AddMinutes(-30)
            };
            Write(ctx);

            RunCleaner();

            var after = Read();
            Assert.True(after.Anonymized);
            Assert.Equal("TX-1", after.PaymentTransactionId);
        }

        [Fact]
        public void ExpiredAnonymizedTrace_IsDeleted()
        {
            // След обезличен сутки назад и сутки пролежал — персонал
            // уже разобрался, дальше хранить нечего
            var ctx = new CheckInContext
            {
                ReservationId = "BR-1",
                PaymentTransactionId = "TX-1",
                Anonymized = true,
                AnonymizedAtUtc = DateTime.UtcNow.AddDays(-2)
            };
            Write(ctx);

            RunCleaner();

            Assert.False(File.Exists(_session), "просроченный след не удалён");
        }

        [Fact]
        public void CorruptedCheckpoint_IsRemoved()
        {
            // Разбирать нечего, ПДн не читаются — мусор
            File.WriteAllText(_session, "{ это не json");
            File.SetLastWriteTimeUtc(_session, DateTime.UtcNow.AddHours(-2));

            RunCleaner();

            Assert.False(File.Exists(_session));
        }

        [Fact]
        public void CompensatedCheckpoint_KeepsRefundTrace_Longer()
        {
            // Бронь с компенсацией: персонал обязан вернуть деньги, поэтому
            // след живёт весь срок, а не удаляется сразу после обезличивания
            Write(new CheckInContext
            {
                ReservationId = "BR-1",
                PaymentTransactionId = "TX-1",
                PaidAmount = 54000m,
                RequiresCompensation = true,
                RequiredCompensation = CompensationAction.Both
            });

            RunCleaner();

            var after = Read();
            Assert.True(File.Exists(_session));
            Assert.True(after.RequiresCompensation);
            Assert.Equal("TX-1", after.PaymentTransactionId);
        }
    }
}
