using System;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
#pragma warning disable CS0618
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeVisionLineService : IVisionLineService
    {
        public bool FailEncode { get; set; }
        public int EncodeCalls { get; private set; }

        public Task<Result<KeyCardResult>> EncodeKeyCardAsync(
            string room, DateTime from, DateTime to, CancellationToken ct)
        {
            EncodeCalls++;
            if (FailEncode)
                return Task.FromResult(Result<KeyCardResult>.SystemFailure("Encoder jammed"));
            return Task.FromResult(Result<KeyCardResult>.Success(
                new KeyCardResult(true, "UID-ABC123", null)));
        }

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
