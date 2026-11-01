using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Infrastructure.Persistence
{
    /// <summary>
    /// Простой файловый чекпоинт: пишет текущий CheckInContext в JSON рядом
    /// с приложением при каждом переходе состояния. При старте UI проверяет
    /// файл — если он есть, значит прошлый процесс не дошёл до конца
    /// (упал/завис/выключили ПК), и стойка должна показать это персоналу,
    /// а не тихо начать новую сессию поверх незавершённой.
    ///
    /// Запись атомарная (пишем во временный файл, потом заменяем) — так
    /// падение процесса ровно в момент записи не оставит битый JSON.
    /// </summary>
    public class FileCheckInSessionStore : ICheckInSessionStore
    {
        private readonly string _filePath;
        private readonly ILogger<FileCheckInSessionStore> _logger;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        public FileCheckInSessionStore(ILogger<FileCheckInSessionStore> logger, string filePath = null)
        {
            _logger = logger;
            _filePath = filePath ?? DefaultPath();
        }

        /// <summary>
        /// Путь чекпоинта по умолчанию.
        ///
        /// Раньше файл лежал в ProgramData — общей папке, доступной на чтение
        /// всем пользователям Windows. Там он содержал номер паспорта, ФИО и дату
        /// рождения гостя, то есть персональные данные, открытые любому, кто
        /// зашёл на киоск-ПК.
        ///
        /// Теперь путь внутри папки приложения (%LocalAppData%): она приватная
        /// для учётной записи, под которой запущен киоск, и удаляется вместе с
        /// профилем. Персональные данные не должны переживать сессию гостя.
        /// </summary>
        public static string DefaultPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotelKiosk", "session.json");

        /// <summary>
        /// Перенос чекпоинта из прежнего расположения: старый файл в ProgramData
        /// мог остаться после обновления и содержать данные гостя. Если нового
        /// файла нет, а старый есть — читаем его (чтобы не потерять незавершённую
        /// оплату) и удаляем.
        /// </summary>
        private static string LegacyPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HotelKiosk", "session.json");

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(dir)) return;

            Directory.CreateDirectory(dir);

            // Приватность: каталог и файл доступны только текущему пользователю.
            // На Windows без этого любой локальный процесс прочитает паспорт гостя.
            try
            {
                if (!OperatingSystem.IsWindows()) return;

                var info = new DirectoryInfo(dir);
                var acl = info.GetAccessControl();
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                var rule = new System.Security.AccessControl.FileSystemAccessRule(
                    identity,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow);
                acl.SetAccessRule(rule);
                info.SetAccessControl(acl);
            }
            catch (Exception ex)
            {
                // На NTFS без поддержки ACL (например, сетевая папка) — не критично:
                // %LocalAppData% и так приватен для пользователя по умолчанию
                _logger.LogDebug(ex, "Не удалось выставить ACL на {Dir}", dir);
            }
        }

        public async Task SaveAsync(CheckInContext ctx, CancellationToken ct)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                EnsureDirectory();

                var tmpPath = _filePath + ".tmp";
                var json = JsonSerializer.Serialize(ctx, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tmpPath, json, ct);

                // Атомарная замена: либо старый файл, либо новый — никогда не
                // получим наполовину записанный JSON, даже если процесс упадёт
                // прямо здесь.
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // Сбой чекпоинта не должен ронять само заселение — гость важнее лога.
                _logger.LogError(ex, "Не удалось сохранить чекпоинт сессии в {Path}", _filePath);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async Task<CheckInContext> LoadAsync(CancellationToken ct)
        {
            // Файл из старой сборки мог остаться в ProgramData и содержать данные
            // гостя. Подхватываем его, чтобы не потерять незавершённую оплату,
            // и сразу удаляем — дальше персональные данные не лежат в общей папке.
            var path = File.Exists(_filePath) ? _filePath : TryMigrateLegacy();

            if (!File.Exists(path)) return null;

            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                return JsonSerializer.Deserialize<CheckInContext>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось прочитать чекпоинт сессии {Path} — считаем повреждённым", path);
                return null;
            }
        }

        /// <summary>
        /// Возвращает путь к legacy-чекпоинту, если он есть, удаляя сам файл.
        /// Возвращает null, если переносить нечего.
        /// </summary>
        private string TryMigrateLegacy()
        {
            var legacy = LegacyPath();
            if (string.IsNullOrEmpty(legacy) ||
                string.Equals(legacy, _filePath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(legacy))
                return null;

            try
            {
                // Содержимое не переносим: персонал увидит его в диалоге
                // незавершённой сессии при следующем запуске, а данные гостя
                // из общей папки исчезают.
                _logger.LogWarning(
                    "Обнаружен чекпоинт прошлой версии в {Legacy}. " +
                    "Файл будет прочитан один раз и удалён — незавершённую оплату разберёт персонал.",
                    legacy);
                return legacy;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось обработать legacy-чекпоинт {Legacy}", legacy);
                return null;
            }
        }

        public async Task ClearAsync(CancellationToken ct)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);

                // Заодно убираем возможные остатки прежнего расположения —
                // в них тоже лежали персональные данные гостя
                var legacy = LegacyPath();
                if (!string.IsNullOrEmpty(legacy) &&
                    !string.Equals(legacy, _filePath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(legacy))
                {
                    File.Delete(legacy);
                    _logger.LogInformation("Удалён legacy-чекпоинт {Legacy}", legacy);
                }

                // Временный файл мог остаться после падения на записи
                var tmp = _filePath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить чекпоинт {Path}", _filePath);
            }
            finally
            {
                _mutex.Release();
            }
        }
    }
}
