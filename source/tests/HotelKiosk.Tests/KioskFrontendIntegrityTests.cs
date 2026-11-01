using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Регрессия на размер и целостность фронтенда.
    ///
    /// kiosk.html раньше весил 5.3 МБ: почти всё — инлайновые base64-картинки
    /// прямо в HTML. Браузер обязан был распарсить весь этот текст до первого
    /// кадра, а одинаковые картинки дублировались в файле по несколько раз.
    /// Теперь это отдельные файлы в wwwroot/assets, а HTML — текст.
    /// </summary>
    public class KioskFrontendIntegrityTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HotelKiosk.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            return dir!.FullName;
        }

        private static string WwwRoot() =>
            Path.Combine(RepoRoot(), "src", "HotelKiosk.UI", "wwwroot");

        private static string ReadKioskHtml() =>
            File.ReadAllText(Path.Combine(WwwRoot(), "kiosk.html"));

        [Fact]
        public void KioskHtml_IsReasonableSize()
        {
            // 5.3 МБ → 212 КБ. Порог с запасом: текст интерфейса, не ассеты.
            var size = new FileInfo(Path.Combine(WwwRoot(), "kiosk.html")).Length;

            Assert.True(size < 700 * 1024,
                $"kiosk.html весит {size / 1024.0 / 1024:F2} МБ — похоже, base64 снова встроены");
        }

        [Fact]
        public void KioskHtml_HasNoInlineBase64Images()
        {
            // Исключения — генерируемые в рантайме (QR от банка, превью
            // подписи, синтетический кадр камеры в демо), они не в разметке.
            var html = ReadKioskHtml();

            var inline = Regex.Matches(
                html, @"data:image/[a-z]+;base64,[A-Za-z0-9+/=]{256,}");

            Assert.Empty(inline);
        }

        [Fact]
        public void EveryReferencedAsset_Exists()
        {
            var html = ReadKioskHtml();
            var root = WwwRoot();

            var references = Regex.Matches(html, @"assets/[0-9a-f]{12}\.[a-z]+")
                .Select(m => m.Value)
                .Distinct()
                .ToArray();

            Assert.NotEmpty(references);

            foreach (var reference in references)
            {
                var path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"ассет не найден: {reference}");
                Assert.True(new FileInfo(path).Length > 0, $"ассет пуст: {reference}");
            }
        }

        [Fact]
        public void AssetsDirectory_HasNoOrphans()
        {
            // Забытый файл в assets незаметен: он просто не грузится,
            // но весит и едет в поставку
            var assetsDir = Path.Combine(WwwRoot(), "assets");
            Assert.True(Directory.Exists(assetsDir), "каталог assets отсутствует");

            var onDisk = Directory.GetFiles(assetsDir)
                .Select(Path.GetFileName)
                .ToArray();

            var referenced = Regex.Matches(ReadKioskHtml(), @"assets/[0-9a-f]{12}\.[a-z]+")
                .Select(m => Path.GetFileName(m.Value))
                .Distinct()
                .ToArray();

            var orphans = onDisk.Except(referenced).ToArray();
            Assert.True(orphans.Length == 0,
                $"неиспользуемые файлы в assets: {string.Join(", ", orphans)}");
        }

        [Fact]
        public void AssetContentType_MatchesExtension()
        {
            // Иначе WebView2 откажется отдавать файл или покажет битую картинку
            var assetsDir = Path.Combine(WwwRoot(), "assets");

            foreach (var file in Directory.GetFiles(assetsDir))
            {
                var header = File.ReadAllBytes(file).Take(12).ToArray();
                var extension = Path.GetExtension(file).ToLowerInvariant();

                if (extension == ".jpg" || extension == ".jpeg")
                    Assert.True(header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8,
                        $"{Path.GetFileName(file)} не похож на JPEG");
                else if (extension == ".png")
                    Assert.True(header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50,
                        $"{Path.GetFileName(file)} не похож на PNG");
                else if (extension == ".webp")
                    Assert.True(header.Length >= 12 &&
                        System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" &&
                        System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
                        $"{Path.GetFileName(file)} не похож на WebP");
            }
        }

        [Fact]
        public void KioskHtml_HasArrivalsScreenWired()
        {
            // Экран «сегодня заезжают» был обещан в README, но отсутствовал
            // в UI: ни секции, ни действия в адаптере, ни ключа в i18n.
            // Гость без кода брони не мог найти себя.
            var html = ReadKioskHtml();

            foreach (var hook in new[]
                     {
                         "<section id=\"arrivals\"",
                         "id=\"arrivalsButton\"",
                         "id=\"arrivalsList\"",
                         "async function showArrivals",
                         "window.showArrivals=showArrivals",
                         "function applyArrivalsCapability",
                         "applyArrivalsCapability(result)",
                         "action===\"booking.arrivals\"",
                         "\"booking.arrivals\"",
                         "search:new Set([\"search\",\"arrivals\"",
                         "todaysArrivals:\"Сегодня заезжают\"",
                         "todaysArrivals:\"Arrivals today\""
                     })
            {
                Assert.Contains(hook, html, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void KioskHtml_OnlyCallsActionsThatAdapterImplements()
        {
            // Каждое действие из request(...) должно обрабатываться адаптером,
            // иначе UI получит API_VALIDATION в середине флоу
            var html = ReadKioskHtml();

            var actions = new List<string>();
            foreach (Match match in Regex.Matches(html, @"request\(""([\w.]+)"""))
            {
                var action = match.Groups[1].Value;
                if (!actions.Contains(action)) actions.Add(action);
            }

            Assert.NotEmpty(actions);

            var adapterPath = Path.Combine(RepoRoot(), "src", "HotelKiosk.Application",
                "Services", "KioskActionAdapter.cs");
            var adapter = File.ReadAllText(adapterPath);

            foreach (var action in actions)
            {
                Assert.Contains($"\"{action}\" =>", adapter, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void KioskHtml_PaymentNotConfirmed_IsKnownNonRetryableCode()
        {
            // Новый код от адаптера (оплата не подтверждена перед заселением)
            // должен быть в каталоге ошибок UI, иначе он превратится в
            // API_SERVER_ERROR и гость получит «повторите» вместо «зовите
            // администратора». Ретраить бессмысленно: деньги от повтора не
            // появятся.
            var html = ReadKioskHtml();

            // В списке кодов
            Assert.Matches(@"API_RESPONSE_TOO_LARGE PAYMENT_NOT_CONFIRMED", html);

            // Помечен как небезопасный: без ретрая и с вызовом персонала
            var unsafeBlock = Regex.Match(html,
                @"const UNSAFE_CODES=new Set\(\[(.*?)\]\);", RegexOptions.Singleline);
            Assert.True(unsafeBlock.Success);
            Assert.Contains("\"PAYMENT_NOT_CONFIRMED\"", unsafeBlock.Groups[1].Value);

            // Домен и маршрут: текст показывается на экране документов
            Assert.Contains("PAYMENT:\"PAYMENT\"", html);
            Assert.Contains("PAYMENT:\"document\"", html);
        }

        [Fact]
        public void KioskHtml_GuardsPaidStepsInUi()
        {
            // Шлагбаум оплаты в UI был один только в бэкенде (pms.checkin).
            // В интерфейсе navigate('keys') вызывал registerCheckIn() безусловно,
            // поэтому обход оплаты оставался открыт.
            var html = ReadKioskHtml();

            Assert.Matches(@"paymentConfirmed:false", html);                    // начальное значение
            Assert.Matches(@"engine\.paymentConfirmed=false", html);            // сброс в newSession
            Assert.Matches(@"engine\.paymentConfirmed=due<=0", html);          // выставление после оплаты
            Assert.Matches(@"!engine\.paymentConfirmed\)", html);               // проверка в переходах
            Assert.Matches(@"PAYMENT_NOT_CONFIRMED", html);                     // код отказа
        }

        [Fact]
        public void KioskHtml_ScriptBlocksParse()
        {
            // Вынос картинок не должен был задеть интерфейс: камера,
            // рамка и элементы сканирования на месте.
            var html = ReadKioskHtml();

            foreach (var hook in new[]
                     {
                         "window.KioskCamera",
                         "id=\"cameraVideo\"",
                         "id=\"cameraCanvas\"",
                         "camera-frame-oval",
                         "id=\"cameraCountdown\"",
                         "PAY_POLL_MS",
                         "system.preflight",
                         "scanner.scan",
                         "payment.create",
                         "pms.checkin",
                         "card.write",
                         "pms.finalize"
                     })
            {
                Assert.Contains(hook, html, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void NoDeadFrontendFiles()
        {
            // kiosk-legacy.html и fon.jpg больше не нужны: legacy не
            // подключался, фон не упоминался в разметке.
            var root = WwwRoot();

            Assert.False(File.Exists(Path.Combine(root, "kiosk-legacy.html")),
                "kiosk-legacy.html мёртвый и весит 47 КБ");
            Assert.False(File.Exists(Path.Combine(root, "fon.jpg")),
                "fon.jpg не используется в разметке");
        }
    }
}
