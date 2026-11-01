using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;

namespace HotelKiosk.Tests.Fakes
{
    public class FakeLogusService : ILogusService
    {
        /// <summary>
        /// Фейк умеет всё, кроме паспортных данных: их отключение проверяется
        /// отдельно, здесь важен полный набор для основных тестов.
        /// </summary>
        public PmsCapabilities Capabilities { get; set; } = new(
            Vendor: "fake",
            CanSearchReservations: true,
            CanCheckIn: true,
            CanCancel: true,
            CanPostPayment: true,
            CanListArrivals: true,
            CanSendGuestDocuments: true,
            CanReadReservationDetails: true);

        public bool FailFind { get; set; }
        public bool FailUpdate { get; set; }
        public bool FailCancel { get; set; }
        public bool FailAddPayment { get; set; }

        // Гостевые документы: флаги отказа по стадиям
        public bool FailGetReservation { get; set; }
        public bool FailCreateProfile { get; set; }
        public bool FailUpdateGuests { get; set; }

        /// <summary>Бронь без назначенного номера + RoomsInfo со свободными.</summary>
        public bool NoRoomAssigned { get; set; }

        /// <summary>
        /// Ответ следующего гостя отличается от предыдущего — иначе тесты на
        /// границы сессии не могут отличить «новая бронь» от «осталась старая».
        /// </summary>
        public string LastNameOverride { get; set; }

        public decimal? PriceOverride { get; set; }

        /// <summary>
        /// Готовит фейк к выдаче другой брони следующему гостю.
        /// reservationNo используется как идентификатор ответа (Id брони).
        /// </summary>
        public void ResetForSecondGuest(string reservationNo, string lastName)
        {
            LastNameOverride = lastName;
            // Другая сумма: по ней видно, какая бронь сейчас в сессии
            PriceOverride = 7300m;
        }
        public int CancelCalls { get; private set; }
        public int SelfCheckInCalls { get; private set; }
        public int AddPaymentCalls { get; private set; }
        /// <summary>Последний аргумент bankTransactionId из AddPaymentAsync.</summary>
        public string LastPaymentUniqueId { get; private set; }

        // Гостевые документы (GuestProfile / UpdateGuests)
        /// <summary>Guid брони, который вернёт GetReservationAsync.</summary>
        public string ReservationGuid { get; set; } = "res-guid-0001";
        public int GuestProfileCalls { get; private set; }
        public int UpdateGuestsCalls { get; private set; }
        public GuestDocumentData LastGuestDocument { get; private set; }
        public string LastUpdateGuestsGuid { get; private set; }

        [Obsolete("Используйте AuthenticateSelfCheckinAsync — поиск по номеру брони + фамилии")]
        public Task<Result<ReservationData>> GetReservationByPassportAsync(
            string passport, CancellationToken ct)
        {
            if (FailFind)
                return Task.FromResult(Result<ReservationData>.BusinessFailure("Not found"));

            return Task.FromResult(Result<ReservationData>.Success(new ReservationData(
                "R-001", "G-001", "101", 5000m,
                DateTime.Today, DateTime.Today.AddDays(2),
                LogusReservationStatus.New)));
        }

