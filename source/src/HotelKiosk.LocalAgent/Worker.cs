using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelKiosk.LocalAgent
{
    /// <summary>
    /// Фоновый агент: мониторинг здоровья локальных сервисов (Regula).
    /// Живость VisionLine киоск проверяет сам (запись карт идёт напрямую
    /// через его REST-сервер); при необходимости включается
    /// VisionLine:HealthMonitorEnabled — тогда монитор работает и здесь.
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly IRegulaService _regula;
        private readonly ILogger<Worker> _logger;

        public Worker(IRegulaService regula, ILogger<Worker> logger)
        {
            _regula = regula;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LocalAgent запущен");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var r = await _regula.PingAsync(stoppingToken);
                    _logger.LogDebug("Health: Regula={R}", r);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Ошибка health-check Regula");
                }

                await Task.Delay(10_000, stoppingToken);
            }
        }
    }
}
