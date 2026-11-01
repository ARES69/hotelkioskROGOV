using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeNotificationService : INotificationService
    {
        public List<string> SentMessages { get; } = new();

        public Task NotifyStaffAsync(string message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
