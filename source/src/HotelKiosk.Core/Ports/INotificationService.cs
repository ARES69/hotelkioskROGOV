using System.Threading;
using System.Threading.Tasks;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Уведомления персоналу отеля (Telegram). Киоск работает без стойки,
    /// но персонал должен узнавать о событиях: успешное заселение, сбой
    /// записи карты (гость стоит без ключа), сбой Logus.
    /// Fire-and-forget по своей природе: сбой отправки не должен влиять
    /// на основной флоу заселения.
    /// </summary>
    public interface INotificationService
    {
        Task NotifyStaffAsync(string message, CancellationToken cancellationToken);
    }
}
