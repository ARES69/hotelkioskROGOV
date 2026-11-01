#pragma warning disable CS0618
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.Infrastructure.Health
{
    /// <summary>
    /// Фоновый монитор: если VisionLine завис — убивает процесс и перезапускает службу.
    /// </summary>
    public class VisionLineHealthMonitor : BackgroundService
    {
        private readonly IVisionLineService _visionLine;
        private readonly ILogger<VisionLineHealthMonitor> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(15);
        private int _failCount;

        public VisionLineHealthMonitor(IVisionLineService visionLine, ILogger<VisionLineHealthMonitor> logger)
        {
            _visionLine = visionLine;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var ok = await _visionLine.PingAsync(stoppingToken);
                    if (ok)
                    {
                        _failCount = 0;
                    }
                    else
                    {
                        _failCount++;
                        _logger.LogWarning("VisionLine не отвечает. Счётчик сбоев: {Count}", _failCount);

                        if (_failCount >= 3)
                        {
                            _logger.LogError("VisionLine завис. Принудительный перезапуск.");
                            RestartVisionLineProcess();
                            _failCount = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка монитора VisionLine");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private void RestartVisionLineProcess()
        {
            try
            {
                // Находим процесс VisionLine Encoder и убиваем
                foreach (var proc in Process.GetProcessesByName("VisionLine"))
                {
                    _logger.LogWarning("Убиваем процесс VisionLine PID={Pid}", proc.Id);
                    proc.Kill();
                }

                // Перезапуск службы Windows
                var psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = "start VisionLineEncoderService",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось перезапустить VisionLine");
            }
        }
    }
}
