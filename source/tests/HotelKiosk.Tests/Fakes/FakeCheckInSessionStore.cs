using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeCheckInSessionStore : ICheckInSessionStore
    {
        public CheckInContext Saved { get; private set; }
        public int SaveCalls { get; private set; }
        public int ClearCalls { get; private set; }

        public Task SaveAsync(CheckInContext ctx, CancellationToken ct)
        {
            Saved = ctx;
            SaveCalls++;
            return Task.CompletedTask;
        }

        public Task<CheckInContext> LoadAsync(CancellationToken ct) => Task.FromResult(Saved);

        public Task ClearAsync(CancellationToken ct)
        {
            Saved = null;
            ClearCalls++;
            return Task.CompletedTask;
        }
    }
}
