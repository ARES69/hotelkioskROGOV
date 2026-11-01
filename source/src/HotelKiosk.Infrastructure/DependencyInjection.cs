using System;
using System.Net;
using System.Net.Http;
using HotelKiosk.Application.Options;
using HotelKiosk.Application.Services;
using HotelKiosk.Core.Ports;
using HotelKiosk.Core.Ports.Pms;
using HotelKiosk.Infrastructure.Adapters.AlfaBank;
using HotelKiosk.Infrastructure.Adapters.CardTerminal;
using HotelKiosk.Infrastructure.Adapters.GenericPms;
using HotelKiosk.Infrastructure.Adapters.Logus;
using HotelKiosk.Infrastructure.Adapters.Regula;
using HotelKiosk.Infrastructure.Adapters.VisionLine;
using HotelKiosk.Infrastructure.Health;
using HotelKiosk.Infrastructure.Notifications;
using HotelKiosk.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace HotelKiosk.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddHotelKiosk(
            this IServiceCollection services, IConfiguration configuration)
        {
            // Options
            services.Configure<CheckInStateMachineOptions>(
                configuration.GetSection(CheckInStateMachineOptions.SectionName));
            services.Configure<LogusApiOptions>(
                configuration.GetSection(LogusApiOptions.SectionName));
            services.Configure<RegulaOptions>(
                configuration.GetSection(RegulaOptions.SectionName));
            services.Configure<QrPaymentOptions>(
                configuration.GetSection(QrPaymentOptions.SectionName));
            services.Configure<VisionLineOptions>(
                configuration.GetSection(VisionLineOptions.SectionName));
            services.Configure<TelegramOptions>(
                configuration.GetSection(TelegramOptions.SectionName));
            services.Configure<CardTerminalOptions>(
                configuration.GetSection(CardTerminalOptions.SectionName));
            services.Configure<GenericPmsOptions>(
                configuration.GetSection(GenericPmsOptions.SectionName));

            // Выбор PMS: боевой адаптер Logus либо универсальный HTTP/JSON.
            // Киоск зависит только от IPmsProvider, поэтому подключение другой
            // PMS — правка секции Pms в конфиге, а не сборка.
            var pms = PmsSelection.FromConfiguration(configuration);

            // Неизвестный код PMS не должен молча уводить киоск в Logus:
            // оператор увидел бы «бронь не найдена» и не понял бы почему.
            // Валидатор конфигурации покажет это как блокер при старте.
            if (!pms.Provider.Equals(PmsProviders.Logus, StringComparison.OrdinalIgnoreCase) &&
                !pms.Provider.Equals(PmsProviders.Generic, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Pms:Provider=\"{pms.Provider}\" не поддерживается. " +
                    $"Ожидается: {string.Join(" или ", PmsProviders.Known)}");

            services.AddSingleton(pms);

            // HttpClients
            // LogusApi — настраиваем Handler с учётом прокси из конфига.
            // По умолчанию прокси отключён (UseProxy = false), т.к.
            // в Windows настройки WinHTTP (.NET) и WinINET (браузер)
            // могут различаться, и автоматический прокси часто не работает.
            // Если нужен прокси — включите в appsettings.json:
            //   "LogusApi": { "UseProxy": true, "ProxyUrl": "http://proxy:8080" }
            services.AddHttpClient<IRegulaService, RegulaDocumentReaderService>();
            services.AddHttpClient<IAlfaBankService, AlfaBankPaymentService>();
            services.AddHttpClient<IVisionLineService, VisionLineService>();
            services.AddHttpClient<INotificationService, TelegramNotifier>();
            services.AddHttpClient<ICardTerminalService, CardTerminalService>();

            // PMS. IPmsProvider — единственный контракт, который знает киоск.
            //
            // ILogusService (Logus-специфичный порт) указывает на тот же
            // экземпляр: в нём остались два метода, помеченные Obsolete, ради
            // которых сам интерфейс ещё жив.
            if (pms.IsGeneric)
            {
                services.AddHttpClient<IPmsProvider, GenericPmsService>();
                services.AddSingleton<ILogusService>(
                    sp => (ILogusService)sp.GetRequiredService<IPmsProvider>());
            }
            else
            {
                services.AddHttpClient<ILogusService, LogusApiService>()
                    .ConfigurePrimaryHttpMessageHandler(sp =>
                    {
                        var opts = sp.GetRequiredService<IOptions<LogusApiOptions>>().Value;
                        var handler = new SocketsHttpHandler
                        {
                            UseProxy = opts.UseProxy,
                            ConnectTimeout = TimeSpan.FromSeconds(10),
                            AllowAutoRedirect = false,
                        };

                        if (opts.UseProxy)
                        {
                            if (!string.IsNullOrEmpty(opts.ProxyUrl))
                                handler.Proxy = new WebProxy(opts.ProxyUrl);
                            else
                                handler.Proxy = WebRequest.GetSystemWebProxy();

                            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                        }

                        return handler;
                    });

                services.AddSingleton<IPmsProvider>(
                    sp => sp.GetRequiredService<ILogusService>());
            }

            // Единый источник таймингов оплаты для всех потребителей
            var qrOpts = configuration
                .GetSection(QrPaymentOptions.SectionName)
                .Get<QrPaymentOptions>() ?? new QrPaymentOptions();

            // Источник кадра паспорта: камера или считыватель
            var regulaOpts = configuration
                .GetSection(RegulaOptions.SectionName)
                .Get<RegulaOptions>() ?? new RegulaOptions();
            services.AddSingleton(CaptureSource.Resolve(
                regulaOpts.CaptureSource,
                !string.IsNullOrWhiteSpace(regulaOpts.AgentScanPath)));
            var smOpts = configuration
                .GetSection(CheckInStateMachineOptions.SectionName)
                .Get<CheckInStateMachineOptions>() ?? new CheckInStateMachineOptions();
            var cardOpts = configuration
                .GetSection(CardTerminalOptions.SectionName)
                .Get<CardTerminalOptions>() ?? new CardTerminalOptions();
            services.AddSingleton(PaymentPollingPolicy.FromOptions(
                smOpts, qrOpts.PollIntervalSeconds, qrOpts.QrLifetimeMinutes,
                cardOpts.PollIntervalSeconds));

            // Persistence (crash-safe checkpoint)
            services.AddSingleton<ICheckInSessionStore, FileCheckInSessionStore>();

            // Application.
            //
            // Раньше здесь регистрировались ещё и CheckInStateMachine с
            // KioskBridgeHandler — 1477 строк кода, которые никто не резолвил:
            // MainWindow работает только через KioskActionAdapter. Две
            // неисполняемые реализации одного флоу расходились между собой по
            // правилам оплаты, и это стоило багов. Удалены 2026-10.
            services.AddSingleton<KioskActionAdapter>();

            // Health
            services.AddHostedService<ForensicCleaner>();

            // Монитор живости VisionLine — только если явно включён в конфиге
            // (перезапускает энкодер после 3 неудачных health-check подряд).
            var visionLineOpts = configuration
                .GetSection(VisionLineOptions.SectionName)
                .Get<VisionLineOptions>() ?? new VisionLineOptions();
            if (visionLineOpts.HealthMonitorEnabled)
                services.AddHostedService<VisionLineHealthMonitor>();

            return services;
        }
    }
}
