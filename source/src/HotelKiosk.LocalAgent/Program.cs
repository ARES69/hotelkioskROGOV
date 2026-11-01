using System;
using HotelKiosk.Infrastructure;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace HotelKiosk.LocalAgent
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Секция Serilog в appsettings.json раньше не читалась: путь был
            // захардкожен как logs/agent-.log, хотя в конфиге значилось
            // logs/localagent-.log. Теперь конфиг — источник истины.
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{Environment.MachineName}.json", optional: true)
                .Build();

            // Путь лога заякорен на папку приложения: агент ставится службой
            // Windows, и рабочий каталог у службы — System32
            Log.Logger = KioskLogging
                .Create(config, AppContext.BaseDirectory, "logs/localagent-.log")
                .CreateLogger();

            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .UseWindowsService()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddHotelKiosk(ctx.Configuration);
                    services.AddHostedService<Worker>();
                });
    }
}
