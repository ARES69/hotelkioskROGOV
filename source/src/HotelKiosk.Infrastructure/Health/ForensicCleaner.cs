using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Infrastructure.Health
{
    /// <summary>
    /// Фоновый «судебный чистильщик»: удаляет временные файлы Regula и драйвера
    /// Альфа-Банка, содержащие паспортные данные и логи терминала, и обезличивает
    /// устаревший чекпоинт сессии (см. CheckpointRetention).
    /// </summary>
    public class ForensicCleaner : BackgroundService
    {
        private readonly ILogger<ForensicCleaner> _logger;

        private readonly string[] _targetPaths = new[]
        {
            Path.Combine(Path.GetTempPath(), "Regula"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Regula"),
            @"C:\Program Files\AlfaBank\Terminal\logs",
            Path.Combine(Path.GetTempPath(), "AlfaBankTerminal")
        };

        private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

        /// <summary>Временные файлы Regula и драйверов Альфа-Банка — пять минут.</summary>
        private readonly TimeSpan _directoryMaxAge = TimeSpan.FromMinutes(5);

        public ForensicCleaner(ILogger<ForensicCleaner> logger) => _logger = logger;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    CleanCheckpoints();
                    foreach (var path in _targetPaths)
                    {
                        if (!Directory.Exists(path)) continue;
                        CleanDirectory(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка ForensicCleaner");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private void CleanCheckpoints()
        {
            foreach (var file in CheckpointRetention.DefaultPaths)
            {
                try
                {
                    CheckpointRetention.Process(file, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось обслужить чекпоинт {File}", file);
                }
            }
        }

        private void CleanDirectory(string path)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                // Недоступная подпапка не должна обрывать очистку остальных
                _logger.LogDebug(ex, "Не удалось обойти каталог {Path}", path);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (DateTime.UtcNow - fi.LastWriteTimeUtc > _directoryMaxAge)
                    {
                        SecureDelete(file);
                        _logger.LogInformation("Удалён файл: {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось удалить {File}", file);
                }
            }
        }

        /// <summary>
        /// Безопасное удаление: перезапись нулями перед удалением.
        /// </summary>
        private void SecureDelete(string filePath)
        {
            try
            {
                var length = new FileInfo(filePath).Length;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var zeros = new byte[Math.Min(4096, length)];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(zeros.Length, remaining);
                        fs.Write(zeros, 0, toWrite);
                        remaining -= toWrite;
                    }
                }
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SecureDelete не удался для {File}", filePath);
                try { File.Delete(filePath); } catch { /* ignore */ }
            }
        }
    }
}
