using System;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;

namespace HotelKiosk.Core.Ports.Pms
{
    /// <summary>
    /// Порт PMS (Property Management System) — система управления отелем,
    /// где живут брони, номера и статусы проживания.
    ///
    /// Киоск работает только через этот интерфейс и ничего не знает о том,
    /// Logus это, Shelter, 1С:Отель или WinHotel. Конкретная PMS подключается
    /// адаптером в Infrastructure (LogusApiService — боевой, GenericPmsService —
    /// для PMS без нашего SDK, конфигурируется в appsettings.json).
    ///
    /// Набор операций — минимально достаточный для самостоятельного заселения.
    /// Расширять PMS новыми эндпоинтами не нужно: всё, что киоску не
    /// требуется, остаётся на стороне стойки.
    /// </summary>
    public interface IPmsProvider
    {
        /// <summary>
        /// Что умеет подключённая PMS. Позволяет интерфейсу честно сказать
        /// «эта система такого не умеет», вместо того чтобы падать с 404.
        /// </summary>
        PmsCapabilities Capabilities { get; }

        /// <summary>
        /// Найти бронь и подтвердить личность гостя.
        ///
        /// Ключ — НЕ паспорт: паспорт входит в результат как данные гостя,
        /// а подтверждением служит то, чем располагает гость у стойки
        /// (фамилия, код брони, телефон).
        ///
        /// Для Logus: POST /api/SelfCheckin/Authenticate.
        /// </summary>
        Task<Result<SelfCheckinInfo>> AuthenticateSelfCheckinAsync(
            string reservationNo, string confirmationMode, string key,
            CancellationToken cancellationToken);

        /// <summary>
        /// Перевести бронь в статус проживания.
        /// Для Logus: POST /api/SelfCheckin/CheckIn.
        /// </summary>
        Task<Result<bool>> SelfCheckInAsync(
            string reservationNo, string roomNo,
            CancellationToken cancellationToken);

        /// <summary>
        /// Отменить бронь с указанием причины.
        /// Для Logus: POST /api/SelfCheckin/Cancel.
        /// </summary>
        Task<Result<bool>> CancelReservationAsync(
            string reservationId, string cancellationReasonCode,
            CancellationToken cancellationToken);

        /// <summary>
        /// Провести оплату в счёт брони.
        /// bankTransactionId — идентификатор банковской транзакции: в PMS он
        /// защищает от двойного проведения одного платежа.
        /// Для Logus: POST /api/Reservation/AddPayment.
        /// </summary>
        Task<Result<bool>> AddPaymentAsync(
            string reservationGenericNo, decimal amount, string bankTransactionId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Заезды на дату — для экрана «сегодня заезжают».
        /// Для Logus: POST /api/Reservation/QuickSearch.
        /// </summary>
        Task<Result<IReadOnlyList<ArrivalInfo>>> GetArrivalsAsync(
            DateTime arrivalDate, CancellationToken cancellationToken);

        /// <summary>
        /// Бронь по номеру. Киоску нужен прежде всего внутренний
        /// идентификатор брони — по нему привязываются паспортные данные.
        /// Для Logus: GET /api/Reservation/{"{genericNo}"}.
        /// </summary>
        Task<Result<ReservationDetails>> GetReservationAsync(
            string reservationGenericNo, CancellationToken cancellationToken);

        /// <summary>
        /// Создать или обновить профиль гостя с паспортными данными.
        /// Для Logus: POST /api/GuestProfile.
        /// </summary>
        Task<Result<GuestProfileRef>> CreateGuestProfileAsync(
            GuestDocumentData guest, CancellationToken cancellationToken);

        /// <summary>
        /// Привязать гостя с паспортом к брони.
        /// Для Logus: POST /api/Reservation/{"{reservationGuid}"}/UpdateGuests.
        /// </summary>
        Task<Result<bool>> UpdateReservationGuestsAsync(
            string reservationGuid, GuestDocumentData mainGuest,
            GuestProfileRef profile, CancellationToken cancellationToken);

        /// <summary>Живость PMS для preflight киоска.</summary>
        Task<bool> PingAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Возможности подключённой PMS.
    ///
    /// Нужны для трёх вещей: честной диагностики в preflight, понятных
    /// сообщений гостю вместо «что-то пошло не так», и корректного
    /// распределения обязательных шагов — если PMS не умеет список заездов,
    /// экран «сегодня заезжают» просто не показывается.
    /// </summary>
    public sealed record PmsCapabilities(
        string Vendor,
        bool CanSearchReservations,
        bool CanCheckIn,
        bool CanCancel,
        bool CanPostPayment,
        bool CanListArrivals,
        bool CanSendGuestDocuments,
        bool CanReadReservationDetails)
    {
        /// <summary>
        /// Минимум, без которого заселение невозможно: найти бронь и заселить.
        /// Всё остальное можно не поддерживать.
        /// </summary>
        public bool CanCheckInAtAll => CanSearchReservations && CanCheckIn;

        /// <summary>Шаги, которых не хватает до полного флоу (не блокеры).</summary>
        public string[] MissingOptional => BuildMissing();

        private string[] BuildMissing()
        {
            var missing = new System.Collections.Generic.List<string>();

            if (!CanPostPayment) missing.Add("постинг оплаты в счёт брони");
            if (!CanListArrivals) missing.Add("список заездов");
            if (!CanSendGuestDocuments) missing.Add("передача паспортных данных");
            if (!CanReadReservationDetails) missing.Add("чтение данных брони");

            return missing.ToArray();
        }

        /// <summary>Человекочитаемое описание для логов и диалога оператора.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(Vendor) ? "PMS" : Vendor.ToUpperInvariant());

            if (MissingOptional.Length == 0) return sb.Append(": все операции доступны").ToString();

            return sb.Append(": не поддерживается — ")
                     .Append(string.Join(", ", MissingOptional))
                     .ToString();
        }
    }
}
