using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    /// <summary>
    /// Порт PMS конкретно для Logus.
    ///
    /// Основной контракт — <see cref="Pms.IPmsProvider"/>, от которого киоск не
    /// зависит: подключить Shelter, 1С:Отель или любую другую PMS можно
    /// конфигурацией, без сборки.
    ///
    /// Этот интерфейс наследует IPmsProvider и добавляет два метода,
    /// специфичных именно для Logus (GetReservationByPassportAsync,
    /// UpdateReservationStatusAsync). Оба помечены Obsolete и из потока
    /// заселения не вызываются. Дублировать общие методы здесь незачем —
    /// они наследуются как есть.
    ///
    /// Новый код должен зависеть от IPmsProvider.
    /// </summary>
    public interface ILogusService : Pms.IPmsProvider
    {
        [Obsolete("Используйте AuthenticateSelfCheckinAsync — поиск по номеру брони + фамилии")]
        Task<Result<ReservationData>> GetReservationByPassportAsync(
            string passportNumber, CancellationToken cancellationToken);

        [Obsolete("Используйте SelfCheckInAsync или CancelReservationAsync вместо прямого обновления статуса")]
        Task<Result<bool>> UpdateReservationStatusAsync(
            string reservationId, LogusReservationStatus status, CancellationToken cancellationToken);
    }
}
