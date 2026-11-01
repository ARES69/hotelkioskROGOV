using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Сохраняет прогресс текущей сессии заселения на диск, чтобы падение
    /// процесса (или зависание/перезагрузка ПК) не теряло данные гостя молча.
    /// На одной стойке одновременно идёт ровно одно заселение, поэтому это
    /// не полноценная БД, а простой чекпоинт последнего известного состояния.
    /// </summary>
    public interface ICheckInSessionStore
    {
        Task SaveAsync(CheckInContext ctx, CancellationToken ct);

        /// <summary>Возвращает незавершённую сессию с прошлого запуска, либо null, если её нет.</summary>
        Task<CheckInContext> LoadAsync(CancellationToken ct);

        Task ClearAsync(CancellationToken ct);
    }
}
