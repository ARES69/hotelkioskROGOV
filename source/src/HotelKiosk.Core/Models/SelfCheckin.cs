using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HotelKiosk.Core.Models
{
    // Соответствует Logus.HMS.WebApi.Models.CheckIn.* — см. /api/SelfCheckin/Authenticate

    public class SelfCheckinGuest
    {
        public string Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public DateTime? BirthDate { get; set; }
    }

    public class SelfCheckinReservation
    {
        public string Id { get; set; }
        public string RoomTypeCode { get; set; }
        public string RoomCode { get; set; }
        public int GuestsCount { get; set; }
        public DateTime ArrivalDate { get; set; }
        public DateTime DepartureDate { get; set; }
        public decimal Price { get; set; }
    }

    public class SelfCheckinUpgradeOption
    {
        public string RoomTypeCode { get; set; }
        public decimal Price { get; set; }
    }

    public class SelfCheckinExtraService
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    public class SelfCheckinRoom
    {
        public string BuildingCode { get; set; }
        public string FloorCode { get; set; }
        public string RoomCode { get; set; }
        public string RoomTypeCode { get; set; }
        public bool IsOccupied { get; set; }
        public List<SelfCheckinAttributeValue> Attributes { get; set; } = new();
    }

    public class SelfCheckinAttributeValue
    {
        public string Code { get; set; }
        public List<string> Value { get; set; } = new();
    }

    public class SelfCheckinAttributeMetadata
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public bool MultipleValuesAllowed { get; set; }
        public List<string> Values { get; set; } = new();
    }

    public class SelfCheckinRoomType
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
    }

    public class SelfCheckinRoomsInfo
    {
        public List<SelfCheckinRoom> Rooms { get; set; } = new();
        public List<SelfCheckinAttributeMetadata> RoomAttributes { get; set; } = new();
        public List<SelfCheckinRoomType> RoomTypes { get; set; } = new();
    }

    /// <summary>Ответ /api/SelfCheckin/Authenticate.</summary>
    public class SelfCheckinInfo
    {
        public SelfCheckinGuest Guest { get; set; }
        public SelfCheckinReservation Reservation { get; set; }
        public List<SelfCheckinUpgradeOption> UpgradeOptions { get; set; } = new();
        public List<SelfCheckinExtraService> ExtraServices { get; set; } = new();
        public SelfCheckinRoomsInfo RoomsInfo { get; set; }
        public string CurrencyNls { get; set; }
    }

    /// <summary>
    /// Формат ошибки Logus WebAPI. Проверено на живом API (2026-08):
    /// настоящая причина приходит в ExceptionMessage (например,
    /// "Бронь с номером '...' не найдена"), а Message — это общая
    /// фраза "An error has occurred."
    /// </summary>
    public class LogusErrorResponse
    {
        public string Message { get; set; }
        public string ExceptionMessage { get; set; }
        public string ExceptionType { get; set; }
    }

    /// <summary>
    /// Способ подтверждения личности при поиске брони (см. ConfirmationMode
    /// в запросе Authenticate). "LastName" подтверждён примером в Swagger;
    /// другие значения enum'а (например, по телефону/email) нужно уточнить
    /// у Logus — набор значений в самой спецификации не перечислен явно.
    /// </summary>
    public static class SelfCheckinConfirmationMode
    {
        public const string LastName = "LastName";
    }

    /// <summary>
    /// Короткая информация о заезде — элемент ответа /api/Reservation/QuickSearch.
    /// Поля сверены с LogusBookingsResponse рабочего клиента Реджисты.
    /// </summary>
    public class ArrivalInfo
    {
        public string GenericNo { get; set; }
        public string Guid { get; set; }
        public string LastName { get; set; }
        public string FirstName { get; set; }
        public DateTime ArrivalDate { get; set; }
        public DateTime DepartureDate { get; set; }
        /// <summary>Сумма проживания (LocalCurrencyStayAmount).</summary>
        public decimal? StayAmount { get; set; }
        /// <summary>Баланс брони (LocalCurrencyBalance).</summary>
        public decimal? Balance { get; set; }
    }

    /// <summary>
    /// Полная бронь — ответ GET /api/Reservation/{"{genericNo}"}.
    /// Разбирается толерантно: киоску важен прежде всего Guid брони
    /// (внутренний идентификатор, по которому адресуется UpdateGuests).
    /// </summary>
    public class ReservationDetails
    {
        /// <summary>Внутренний GUID брони — ключ для UpdateGuests.</summary>
        public string Guid { get; set; }
        /// <summary>Номер брони (genericNo), если пришёл в ответе.</summary>
        public string GenericNo { get; set; }

        /// <summary>Толерантный разбор: guid может лежать в разных полях корня.</summary>
        public static ReservationDetails FromJson(JsonDocument doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string TryGet(params string[] names)
            {
                foreach (var n in names)
                    if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }

            var details = new ReservationDetails
            {
                GenericNo = TryGet("GenericNo", "genericNo", "Id", "id")
            };

            // Guid брони: в корне либо в обёртке reservation
            details.Guid = TryGet("Guid", "guid", "GUID", "ReservationGuid", "reservationGuid");
            if (string.IsNullOrEmpty(details.Guid) &&
                root.TryGetProperty("reservation", out var res) &&
                res.ValueKind == JsonValueKind.Object)
            {
                foreach (var n in new[] { "Guid", "guid" })
                    if (res.TryGetProperty(n, out var g) && g.ValueKind == JsonValueKind.String)
                    {
                        details.Guid = g.GetString();
                        break;
                    }
            }

            return string.IsNullOrEmpty(details.Guid) ? null : details;
        }
    }
}
