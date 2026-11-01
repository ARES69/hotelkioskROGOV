using System;
using System.Collections.Generic;

namespace HotelKiosk.Infrastructure.Adapters.GenericPms
{
    /// <summary>
    /// Настройки конфигурируемой интеграции с PMS (секция Pms:Generic).
    ///
    /// Идея: киоск говорит с PMS на каноническом языке (SelfCheckinInfo,
    /// ArrivalInfo и т.д.), а конкретная система отдаёт свои URL и свои имена
    /// полей. Описание этого расхождения живёт здесь, в appsettings.json,
    /// поэтому подключение новой PMS — правка конфига, а не сборка.
    ///
    /// Поля Paths — точечные пути в ответе PMS ("reservation.room.number").
    /// Поля Body/Path — шаблоны с плейсхолдерами {reservationNo}, {key} и т.п.
    /// </summary>
    public class GenericPmsOptions
    {
        public const string SectionName = "Pms:Generic";

        /// <summary>Код вендора для диагностики: "shelter", "1c-hotel", "winhotel".</summary>
        public string Vendor { get; set; } = "generic";

        public string BaseUrl { get; set; } = "http://localhost:8080/api";

        /// <summary>
        /// Схема авторизации: none | basic | bearer | token-query | token-header.
        /// У каждой PMS своя, и это самая частая причина «ки��ск не видит бронь».
        /// </summary>
        public string AuthMode { get; set; } = "basic";

        public string Login { get; set; }
        public string Password { get; set; }

        /// <summary>Значение для bearer / token-header.</summary>
        public string Token { get; set; }

        /// <summary>Имя заголовка для token-header (напр. "X-Api-Key").</summary>
        public string TokenHeaderName { get; set; } = "X-Api-Key";

        /// <summary>Имя параметра для token-query (напр. "token" или "access_token").</summary>
        public string TokenQueryName { get; set; } = "token";

        /// <summary>
        /// Префикс значения токена: некоторые PMS требуют "Bearer " / "Token "
        /// перед самим токеном. Пусто = токен подставляется как есть.
        /// </summary>
        public string TokenPrefix { get; set; } = "";

        public int TimeoutSeconds { get; set; } = 20;

        /// <summary>Заголовки, добавляемые ко всем запросам (X-Hotel-Id и т.п.).</summary>
        public Dictionary<string, string> DefaultHeaders { get; set; } = new();

        // ==================================================================
        //  Операции
        // ==================================================================

        /// <summary>Поиск брони по номеру + фамилии. Обязательна.</summary>
        public OperationOptions Authenticate { get; set; } = new();

        /// <summary>Бизнес-заселение (перевод брони в статус проживания).</summary>
        public OperationOptions CheckIn { get; set; } = new();

        /// <summary>Отмена брони.</summary>
        public OperationOptions Cancel { get; set; } = new();

        /// <summary>Постинг оплаты в счёт брони.</summary>
        public OperationOptions AddPayment { get; set; } = new();

        /// <summary>Заезды на дату — для экрана «сегодня заезжают».</summary>
        public OperationOptions Arrivals { get; set; } = new();

        /// <summary>Бронь по номеру: чаще всего нужна для внутреннего идентификатора.</summary>
        public OperationOptions ReservationDetails { get; set; } = new();

        /// <summary>Создание/обновление профиля гостя с паспортом.</summary>
        public OperationOptions UpsertGuest { get; set; } = new();

        /// <summary>Привязка гостя к брони.</summary>
        public OperationOptions AttachGuest { get; set; } = new();

        /// <summary>Health-check. Пустой Path = запрос к BaseUrl.</summary>
        public OperationOptions Ping { get; set; } = new();

        /// <summary>
        /// Как разбирать ответы, где «ошибка» приходит кодом в теле, а не
        /// HTTP-статусом: some — считать тело успешным, опираясь на Paths.Success
        /// </summary>
        public bool ErrorInBody { get; set; } = true;

        /// <summary>Поле с признаком успеха в теле ответа (при ErrorInBody).</summary>
        public string SuccessPath { get; set; }

        /// <summary>Поле с текстом ошибки в теле ответа.</summary>
        public string ErrorMessagePath { get; set; } = "error.message";

        /// <summary>
        /// Передавать паспортные данные гостя. У части PMS нет эндпоинтов
        /// профиля гостя — тогда оставляем false и в Logus паспорт не уйдёт,
        /// но заселение продолжится (см. KioskActionAdapter.GuestUpsertAsync).
        /// </summary>
        public bool SendGuestDocuments { get; set; } = true;

