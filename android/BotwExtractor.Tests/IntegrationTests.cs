using System;
using System.IO;
using System.Linq;
using System.Text;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    // End-to-end flows that combine the pure-logic pieces the same way the
    // activities do, covering normal / boundary / error paths and cross-feature
    // interactions (theme + dark mode + log export + cache clean).
    public class IntegrationTests : IDisposable
    {
        readonly string _root;
        public IntegrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "botw_int_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
            AppState.IsExtracting = false;
        }

        [Fact]
        public void ThemeFlow_CustomPalette_StaysAccessible_InDarkMode()
        {
            var store = new FakeStore();
            var prefs = new ThemePrefs(store);
            prefs.SetNightMode(NightMode.Dark);
            Assert.Equal(2, NightMode.ToAppCompatMode(prefs.GetNightMode()));

            // User picks a dark primary; on the dark surface it must be lifted for
            // header text, and text on the primary button must remain readable.
            prefs.SetPalette(new ThemePalette
            {
                Primary = ColorMath.ParseHex("#00333A").Value,
                Secondary = ColorMath.ParseHex("#22333A").Value,
                Tertiary = ColorMath.ParseHex("#102028").Value,
            });
            var pal = prefs.GetPalette();
            int darkSurface = ColorMath.ParseHex("#0E1515").Value;
            int header = ColorMath.EnsureContrast(pal.Primary, darkSurface, 4.5);
            Assert.True(ColorMath.ContrastRatio(header, darkSurface) >= 4.5);
            Assert.True(ColorMath.MeetsWcagAA(ColorMath.OnColor(pal.Primary), pal.Primary));

            // Mode + palette persist across a "restart" (new instance, same store).
            var reborn = new ThemePrefs(store);
            Assert.Equal(NightMode.Dark, reborn.GetNightMode());
            Assert.True(reborn.HasCustomPalette);

            // Reset returns to defaults without touching the night mode.
            reborn.ResetPalette();
            Assert.False(reborn.HasCustomPalette);
            Assert.Equal(NightMode.Dark, reborn.GetNightMode());
        }

        [Fact]
        public void LogExportFlow_RangePreviewWriteRead_Complete()
        {
            var store = new LogStore();
            var now = new DateTime(2026, 11, 23, 12, 0, 0);
            store.Add(new LogEntry { Timestamp = now.AddHours(-2), Level = "INFO", Message = "old-event" });
            store.Add(new LogEntry { Timestamp = now.AddMinutes(-30), Level = "INFO", Message = "recent-event" });
            store.Add(new LogEntry { Timestamp = now, Level = "WARN", Message = "now-event" });

            var (from, to) = LogRange.Resolve(LogRangePreset.LastHour, now);
            var entries = store.Filter(from, to);
            Assert.Equal(2, entries.Count);

            var ctx = new LogContext
            {
                AppVersion = "1.4",
                Device = "Pixel",
                AndroidVersion = "14 (API 34)",
                ThemeMode = "跟随系统",
                Palette = "默认",
                RangeDescription = LogRange.Describe(LogRangePreset.LastHour),
                RangeFrom = from,
                RangeTo = to,
                ExportedAt = now,
            };
            string content = LogExporter.Format(entries, ctx);
            string path = Path.Combine(_root, LogExporter.SuggestedFileName(now));
            File.WriteAllText(path, content, new UTF8Encoding(false));

            string readBack = File.ReadAllText(path);
            Assert.Equal(content, readBack);
            Assert.Contains("recent-event", readBack);
            Assert.Contains("now-event", readBack);
            Assert.DoesNotContain("old-event", readBack);
            Assert.Contains("日志条数: 2", readBack);
        }

        [Fact]
        public void CacheCleanFlow_ProtectsRunningJob_ThenReleases()
        {
            string temp = Path.Combine(_root, "temp");
            string cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(temp, "job.bin"), new byte[400]);
            File.WriteAllBytes(Path.Combine(cache, "c.bin"), new byte[600]);

            CacheCandidate[] Candidates() => new[]
            {
                new CacheCandidate { Key = "temp", Label = "临时", Path = temp, Protected = AppState.IsExtracting },
                new CacheCandidate { Key = "cache", Label = "缓存", Path = cache },
            };

            // Job running: temp protected.
            AppState.IsExtracting = true;
            var items = CacheScanner.Scan(Candidates());
            var res = CacheScanner.Clean(items);
            Assert.Equal(600, res.FreedBytes);
            Assert.Equal(400, CacheScanner.DirectorySize(temp)); // untouched

            // Job finished: temp becomes cleanable.
            AppState.IsExtracting = false;
            var items2 = CacheScanner.Scan(Candidates());
            var tempItem = items2.First(i => i.Key == "temp");
            Assert.True(tempItem.Cleanable);
            var res2 = CacheScanner.Clean(items2);
            Assert.Equal(400, res2.FreedBytes);
            Assert.Equal(0, CacheScanner.DirectorySize(temp));
            Assert.True(Directory.Exists(temp)); // root kept
        }

        [Fact]
        public void CrossFeature_CacheClean_DoesNotAffectLogs_OrTheme()
        {
            var store = new FakeStore();
            var prefs = new ThemePrefs(store);
            prefs.SetNightMode(NightMode.Dark);
            prefs.SetPalette(ThemePrefs.DefaultPalette());

            AppState.Logs.Clear();
            AppState.Logs.Add("INFO", "before-clean");

            string cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "x.bin"), new byte[100]);
            var items = CacheScanner.Scan(new[] { new CacheCandidate { Key = "c", Label = "缓存", Path = cache } });
            CacheScanner.Clean(items);

            // Logs survive the clean and remain exportable.
            AppState.Logs.Add("INFO", "after-clean");
            var all = AppState.Logs.Filter(DateTime.MinValue, DateTime.MaxValue);
            Assert.Contains(all, e => e.Message == "before-clean");
            Assert.Contains(all, e => e.Message == "after-clean");
            string export = LogExporter.Format(all, new LogContext());
            Assert.Contains("before-clean", export);

            // Theme settings untouched by cache cleaning.
            Assert.Equal(NightMode.Dark, prefs.GetNightMode());
            Assert.True(prefs.HasCustomPalette);
        }
    }
}
