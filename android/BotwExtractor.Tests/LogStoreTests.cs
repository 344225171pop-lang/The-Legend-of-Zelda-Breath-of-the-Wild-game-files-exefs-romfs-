using System;
using System.Linq;
using System.Threading.Tasks;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    public class LogStoreTests
    {
        static LogEntry At(DateTime t, string msg) =>
            new LogEntry { Timestamp = t, Level = "INFO", Message = msg };

        [Fact]
        public void Add_IncrementsCount()
        {
            var s = new LogStore();
            s.Add("INFO", "a");
            s.Add("WARN", "b");
            Assert.Equal(2, s.Count);
        }

        [Fact]
        public void Capacity_TrimsOldest()
        {
            var s = new LogStore(3);
            for (int i = 0; i < 5; i++) s.Add("INFO", "m" + i);
            Assert.Equal(3, s.Count);
            var snap = s.Snapshot();
            Assert.Equal("m2", snap[0].Message);
            Assert.Equal("m4", snap[2].Message);
        }

        [Fact]
        public void Filter_InclusiveBoundaries()
        {
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            var s = new LogStore();
            s.Add(At(t0.AddMinutes(-1), "before"));
            s.Add(At(t0, "start"));
            s.Add(At(t0.AddMinutes(30), "mid"));
            s.Add(At(t0.AddHours(1), "end"));
            s.Add(At(t0.AddHours(1).AddSeconds(1), "after"));
            var got = s.Filter(t0, t0.AddHours(1));
            Assert.Equal(3, got.Count);
            Assert.DoesNotContain(got, e => e.Message == "before");
            Assert.DoesNotContain(got, e => e.Message == "after");
        }

        [Fact]
        public void Filter_EmptyRange_ReturnsEmpty()
        {
            var s = new LogStore();
            s.Add("INFO", "x");
            Assert.Empty(s.Filter(DateTime.MinValue, DateTime.MinValue));
        }

        [Fact]
        public void Resolve_Presets()
        {
            var now = new DateTime(2026, 11, 23, 12, 0, 0);
            Assert.Equal((DateTime.MinValue, now), LogRange.Resolve(LogRangePreset.All, now));
            Assert.Equal((now.AddHours(-1), now), LogRange.Resolve(LogRangePreset.LastHour, now));
            Assert.Equal((now.AddDays(-1), now), LogRange.Resolve(LogRangePreset.Last24Hours, now));
        }

        [Fact]
        public void Format_ContainsHeaderAndAllLines()
        {
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            var entries = new[] { At(t0, "hello"), At(t0.AddSeconds(1), "world") };
            var ctx = new LogContext
            {
                AppVersion = "1.4",
                Device = "Test Device",
                AndroidVersion = "13 (API 33)",
                ThemeMode = "深色",
                Palette = "默认",
                RangeDescription = "全部日志",
                RangeFrom = DateTime.MinValue,
                RangeTo = t0.AddSeconds(1),
            };
            string text = LogExporter.Format(entries, ctx);
            Assert.Contains("应用版本: 1.4", text);
            Assert.Contains("设备: Test Device", text);
            Assert.Contains("主题模式: 深色", text);
            Assert.Contains("日志条数: 2", text);
            Assert.Contains("[INFO] hello", text);
            Assert.Contains("[INFO] world", text);
            Assert.Contains("[2026-11-23 12:00:00.000]", text);
        }

        [Fact]
        public void Format_Empty_StillHasHeader()
        {
            string text = LogExporter.Format(Array.Empty<LogEntry>(), new LogContext());
            Assert.Contains("日志条数: 0", text);
            Assert.Contains("==== BOTW Extractor 运行日志 ====", text);
        }

        [Fact]
        public void SuggestedFileName_EndsWithLog()
        {
            string name = LogExporter.SuggestedFileName(new DateTime(2026, 11, 23, 12, 0, 0));
            Assert.EndsWith(".log", name);
            Assert.StartsWith("botw_log_", name);
        }

        [Fact]
        public void ConcurrentAdds_AreSafe()
        {
            var s = new LogStore(10000);
            Parallel.For(0, 1000, i => s.Add("INFO", "m" + i));
            Assert.Equal(1000, s.Count);
        }
    }
}
