using System;
using System.Collections.Generic;

namespace HotelKiosk.Core.Models
{
    /// <summary>
    /// Ссылка на созданный в Logus профиль гостя.
    /// Контракт сверен с рабочим клиентом Реджисты: POST /api/GuestProfile
    /// возвращает идентификаторы, которые затем используются в
    /// POST /api/Reservation/{"{guid}"}/UpdateGuests (MainProfileGuid + Id).
    /// </summary>
    public class GuestProfileRef
    {
        /// <summary>Числовой идентификатор персоны в Logus (personId).</summary>
        public string PersonId { get; set; }

        /// <summary>GUID профиля гостя (profileGuid).</summary>
        public string ProfileGuid { get; set; }
    }

    /// <summary>
    /// Нормализованные данные гостя для передачи в Logus:
    /// ФИО + паспорт + адрес регистрации (что распознал сканер).
    /// </summary>
    public class GuestDocumentData
    {
        public string LastName { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public DateTime? BirthDate { get; set; }
        /// <summary>"M" / "F" — как в примере Реджисты.</summary>
        public string Sex { get; set; }
        public string CountryCode { get; set; } = "RUS";

        // Паспорт
        public string DocumentSeries { get; set; }
        public string DocumentNumber { get; set; }
        public DateTime? IssueDate { get; set; }
        public string DepartmentCode { get; set; }
        public string IssuedBy { get; set; }
        public string DocumentTypeCode { get; set; }
        public string DocumentTypeName { get; set; }

        // Адрес регистрации (если распознан)
        public string BirthPlace { get; set; }
        public string City { get; set; }
        public string Street { get; set; }
        public string HouseNo { get; set; }
        public string BuildingNo { get; set; }
        public string FlatNo { get; set; }

        /// <summary>
        /// Собирает данные из результата сканера. Если ФИО не разбито на части,
        /// FullName раскладывается по пробелам: фамилия, имя, отчество.
        /// </summary>
        public static GuestDocumentData FromPassport(PassportData p)
        {
            if (p == null) return null;

            var lastName = p.LastName;
            var firstName = p.FirstName;
            var middleName = p.MiddleName;
            if (string.IsNullOrEmpty(lastName) && !string.IsNullOrEmpty(p.FullName))
            {
                var parts = p.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                lastName = parts.Length > 0 ? parts[0] : null;
                firstName = parts.Length > 1 ? parts[1] : null;
                middleName = parts.Length > 2 ? string.Join(' ', parts[2..]) : null;
            }

            return new GuestDocumentData
            {
                LastName = lastName,
                FirstName = firstName,
                MiddleName = middleName,
                BirthDate = p.DateOfBirth,
                Sex = NormalizeSex(p.Sex),
                CountryCode = string.IsNullOrEmpty(p.CountryCode) ? "RUS" : p.CountryCode,
                DocumentSeries = p.Series,
                DocumentNumber = ExtractNumber(p.PassportNumber),
                IssueDate = p.IssueDate,
                DepartmentCode = p.DepartmentCode,
                IssuedBy = p.IssuedBy,
                BirthPlace = p.BirthPlace
            };
        }

        /// <summary>Приводит пол к формату Logus ("M"/"F"), терпит "М"/"Ж" и мужской/женский.</summary>
        private static string NormalizeSex(string sex)
        {
            if (string.IsNullOrEmpty(sex)) return null;
            return char.ToUpperInvariant(sex[0]) switch
            {
                'M' or 'М' => "M",
                'F' or 'Ж' => "F",
                _ => null
            };
        }

        /// <summary>
        /// Сканер часто отдаёт номер вместе с серией ("5217 660017").
        /// Для Logus нужен только номер — берём последний токен.
        /// </summary>
        private static string ExtractNumber(string passportNumber)
        {
            if (string.IsNullOrWhiteSpace(passportNumber)) return null;
            var tokens = passportNumber.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens[^1];
        }
    }
}
