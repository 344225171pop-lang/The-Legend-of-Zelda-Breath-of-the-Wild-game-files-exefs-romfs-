using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BotwExtractor.Ui;

namespace ExportBench
{
    // Reproducible before/after benchmark for the log-export optimization.
    // "Legacy" methods are verbatim copies of the v1.5 algorithms; "New"
    // methods call the shared production code in android/BotwExtractor/Ui.
    // Every size also verifies that old and new pipelines emit identical bytes.
    static class Program
    {
        sealed class Result { public double Ms; public double AllocMB; }

        static readonly object Gate = new object();

        // ---- Legacy (v1.5) reference implementations ----

        static List<LogEntry> LegacyFilter(List<LogEntry> all, DateTime from, DateTime to)
        {
            lock (Gate)
                return all.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
        }

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

        // v1.5 OnActivityResult: default 1 KB StreamWriter buffer, one giant Write.
        static void LegacyWrite(Stream output, string content)
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.Write(content);
        }

        // ---- New (v1.6) pipeline ----

        static void NewWrite(Stream output, IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            LogExporter.WriteTo(writer, entries, ctx);
        }

        // ---- Harness ----

        static Result Measure(Action a, int iters)
        {
            a(); // warmup (JIT + caches)
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) a();
            sw.Stop();
            double alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
            return new Result { Ms = sw.Elapsed.TotalMilliseconds / iters, AllocMB = alloc / (1024 * 1024) };
        }

        static List<LogEntry> Generate(int n)
        {
            var rnd = new Random(20261123);
            var start = new DateTime(2026, 11, 16, 0, 0, 0);
            double step = 7.0 * 86400 / n; // entries span 7 days, strictly increasing
            string[] levels = { "INFO", "INFO", "INFO", "WARN", "ERROR" };
            var list = new List<LogEntry>(n);
            for (int i = 0; i < n; i++)
            {
                string msg = (i % 4) switch
                {
                    0 => "提取进度 " + (i * 100 / n) + "% · 文件 game_" + (i % 10000) + ".nca",
                    1 => "校验通过: " + ((uint)(i * 2654435761u) % 0xFFFFFF).ToString("x6"),
                    2 => "跳过损坏文件: section_" + (i % 500) + ".bin",
                    _ => "Extraction chunk " + i + " completed successfully",
                };
                list.Add(new LogEntry
                {
                    Timestamp = start.AddSeconds(i * step),
                    Level = levels[rnd.Next(levels.Length)],
                    Message = msg,
                });
            }
            return list;
        }

        static LogContext MakeContext(DateTime from, DateTime to) => new LogContext
        {
            AppVersion = "1.6",
            Device = "Benchmark Host",
            AndroidVersion = "13 (API 33)",
            ThemeMode = "跟随系统",
            Palette = "默认",
            ExportedAt = new DateTime(2026, 11, 23, 12, 0, 0),
            RangeDescription = "全部日志",
            RangeFrom = from,
            RangeTo = to,
        };

        static string Speedup(double oldMs, double newMs) =>
            newMs <= 0 ? "-" : (oldMs / newMs).ToString("0.0") + "x";

        static void Row(string phase, Result o, Result n)
        {
            Console.WriteLine("| " + phase + " | " + o.Ms.ToString("0.00") + " | " + n.Ms.ToString("0.00") +
                " | " + Speedup(o.Ms, n.Ms) + " | " + o.AllocMB.ToString("0.00") + " | " + n.AllocMB.ToString("0.00") + " |");
        }

        static void Main()
        {
            Console.WriteLine("# 日志导出性能对比基准（ExportBench）");
            Console.WriteLine();
            Console.WriteLine("- 时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("- 运行时: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
                " · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Console.WriteLine("- CPU 逻辑核心: " + Environment.ProcessorCount + " · 工作站 GC（非并发）");
            Console.WriteLine("- 方法: 每阶段 1 次预热 + N 次计时取平均；分配量取 GC.GetAllocatedBytesForCurrentThread 差值");
            Console.WriteLine("- 数据: 固定种子生成，时间戳单调递增，跨度 7 天；中英文混合消息");
            Console.WriteLine();

            int[] sizes = { 1_000, 5_000, 10_000, 100_000, 1_000_000 };
            foreach (int n in sizes)
            {
                int iters = n <= 10_000 ? 20 : n <= 100_000 ? 5 : 3;
                var entries = Generate(n);
                var store = new LogStore(n);
                foreach (var e in entries) store.Add(e);
                var start = entries[0].Timestamp;
                var end = entries[n - 1].Timestamp;
                var ctx = MakeContext(DateTime.MinValue, DateTime.MaxValue);
                var dayFrom = start.AddDays(6);

                // Correctness gate: full pipelines must emit identical bytes.
                byte[] oldBytes, newBytes;
                using (var ms = new MemoryStream())
                {
                    var f = LegacyFilter(entries, DateTime.MinValue, DateTime.MaxValue);
                    LegacyWrite(ms, LegacyFormat(f, ctx));
                    oldBytes = ms.ToArray();
                }
                using (var ms = new MemoryStream())
                {
                    var f = store.Filter(DateTime.MinValue, DateTime.MaxValue);
                    NewWrite(ms, f, ctx);
                    newBytes = ms.ToArray();
                }
                bool identical = oldBytes.AsSpan().SequenceEqual(newBytes);
                Console.WriteLine("## 数据量 " + n.ToString("N0") + " 条（计时 " + iters + " 次平均，输出 " +
                    (oldBytes.Length / 1024.0 / 1024.0).ToString("0.00") + " MB）");
                Console.WriteLine();
                Console.WriteLine("字节一致性校验: " + (identical ? "通过（新旧输出完全一致）" : "失败！！！"));
                if (!identical) throw new InvalidOperationException("Export bytes differ at n=" + n);
                oldBytes = null; newBytes = null;
                Console.WriteLine();
                Console.WriteLine("| 阶段 | 旧 v1.5 (ms) | 新 v1.6 (ms) | 提速 | 旧分配 (MB) | 新分配 (MB) |");
                Console.WriteLine("|---|---|---|---|---|---|");

                Row("Filter 全部范围",
                    Measure(() => LegacyFilter(entries, DateTime.MinValue, DateTime.MaxValue), iters),
                    Measure(() => store.Filter(DateTime.MinValue, DateTime.MaxValue), iters));
                Row("Filter 最近24小时(约1/7)",
                    Measure(() => LegacyFilter(entries, dayFrom, end), iters),
                    Measure(() => store.Filter(dayFrom, end), iters));
                Row("Format 全量字符串",
                    Measure(() => LegacyFormat(entries, ctx), iters),
                    Measure(() => LogExporter.Format(entries, ctx), iters));

                // Apples-to-apples export core: old = build giant string then
                // write it; new = stream-format directly into the writer.
                Row("导出核心(格式化+写入)",
                    Measure(() =>
                    {
                        string s = LegacyFormat(entries, ctx);
                        using var ms = new MemoryStream();
                        LegacyWrite(ms, s);
                    }, iters),
                    Measure(() => { using var ms = new MemoryStream(); NewWrite(ms, entries, ctx); }, iters));
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

                Row("端到端(筛选+格式化+写入)",
                    Measure(() =>
                    {
                        var f = LegacyFilter(entries, DateTime.MinValue, DateTime.MaxValue);
                        using var ms = new MemoryStream();
                        LegacyWrite(ms, LegacyFormat(f, ctx));
                    }, iters),
                    Measure(() =>
                    {
                        var f = store.Filter(DateTime.MinValue, DateTime.MaxValue);
                        using var ms = new MemoryStream();
                        NewWrite(ms, f, ctx);
                    }, iters));

                Console.WriteLine();
                entries = null; store = null;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Console.WriteLine("基准完成：所有量级字节一致性校验通过。");
        }
    }
}
