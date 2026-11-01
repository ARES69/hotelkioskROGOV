using System;
using System.IO;
using System.Text;
using System.Text.Json;
using HotelKiosk.Core.Models;
using HotelKiosk.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Infrastructure.Health
{
    /// <summary>
    /// Хранение чекпоинта незавершённой сессии.
    ///
    /// Задача — поймать противоречие: чекпоинт содержит персональные данные
    /// гостя (паспорт, ФИО, дата рождения), но он же единственный след оплаты,
    /// за которую гость не получил заселение. Раньше файл просто стирался через
    /// час — персонал не узнавал, что нужно вернуть деньги, а след исчезал.
    ///
    /// Поэтому схема двухступенчатая:
    ///
    ///   часа  →  ПДн стёрты, след для возврата живёт
    ///   сутки →  след удалён
    ///
    /// Что хранится после обезличивания: номер брони, номер транзакции, сумма,
    /// признак «требуется компенсация» и шаг, на котором остановились.
    /// Гостя в этих данных уже нет.
    /// </summary>
    public static class CheckpointRetention
    {
        /// <summary>Через сколько брошенный чекпоинт обезличивается.</summary>
        public static readonly TimeSpan AnonymizeAfter = TimeSpan.FromHours(1);

        /// <summary>Сколько лежит обезличенный след до удаления.</summary>
        public static readonly TimeSpan TraceRetention = TimeSpan.FromDays(1);

        /// <summary>
        /// Чекпоинты, которые нужно обслуживать: текущее расположение и
        /// прежнее (ProgramData — там ПДн в общей папке быть не должны).
        /// </summary>
        public static string[] DefaultPaths
        {
            get
            {
                return new[]
                {
                    FileCheckInSessionStore.DefaultPath(),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "HotelKiosk", "session.json")
                };
            }
        }

        /// <summary>
        /// Обслуживает один файл чекпоинта. Активные сессии не трогает: файл
        /// обновляется на каждом шаге, поэтому «старый» означает брошенный.
        /// </summary>
        public static void Process(string file, ILogger logger)
        {
            if (!File.Exists(file)) return;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            if (age <= AnonymizeAfter) return;

            var ctx = Read(file);

            if (ctx == null)
            {
                // Битый чекпоинт разобрать нечем — удаляем как мусор
                File.Delete(file);
                logger.LogWarning("Удалён нечитаемый чекпоинт {File}", file);
                return;
            }

            if (ctx.Anonymized)
            {
                var sinceAnonymized = ctx.AnonymizedAtUtc.HasValue
                    ? DateTime.UtcNow - ctx.AnonymizedAtUtc.Value
                    : age;

                if (sinceAnonymized <= TraceRetention) return;

                File.Delete(file);
                logger.LogInformation(
                    "Удалён обезличенный след сессии {File} (лежал {Age})", file, sinceAnonymized);
                return;
            }

            Anonymize(file, ctx, age, logger);
        }

        private static CheckInContext Read(string file)
        {
            try
            {
                return JsonSerializer.Deserialize<CheckInContext>(File.ReadAllText(file));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Стирает ПДн, сохраняя след для возврата. Проверяем именно файл, а не
        /// объект: важно, чтобы данные физически не остались на диске.
        /// </summary>
        private static void Anonymize(string file, CheckInContext ctx, TimeSpan age, ILogger logger)
        {
            var trace = new CheckInContext
            {
                // Финансовый след — то, без чего персонал не сделает возврат
                ReservationId = ctx.ReservationId,
                PaymentTransactionId = ctx.PaymentTransactionId,
                PaidAmount = ctx.PaidAmount,
                RequiresCompensation = ctx.RequiresCompensation,
                RequiredCompensation = ctx.RequiredCompensation,
                CurrentState = ctx.CurrentState,
                LastError = "Персональные данные удалены по истечении срока хранения",

                // ПДн обнуляем
                GuestName = null,
                PassportNumber = null,
                ScannedPassport = null,
                GuestId = null,

                Anonymized = true,
                AnonymizedAtUtc = DateTime.UtcNow
            };

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(trace,
                new JsonSerializerOptions { WriteIndented = true }));

            // FileMode.Create перетирает прежнее содержимое: след ПДн на диске
            // физически заменяется, а не исчезает вместе с файлом.
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (trace.RequiresCompensation && !string.IsNullOrEmpty(trace.PaymentTransactionId))
                logger.LogWarning(
                    "Чекпоинт обезличен, но требуется возврат: бронь {Reservation}, " +
                    "транзакция {TxId}, сумма {Amount} руб. След удаляется через сутки.",
                    trace.ReservationId, trace.PaymentTransactionId, trace.PaidAmount);
            else
                logger.LogInformation(
                    "Чекпоинт обезличен (возраст {Age}): ПДн удалены, след для разбора сохранён",
                    age);
        }
    }
}
