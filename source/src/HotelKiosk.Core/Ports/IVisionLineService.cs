using System;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Запись ключ-карт через сервер VisionLine (ASSA ABLOY / VingCard).
    /// Киоск вызывает напрямую: Logus WebAPI SelfCheckin/CheckIn делает только
    /// бизнес-заселение и не пишет ключи — модули записи (Keylock.*) живут
    /// в Logus Desktop на стойке регистрации.
    /// </summary>
    public interface IVisionLineService
    {
        Task<Result<KeyCardResult>> EncodeKeyCardAsync(
            string roomNumber, DateTime checkIn, DateTime checkOut,
            CancellationToken cancellationToken);

        Task<bool> PingAsync(CancellationToken cancellationToken);
    }
}
