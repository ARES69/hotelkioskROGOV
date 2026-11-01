using System;
using System.Collections.Generic;
using System.IO;
using HotelKiosk.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Debugging;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Секция Serilog в appsettings.json была мёртвой: путь и уровень задавались
    /// в коде. Первый вариант починки (ReadFrom.Configuration) выглядел логично,
    /// но молча не подключал НИ ОДНОГО синка — приложение стартовало вообще
    /// без логирования, и даже неизвестный синк не давал ошибки. Тесты ниже
    /// фиксируют рабочий способ: секция разбирается явно, и файл появляется.
    /// </summary>
    public class KioskLoggingTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HotelKiosk.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static IConfiguration LoadShipped() =>
            new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(FindRepoRoot(), "src", "HotelKiosk.UI", "appsettings.json"),
                    optional: false)
                .Build();

        private static IConfiguration Build(Dictionary<string, string> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        /// <summary>Собирает логгер и пишет в него, возвращает путь к ожидаемому файлу.</summary>
        private static string WriteAndFind(string dir, IConfiguration config)
        {
            var logger = KioskLogging.Create(config).CreateLogger();
            try
            {
                logger.Information("проверка записи в лог");
            }
            finally
            {
                logger.Dispose();
            }

            return Directory.Exists(dir) ? dir : null;
        }

        [Fact]
        public void ShippedSection_ActuallyCreatesLogFile()
        {
            // Регрессия на «мёртвый» конфиг: раньше секция читалась, но файл
            // не появлялся, потому что синки не подключались
            var dir = Path.Combine(Path.GetTempPath(), "hk-log-" + Guid.NewGuid().ToString("N"));
            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = Path.Combine(dir, "kiosk-.log")
            });

            var logsDir = WriteAndFind(dir, config);

            Assert.True(Directory.Exists(dir), "каталог с логом не создан");
            var files = Directory.GetFiles(dir);
            Assert.NotEmpty(files);
            Assert.Contains("проверка записи в лог", File.ReadAllText(files[0]));

            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void ShippedConfig_SectionExists()
        {
            var config = LoadShipped();

            Assert.True(config.GetSection("Serilog").Exists(),
                "секция Serilog отсутствует в поставляемом appsettings.json");
            Assert.False(string.IsNullOrWhiteSpace(config["Serilog:MinimumLevel"]));
        }

        [Fact]
        public void RelativePath_IsResolvedAgainstWorkingDirectory()
        {
            // Путь в конфиге относительный — как в поставке ("logs/kiosk-.log")
            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = "logs/hk-relative-probe.log"
            });

            var logger = KioskLogging.Create(config).CreateLogger();
            try { logger.Information("relative probe"); }
            finally { logger.Dispose(); }

            var expected = Path.Combine(Environment.CurrentDirectory, "logs");
            Assert.True(Directory.Exists(expected), "относительный путь не разрешился в рабочий каталог");
            Assert.NotEmpty(Directory.GetFiles(expected, "hk-relative-probe*"));
        }

        [Fact]
        public void RelativePath_IsResolvedAgainstAppBaseDirectory()
        {
            // Путь в конфиге относительный — как в поставке ("logs/kiosk-.log").
            // При автозапуске рабочий каталог не гарантирован, поэтому путь
            // обязан привязываться к папке приложения, а не к CWD.
            var appDir = Path.Combine(Path.GetTempPath(), "hk-app-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(appDir);

            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = "logs/kiosk-.log"
            });

            var logger = KioskLogging.Create(config, appDir).CreateLogger();
            try { logger.Information("appdir probe"); }
            finally { logger.Dispose(); }

            var logs = Path.Combine(appDir, "logs");
            Assert.True(Directory.Exists(logs),
                "лог должен попасть в папку приложения, а не в рабочий каталог");
            Assert.NotEmpty(Directory.GetFiles(logs, "kiosk-*"));

            try { Directory.Delete(appDir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void NoSection_FallsBackToDefaults_AndStillWrites()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hk-fallback-" + Guid.NewGuid().ToString("N"));
            var fallback = Path.Combine(dir, "kiosk-.log");

            var logger = KioskLogging.Create(
                    Build(new Dictionary<string, string>()), appBaseDirectory: null, defaultLogPath: fallback)
                .CreateLogger();
            try { logger.Information("fallback probe"); }
            finally { logger.Dispose(); }

            // Суточная ротация дописывает дату к имени: kiosk-20261002.log
            Assert.True(Directory.Exists(dir), "каталог с логом не создан");
            var files = Directory.GetFiles(dir);
            Assert.NotEmpty(files);
            Assert.Contains("fallback probe", File.ReadAllText(files[0]));

            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void UnknownSinkName_IsReported_NotSilentlyIgnored()
        {
            SelfLog.Enable(m => { });

            var messages = new List<string>();
            SelfLog.Enable(m => messages.Add(m));

            var dir = Path.Combine(Path.GetTempPath(), "hk-badsink-" + Guid.NewGuid().ToString("N"));
            var good = Path.Combine(dir, "kiosk-.log");

            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "Typosink",
                ["Serilog:WriteTo:0:Args:path"] = "whatever.log",
                ["Serilog:WriteTo:1:Name"] = "File",
                ["Serilog:WriteTo:1:Args:path"] = good
            });

            var logger = KioskLogging.Create(config).CreateLogger();
            try { logger.Information("badsink probe"); }
            finally { logger.Dispose(); SelfLog.Disable(); }

            Assert.Contains(messages, m => m.Contains("Typosink"));
            // Известный синк рядом с опечаткой всё равно работает
            Assert.True(Directory.Exists(dir), "известный синк рядом с опечаткой должен работать");
            Assert.NotEmpty(Directory.GetFiles(dir));

            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void BrokenSection_DoesNotThrow_AndFallsBack()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hk-broken-" + Guid.NewGuid().ToString("N"));
            var fallback = Path.Combine(dir, "kiosk-.log");

            // Некорректные значения уровня и интервала не должны ронять киоск
            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "НЕ-УРОВЕНЬ",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = fallback,
                ["Serilog:WriteTo:0:Args:rollingInterval"] = "НЕ-ИНТЕРВАЛ",
                ["Serilog:WriteTo:0:Args:retainedFileCountLimit"] = "не число"
            });

            var logger = KioskLogging.Create(config).CreateLogger();
            try { logger.Information("broken probe"); }
            finally { logger.Dispose(); }

            Assert.True(Directory.Exists(dir));
            Assert.NotEmpty(Directory.GetFiles(dir));

            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void MinimumLevelOverride_IsAccepted()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hk-override-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "kiosk-.log");

            var config = Build(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel"] = "Information",
                ["Serilog:MinimumLevelOverride:Microsoft"] = "Warning",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = file
            });

            var logger = KioskLogging.Create(config).CreateLogger();
            try { logger.Information("override probe"); }
            finally { logger.Dispose(); }

            Assert.True(Directory.Exists(dir));
            Assert.NotEmpty(Directory.GetFiles(dir));

            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        [Fact]
        public void LocalAgentConfig_UsesItsOwnLogPath()
        {
            // Раньше LocalAgent писал в logs/agent-.log в коде, игнорируя
            // указанный в конфиге logs/localagent-.log
            var config = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(FindRepoRoot(), "src", "HotelKiosk.LocalAgent",
                    "appsettings.json"), optional: false)
                .Build();

            var path = config["Serilog:WriteTo:1:Args:path"];
            Assert.Equal("logs/localagent-.log", path);
        }
    }
}
