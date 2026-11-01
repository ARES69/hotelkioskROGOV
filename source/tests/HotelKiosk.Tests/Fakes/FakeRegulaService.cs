using System;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeRegulaService : IRegulaService
    {
        public Func<CancellationToken, Task<Result<PassportData>>> ScanImpl { get; set; }
        public bool ThrowOnScan { get; set; }

        // --- камера: кадр из payload.image ---
        public string LastPhoto { get; private set; }
        public int PhotoCalls { get; private set; }
        public int ScanCalls { get; private set; }
        public bool ThrowOnPhoto { get; set; }
        public Result<PassportData> PhotoResult { get; set; }
        public Func<string, Result<PassportData>> PhotoImpl { get; set; }

        public Task<Result<PassportData>> ScanPassportAsync(CancellationToken ct)
        {
            ScanCalls++;
            if (ThrowOnScan) throw new InvalidOperationException("Regula down");
            if (ScanImpl != null) return ScanImpl(ct);
            return Task.FromResult(Result<PassportData>.Success(
                new PassportData("4500123456", "IVAN IVANOV", new DateTime(1990, 1, 1))));
        }

        public Task<Result<PassportData>> RecognizeFromPhotoAsync(
            string imageBase64, CancellationToken ct)
        {
            PhotoCalls++;
            LastPhoto = imageBase64;
            if (ThrowOnPhoto) throw new InvalidOperationException("camera down");
            if (PhotoImpl != null) return Task.FromResult(PhotoImpl(imageBase64));
            if (PhotoResult != null) return Task.FromResult(PhotoResult);
            // Тот же результат, что и со считывателя: OCR один и тот же
            return Task.FromResult(Result<PassportData>.Success(
                new PassportData("4500123456", "IVAN IVANOV", new DateTime(1990, 1, 1))));
        }

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
