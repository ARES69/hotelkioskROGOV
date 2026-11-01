using System.Threading;
using System.Threading.Tasks;
using HotelKiosk.Core.Models;

namespace HotelKiosk.Core.Ports
{
    public interface IRegulaService
    {
        /// <summary>
        /// Захват документа физическим считывателем (Regula Document Reader).
        /// Требует настроенного Regula:AgentScanPath.
        /// </summary>
        Task<Result<PassportData>> ScanPassportAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Распознавание паспорта по готовому кадру, снятому камерой киоска.
        ///
        /// Нужен для схемы «фронтальная камера»: гость с паспортом в руках
        /// попадает в рамку, WebView2 отдаёт JPEG в base64, а этот метод
        /// отправляет его в тот же Regula /process. Агент-считыватель не
        /// участвует, поэтому Regula:AgentScanPath может быть пустым.
        ///
        /// imageBase64 — JPEG/PNG без data:-префикса.
        /// </summary>
        Task<Result<PassportData>> RecognizeFromPhotoAsync(
            string imageBase64, CancellationToken cancellationToken);

        Task<bool> PingAsync(CancellationToken cancellationToken);
    }
}
