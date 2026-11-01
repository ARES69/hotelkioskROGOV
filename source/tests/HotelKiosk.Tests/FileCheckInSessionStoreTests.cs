using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Чекпоинт сессии содержит персональные данные гостя (номер паспорта, ФИО,
    /// дату рождения), поэтому его расположение и удаление — не вопрос вкуса.
    ///
    /// Раньше файл лежал в ProgramData: папка общая, на чтение доступна любому
    /// пользователю Windows, то есть паспорт гостя читался любым процессом на
    /// киоск-ПК.
    /// </summary>
    public class FileCheckInSessionStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public FileCheckInSessionStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hk-store-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "session.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* не критично */ }
            GC.SuppressFinalize(this);
        }

        private FileCheckInSessionStore Create() =>
            new(NullLogger<FileCheckInSessionStore>.Instance, _path);

        [Fact]
        public void DefaultPath_IsNotInProgramData()
        {
            var path = FileCheckInSessionStore.DefaultPath();

            Assert.DoesNotContain("ProgramData", path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HotelKiosk", path);
        }

        [Fact]
        public async Task SaveThenLoad_RoundTrips()
        {
            var store = Create();
            await store.SaveAsync(new CheckInContext
            {
                ReservationId = "BR-1",
                GuestName = "ИВАНОВ ИВАН",
                PassportNumber = "45 00123456",
                PaidAmount = 5000
            }, CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("BR-1", loaded.ReservationId);
            Assert.Equal("ИВАНОВ ИВАН", loaded.GuestName);
            Assert.Equal("45 00123456", loaded.PassportNumber);
        }

        [Fact]
        public async Task Load_NoFile_ReturnsNull()
        {
            Assert.Null(await Create().LoadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Clear_RemovesFile()
        {
            var store = Create();
            await store.SaveAsync(new CheckInContext { ReservationId = "BR-1" },
                CancellationToken.None);
            Assert.True(File.Exists(_path));

            await store.ClearAsync(CancellationToken.None);

            Assert.False(File.Exists(_path));
        }

        [Fact]
        public async Task Clear_AlsoRemovesLeftoverTempFile()
        {
            // tmp остаётся, если процесс упал ровно на записи
            await Create().SaveAsync(new CheckInContext { ReservationId = "BR-1" },
                CancellationToken.None);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, "{ half-written");

            await Create().ClearAsync(CancellationToken.None);

            Assert.False(File.Exists(tmp));
        }

        [Fact]
        public async Task Save_WritesAtomically_NoPartialJsonLeft()
        {
            var store = Create();
            await store.SaveAsync(new CheckInContext { ReservationId = "BR-1" },
                CancellationToken.None);

            // Валидный JSON целиком, а не обрывок
            var json = await File.ReadAllTextAsync(_path);
            Assert.Contains("\"ReservationId\": \"BR-1\"", json.Replace("\r\n", "\n"));
            Assert.False(File.Exists(_path + ".tmp"));
        }

        [Fact]
        public async Task Save_DoesNotThrow_WhenDirectoryIsNotWritable()
        {
            // Чекпоинт — вспомогательный: его сбой не должен ронять заселение
            var store = new FileCheckInSessionStore(
                NullLogger<FileCheckInSessionStore>.Instance,
                Path.Combine(_dir, "sub", "dir", "\0invalid", "session.json"));

            await store.SaveAsync(new CheckInContext { ReservationId = "BR-1" },
                CancellationToken.None);
        }

        [Fact]
        public async Task Load_CorruptedFile_ReturnsNull()
        {
            Directory.CreateDirectory(_dir);
            await File.WriteAllTextAsync(_path, "{ this is not json");
            Assert.Null(await Create().LoadAsync(CancellationToken.None));
        }
    }
}
