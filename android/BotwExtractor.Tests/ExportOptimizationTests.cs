using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    // Correctness gate for the export performance optimization (v1.6):
    // the optimized Filter/Format/WriteTo must produce results identical to
    // the original v1.5 algorithms, byte for byte.
    public class ExportOptimizationTests
    {
        // ---- Reference copies of the ORIGINAL (pre-optimization) algorithms ----

        static string LegacyFormat(IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            ctx ??= new LogContext();
            entries ??= Array.Empty<LogEntry>();
            var sb = new StringBuilder();
            sb.AppendLine("==== BOTW Extractor 运行日志 ====");
            sb.AppendLine("应用版本: " + (ctx.AppVersion ?? ""));
            sb.AppendLine("设备: " + (ctx.Device ?? ""));
            sb.AppendLine("Android 版本: " + (ctx.AndroidVersion ?? ""));
            sb.AppendLine("主题模式: " + (ctx.ThemeMode ?? ""));
            sb.AppendLine("配色方案: " + (ctx.Palette ?? ""));
            sb.AppendLine("导出时间: " + ctx.ExportedAt.ToString(LogExporter.TimestampFormat));
            sb.AppendLine("日志范围: " + (ctx.RangeDescription ?? "") +
                " (" + ctx.RangeFrom.ToString(LogExporter.TimestampFormat) + " ~ " + ctx.RangeTo.ToString(LogExporter.TimestampFormat) + ")");
            sb.AppendLine("日志条数: " + entries.Count);
            sb.AppendLine("================================");
            foreach (var e in entries)
                sb.AppendLine("[" + e.Timestamp.ToString(LogExporter.TimestampFormat) + "] [" + (e.Level ?? "INFO") + "] " + (e.Message ?? ""));
            return sb.ToString();
        }

        static List<LogEntry> LegacyFilter(List<LogEntry> all, DateTime from, DateTime to) =>
            all.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();

        static LogEntry At(DateTime t, string level, string msg) =>
            new LogEntry { Timestamp = t, Level = level, Message = msg };

        static LogContext SampleContext() => new LogContext
        {
            AppVersion = "1.6",
            Device = "Test Device",
            AndroidVersion = "13 (API 33)",
            ThemeMode = "深色",
            Palette = "默认",
            ExportedAt = new DateTime(2026, 11, 23, 12, 0, 0),
            RangeDescription = "全部日志",
            RangeFrom = DateTime.MinValue,
            RangeTo = new DateTime(2026, 11, 23, 12, 0, 0),
        };

        static void AssertSameEntries(List<LogEntry> expected, List<LogEntry> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Same(expected[i], actual[i]);
        }

        // ---- Format / WriteTo byte-identity ----

        [Fact]
        public void Format_NullInputs_MatchesLegacy()
        {
            // Fixed context: a null ctx would default ExportedAt to DateTime.Now
            // independently in each call, making the comparison time-dependent.
            var ctx = SampleContext();
            Assert.Equal(LegacyFormat(null, ctx), LogExporter.Format(null, ctx));
            string nullCtx = LogExporter.Format(null, null);
            Assert.Contains("==== BOTW Extractor 运行日志 ====", nullCtx);
            Assert.Contains("日志条数: 0", nullCtx);
        }

        [Fact]
        public void Format_Empty_MatchesLegacy()
        {
            // Share one context: two separate `new LogContext()` instances stamp
            // ExportedAt with DateTime.Now independently, which makes the byte
            // comparison flaky whenever the two calls straddle a millisecond.
            var ctx = new LogContext();
            Assert.Equal(
                LegacyFormat(Array.Empty<LogEntry>(), ctx),
                LogExporter.Format(Array.Empty<LogEntry>(), ctx));
        }

        [Fact]
        public void Format_NullFieldsAndUnicode_MatchesLegacy()
        {
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            var entries = new[]
            {
                At(t0, null, null),
                At(t0.AddMilliseconds(1), "", ""),
                At(t0.AddSeconds(1), "WARN", "提取失败：文件不存在"),
                At(t0.AddSeconds(2), "ERROR", "line1\nline2\ttab"),
                At(t0.AddSeconds(3), "INFO", new string('x', 5000)),
            };
            var ctx = SampleContext();
            ctx.Device = null;
            ctx.Palette = null;
            ctx.RangeDescription = null;
            Assert.Equal(LegacyFormat(entries, ctx), LogExporter.Format(entries, ctx));
        }

        [Fact]
        public void Format_Randomized_MatchesLegacy()
        {
            var rnd = new Random(20261123);
            var t0 = new DateTime(2026, 11, 23, 0, 0, 0);
            string[] levels = { "INFO", "WARN", "ERROR", "DEBUG", null, "" };
            var entries = new List<LogEntry>();
            for (int i = 0; i < 500; i++)
            {
                string msg = rnd.Next(4) switch
                {
                    0 => null,
                    1 => "",
                    2 => "提取进度 " + rnd.Next(100) + "% · 文件 game_" + rnd.Next(10000) + ".nca",
                    _ => new string((char)('a' + rnd.Next(26)), rnd.Next(1, 200)),
                };
                entries.Add(At(t0.AddMilliseconds(rnd.Next(86_400_000)), levels[rnd.Next(levels.Length)], msg));
            }
            var ctx = SampleContext();
            string expected = LegacyFormat(entries, ctx);
            string actual = LogExporter.Format(entries, ctx);
            Assert.Equal(expected, actual);
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        }

        [Fact]
        public void WriteTo_MatchesFormat_ByteForByte()
        {
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            var entries = new[]
            {
                At(t0, "INFO", "开始提取"),
                At(t0.AddSeconds(1), null, null),
                At(t0.AddSeconds(2), "WARN", "跳过损坏文件"),
            };
            var ctx = SampleContext();
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) LogExporter.WriteTo(w, entries, ctx);
            string streamed = sb.ToString();
            Assert.Equal(LogExporter.Format(entries, ctx), streamed);
            Assert.Equal(LegacyFormat(entries, ctx), streamed);
        }

        [Fact]
        public void WriteTo_NullInputs_MatchesLegacy()
        {
            var ctx = SampleContext();
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) LogExporter.WriteTo(w, null, ctx);
            Assert.Equal(LegacyFormat(null, ctx), sb.ToString());
            var sb2 = new StringBuilder();
            using (var w = new StringWriter(sb2)) LogExporter.WriteTo(w, null, null);
            Assert.Contains("日志条数: 0", sb2.ToString());
        }

        [Fact]
        public void WriteTo_Utf8NoBom_StreamEqualsFormatBytes()
        {
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            var entries = new[] { At(t0, "INFO", "中文消息 mixed ascii") };
            var ctx = SampleContext();
            using var ms = new MemoryStream();
            using (var w = new StreamWriter(ms, new UTF8Encoding(false), 64 * 1024))
                LogExporter.WriteTo(w, entries, ctx);
            Assert.Equal(Encoding.UTF8.GetBytes(LogExporter.Format(entries, ctx)), ms.ToArray());
        }

        // ---- Filter: binary fast path / linear fallback == legacy LINQ ----

        static readonly DateTime T0 = new DateTime(2026, 11, 23, 0, 0, 0);

        static List<(DateTime from, DateTime to)> Ranges(int count)
        {
            var last = T0.AddSeconds(count - 1);
            return new List<(DateTime, DateTime)>
            {
                (DateTime.MinValue, DateTime.MaxValue),          // all
                (T0, last),                                      // exact full span
                (T0, T0),                                        // single first tick
                (last, last),                                    // single last tick
                (T0.AddSeconds(10), T0.AddSeconds(20)),          // middle window
                (T0.AddSeconds(-5), T0.AddSeconds(-1)),          // entirely before
                (last.AddSeconds(1), last.AddSeconds(5)),        // entirely after
                (T0.AddSeconds(20), T0.AddSeconds(10)),          // from > to
                (DateTime.MinValue, DateTime.MinValue),          // empty range
                (T0.AddSeconds(5).AddMilliseconds(500), T0.AddSeconds(9).AddMilliseconds(500)), // fractional
            };
        }

        [Fact]
        public void Filter_SortedStore_MatchesLegacy()
        {
            const int n = 2000;
            var store = new LogStore(n + 10);
            var all = new List<LogEntry>();
            for (int i = 0; i < n; i++)
            {
                var e = At(T0.AddSeconds(i), "INFO", "m" + i);
                store.Add(e);
                all.Add(e);
            }
            foreach (var (from, to) in Ranges(n))
                AssertSameEntries(LegacyFilter(all, from, to), store.Filter(from, to));
        }

        [Fact]
        public void Filter_UnsortedStore_MatchesLegacy()
        {
            const int n = 2000;
            var rnd = new Random(42);
            var entries = new List<LogEntry>();
            for (int i = 0; i < n; i++)
                entries.Add(At(T0.AddSeconds(i), "INFO", "m" + i));
            // Shuffle insertion order -> store must fall back to linear scan.
            for (int i = entries.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (entries[i], entries[j]) = (entries[j], entries[i]);
            }
            var store = new LogStore(n + 10);
            foreach (var e in entries) store.Add(e);
            foreach (var (from, to) in Ranges(n))
                AssertSameEntries(LegacyFilter(entries, from, to), store.Filter(from, to));
        }

        [Fact]
        public void Filter_AfterCapacityTrim_MatchesLegacy()
        {
            var store = new LogStore(100);
            var all = new List<LogEntry>();
            for (int i = 0; i < 300; i++)
            {
                var e = At(T0.AddSeconds(i), "INFO", "m" + i);
                store.Add(e);
                all.Add(e);
            }
            var kept = all.Skip(200).ToList();
            foreach (var (from, to) in Ranges(300))
                AssertSameEntries(LegacyFilter(kept, from, to), store.Filter(from, to));
        }

        [Fact]
        public void Filter_AfterClear_RestoresSortedFastPath()
        {
            var store = new LogStore();
            store.Add(At(T0.AddHours(2), "INFO", "late"));
            store.Add(At(T0, "INFO", "early")); // breaks sortedness
            store.Clear();
            var all = new List<LogEntry>();
            for (int i = 0; i < 50; i++)
            {
                var e = At(T0.AddSeconds(i), "INFO", "n" + i);
                store.Add(e);
                all.Add(e);
            }
            foreach (var (from, to) in Ranges(50))
                AssertSameEntries(LegacyFilter(all, from, to), store.Filter(from, to));
        }

        [Fact]
        public void Filter_EmptyStore_ReturnsEmpty()
        {
            var store = new LogStore();
            Assert.Empty(store.Filter(DateTime.MinValue, DateTime.MaxValue));
        }

        [Fact]
        public void Filter_DuplicateTimestamps_Inclusive()
        {
            var store = new LogStore();
            var all = new List<LogEntry>();
            for (int i = 0; i < 10; i++)
            {
                var e = At(T0, "INFO", "dup" + i); // identical timestamps stay sorted
                store.Add(e);
                all.Add(e);
            }
            AssertSameEntries(LegacyFilter(all, T0, T0), store.Filter(T0, T0));
        }
    }
}
