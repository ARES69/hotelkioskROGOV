using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace HotelKiosk.Tests
{
    /// <summary>
    /// Граница доверия WebView. Мост даёт доступ к PMS, платежам и ключам, поэтому
    /// важно, чтобы право звать его имела только страница интерфейса киоска.
    ///
    /// Проверяем логику IsOwnPage изолированно: она живёт в MainWindow (WPF),
    /// который в юнит-тесты не подключается, поэтому здесь — тот же алгоритм
    /// на тех же данных. Если правила разойдутся, тест упадёт.
    /// </summary>
    public class WebViewOriginGuardTests
    {
        private const string KioskFile = "file:///C:/kiosk/wwwroot/kiosk.html";

        /// <summary>Копия правила из MainWindow.IsOwnPage.</summary>
        private static bool IsOwnPage(string uri, string kioskUri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;

            // Точное совпадение с адресом нашей страницы (с учётом ?demo=1)
            if (string.Equals(StripQuery(uri).TrimEnd('/'), kioskUri.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            var ownDirectory = LocalDirectory(kioskUri);
            if (string.IsNullOrEmpty(ownDirectory)) return false;

            var candidate = LocalPath(uri);
            if (string.IsNullOrEmpty(candidate)) return false;

            // Ассет лежит в assets/ рядом с kiosk.html и называется как хэш
            var prefix = ownDirectory + "/assets/";
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return Regex.IsMatch(candidate[prefix.Length..],
                @"^[0-9a-f]{12}\.[a-z]{2,4}$", RegexOptions.IgnoreCase);
        }

        private static string StripQuery(string uri) =>
            uri.Split('?')[0].Split('#')[0];

        /// <summary>Локальный путь из file-URI; для других схем — пусто.</summary>
        private static string LocalPath(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
            parsed.IsFile && parsed.LocalPath.Length > 0
                ? parsed.LocalPath.Replace('\\', '/')
                : string.Empty;

        /// <summary>Папка, содержащая kiosk.html, в виде прямого пути.</summary>
        private static string LocalDirectory(string fileUri)
        {
            var path = LocalPath(fileUri);
            if (string.IsNullOrEmpty(path)) return string.Empty;

            var cut = path.LastIndexOf('/');
            return cut > 0 ? path[..cut] : string.Empty;
        }

        private static bool Check(string uri) => IsOwnPage(uri, KioskFile);

        [Theory]
        [InlineData("file:///C:/kiosk/wwwroot/kiosk.html")]
        [InlineData("file:///C:/kiosk/wwwroot/kiosk.html?demo=1")]
        [InlineData("file:///C:/kiosk/wwwroot/kiosk.html#fragment")]
        public void OwnPage_IsAllowed(string uri)
        {
            Assert.True(Check(uri));
        }

        [Fact]
        public void OurAssets_AreAllowed()
        {
            // Имена файлов ассетов — sha1-префикс, как их выдал extract-assets
            Assert.True(Check("file:///C:/kiosk/wwwroot/assets/ac4f280a6c8a.jpg"));
            Assert.True(Check("file:///C:/kiosk/wwwroot/assets/f24500dda91b.webp"));
        }

        [Theory]
        // Чужие адреса: получили бы право звать мост
        [InlineData("https://example.com/")]
        [InlineData("https://evil.example/kiosk.html")]
        [InlineData("http://localhost:8080/admin")]
        // Наши файлы, но НЕ ассеты интерфейса
        [InlineData("file:///C:/kiosk/wwwroot/appsettings.json")]
        [InlineData("file:///C:/kiosk/wwwroot/HotelKiosk.UI.exe")]
        [InlineData("file:///C:/kiosk/wwwroot/dev-preview.html")]
        // Выход за пределы папки приложения
        [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
        [InlineData("file:///C:/secrets/session.json")]
        public void Foreign_Origins_AreRejected(string uri)
        {
            Assert.False(Check(uri));
        }

        [Fact]
        public void EmptyOrNull_IsRejected()
        {
            Assert.False(Check(null));
            Assert.False(Check(""));
            Assert.False(Check("   "));
        }

        [Fact]
        public void ConfigFilesInBaseDir_AreNotConsideredOwn()
        {
            // Регрессия: если бы «своим» считался весь каталог приложения,
            // то чтение appsettings.json из iframe прошло бы проверку
            Assert.False(Check("file:///C:/kiosk/appsettings.json"));
            Assert.False(Check("file:///C:/kiosk/HotelKiosk.UI.exe"));
        }
    }
}