        /// <summary>Код типа документа для PMS.</summary>
        public string DocumentTypeCode { get; set; } = "PASSPORT";

        public string DocumentTypeName { get; set; } = "Паспорт гражданина РФ";

        /// <summary>Идентификатор отеля/свойства в PMS, если требуется в запросах.</summary>
        public string PropertyId { get; set; }

        /// <summary>Код транзакции оплаты. Пусто = постинг оплат отключён.</summary>
        public string PaymentTransactionCode { get; set; }

        /// <summary>
        /// Словарь кодов причин отмены. Ключ — наш внутренний код
        /// (Compensation / GuestRequest / …), значение — код PMS.
        /// </summary>
        public Dictionary<string, string> CancellationReasonCodes { get; set; } = new()
        {
            ["Compensation"] = "Compensation",
            ["GuestRequest"] = "GuestRequest",
            ["SystemError"] = "SystemError",
            ["Overbooking"] = "Overbooking"
        };
    }

    /// <summary>
    /// Описание одной операции PMS: URL, тело и пути в ответе.
    /// Заполняется частично — то, что PMS не поддерживает, просто не описывается.
    /// </summary>
    public class OperationOptions
    {
        /// <summary>
        /// Включена ли операция. Операция без Path считается неподдерживаемой:
        /// киоск не будет её вызывать, а скажет «PMS не поддерживает».
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>HTTP-метод. По умолчанию POST, если задан Path.</summary>
        public string Method { get; set; } = "POST";

        /// <summary>URL относительно BaseUrl. Поддерживает плейсхолдеры.</summary>
        public string Path { get; set; }
        /// <summary>
        /// Операция описана и включена. Не описанная операция считается
        /// неподдерживаемой: киоск не будет её вызывать, а скажет
        /// «PMS не поддерживает», вместо того чтобы получить 404.
        /// </summary>
        public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Path);

        /// <summary>
        /// Тело запроса как шаблон. Значения вида "@{amount:0.00}" подставляются
        /// текстом; прочие значения остаются литералами JSON.
        /// Пример: {"no":"@{reservationId}","amount":"@{amount:0.00}"}
        /// </summary>
        public Dictionary<string, object> Body { get; set; }

        /// <summary>Пути в ответе → канонические поля (см. GenericPmsService).</summary>
        public FieldMap Paths { get; set; } = new();

        /// <summary>
        /// Заголовки этой операции (напр. "Idempotency-Key": "@{idempotencyKey}").
        /// </summary>
        public Dictionary<string, string> Headers { get; set; }
    }

    /// <summary>
    /// Пути в JSON-ответе PMS для канонических полей киоска.
    /// Пустой путь означает «взять из корня ответа».
    /// </summary>
    public class FieldMap
    {
        // --- SelfCheckinInfo ---
        public string GuestId { get; set; }
        public string GuestFirstName { get; set; }
        public string GuestLastName { get; set; }
        public string GuestMiddleName { get; set; }
        public string GuestBirthDate { get; set; }

        public string ReservationId { get; set; }
        public string ReservationRoomCode { get; set; }
        public string ReservationRoomTypeCode { get; set; }
        public string ReservationGuestsCount { get; set; }
        public string ReservationArrival { get; set; }
        public string ReservationDeparture { get; set; }
        public string ReservationPrice { get; set; }

        public string Currency { get; set; }

        /// <summary>Массив свободных номеров: путь к объекту с двумя полями ниже.</summary>
        public string AvailableRooms { get; set; }
        public string RoomCode { get; set; }
        public string RoomTypeCode { get; set; }
        public string RoomFloor { get; set; }
        public string RoomBuilding { get; set; }
        public string RoomOccupied { get; set; }

        /// <summary>Массив типов номеров: путь к объекту с Name.</summary>
        public string RoomTypes { get; set; }
        public string RoomTypeName { get; set; }

        // --- ArrivalInfo ---
        public string ArrivalsArray { get; set; }
        public string ArrivalGenericNo { get; set; }
        public string ArrivalGuid { get; set; }
        public string ArrivalLastName { get; set; }
        public string ArrivalFirstName { get; set; }
        public string ArrivalArrivalDate { get; set; }
        public string ArrivalDepartureDate { get; set; }
        public string ArrivalStayAmount { get; set; }
        public string ArrivalBalance { get; set; }

        // --- ReservationDetails ---
        public string ReservationGuid { get; set; }

        // --- GuestProfile ---
        public string PersonId { get; set; }
        public string ProfileGuid { get; set; }
    }
}