        [Obsolete("Используйте SelfCheckInAsync или CancelReservationAsync вместо прямого обновления статуса")]
        public Task<Result<bool>> UpdateReservationStatusAsync(
            string id, LogusReservationStatus status, CancellationToken ct)
        {
            if (FailUpdate)
                return Task.FromResult(Result<bool>.SystemFailure("Logus down"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<Result<SelfCheckinInfo>> AuthenticateSelfCheckinAsync(
            string reservationNo, string confirmationMode, string key, CancellationToken ct)
        {
            if (FailFind)
                return Task.FromResult(Result<SelfCheckinInfo>.BusinessFailure("Not found"));

            var info = new SelfCheckinInfo
            {
                Guest = new SelfCheckinGuest
                {
                    Id = "G-001", FirstName = "Иван",
                    LastName = LastNameOverride ?? "Иванов"
                },
                Reservation = new SelfCheckinReservation
                {
                    Id = reservationNo ?? "BR-001", RoomCode = "101",
                    Price = PriceOverride ?? 5000m,
                    ArrivalDate = DateTime.Today, DepartureDate = DateTime.Today.AddDays(2)
                }
            };

            // Сценарий «номер не назначен»: RoomCode=null + список свободных
            if (NoRoomAssigned)
            {
                info.Reservation.RoomCode = null;
                info.RoomsInfo = new SelfCheckinRoomsInfo
                {
                    Rooms = new List<SelfCheckinRoom>
                    {
                        new() { RoomCode = "301", RoomTypeCode = "STD", IsOccupied = true },
                        new() { RoomCode = "305", RoomTypeCode = "STD", FloorCode = "3", IsOccupied = false },
                        new() { RoomCode = "407", RoomTypeCode = "LUX", FloorCode = "4", IsOccupied = false }
                    },
                    RoomTypes = new List<SelfCheckinRoomType>
                    {
                        new() { Code = "STD", Name = "Стандарт" },
                        new() { Code = "LUX", Name = "Люкс" }
                    }
                };
            }

            return Task.FromResult(Result<SelfCheckinInfo>.Success(info));
        }

        public Task<Result<bool>> SelfCheckInAsync(
            string reservationNo, string roomNo, CancellationToken ct)
        {
            SelfCheckInCalls++;
            if (FailUpdate)
                return Task.FromResult(Result<bool>.SystemFailure("Logus down"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<Result<bool>> CancelReservationAsync(
            string reservationId, string cancellationReasonCode, CancellationToken ct)
        {
            CancelCalls++;
            if (FailCancel)
                return Task.FromResult(Result<bool>.SystemFailure("Cancel failed"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<Result<bool>> AddPaymentAsync(
            string reservationGenericNo, decimal amount, string bankTransactionId, CancellationToken ct)
        {
            AddPaymentCalls++;
            LastPaymentUniqueId = bankTransactionId;
            if (FailAddPayment)
                return Task.FromResult(Result<bool>.SystemFailure("AddPayment failed"));
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task<Result<IReadOnlyList<ArrivalInfo>>> GetArrivalsAsync(
            DateTime arrivalDate, CancellationToken ct)
        {
            IReadOnlyList<ArrivalInfo> arrivals = new List<ArrivalInfo>
            {
                new() { GenericNo = "20001", LastName = "Иванов", FirstName = "Иван",
                    ArrivalDate = arrivalDate, DepartureDate = arrivalDate.AddDays(2),
                    StayAmount = 5000m, Balance = 5000m },
                new() { GenericNo = "20002", LastName = "Петров", FirstName = "Пётр",
                    ArrivalDate = arrivalDate, DepartureDate = arrivalDate.AddDays(1),
                    StayAmount = 3000m, Balance = 0m }
            };
            return Task.FromResult(Result<IReadOnlyList<ArrivalInfo>>.Success(arrivals));
        }

        public Task<Result<ReservationDetails>> GetReservationAsync(
            string reservationGenericNo, CancellationToken ct)
        {
            if (FailGetReservation)
                return Task.FromResult(Result<ReservationDetails>.BusinessFailure("Guid not found"));

            return Task.FromResult(Result<ReservationDetails>.Success(new ReservationDetails
            {
                Guid = ReservationGuid,
                GenericNo = reservationGenericNo
            }));
        }

        public Task<Result<GuestProfileRef>> CreateGuestProfileAsync(
            GuestDocumentData guest, CancellationToken ct)
        {
            GuestProfileCalls++;
            LastGuestDocument = guest;
            if (FailCreateProfile)
                return Task.FromResult(Result<GuestProfileRef>.BusinessFailure("GuestProfile failed"));

            return Task.FromResult(Result<GuestProfileRef>.Success(new GuestProfileRef
            {
                PersonId = "P-777",
                ProfileGuid = "prof-guid-0777"
            }));
        }

        public Task<Result<bool>> UpdateReservationGuestsAsync(
            string reservationGuid, GuestDocumentData mainGuest,
            GuestProfileRef profile, CancellationToken ct)
        {
            UpdateGuestsCalls++;
            LastUpdateGuestsGuid = reservationGuid;
            if (FailUpdateGuests)
                return Task.FromResult(Result<bool>.BusinessFailure("UpdateGuests failed"));

            return Task.FromResult(Result<bool>.Success(true));
        }

        /// <summary>Живость PMS. По умолчанию жива; FailPing= true — нет.</summary>
        public bool FailPing { get; set; }

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(!FailPing);
    }
}
