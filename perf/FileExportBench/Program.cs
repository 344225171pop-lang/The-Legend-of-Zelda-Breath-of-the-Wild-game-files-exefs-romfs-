using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BotwExtractor.Ui;

namespace FileExportBench
{
    // Measures the real log-export path: format the selected range and stream it
    // into a freshly created file with the same 64 KB buffered writer the app
    // uses for SAF exports. The legacy (v1.5) pipeline — one giant string plus a
    // 1 KB buffered writer — is measured alongside it so the report can show the
    // delta, and every size proves both pipelines emit identical bytes.
    static class Program
    {
        struct Stats
        {
            public double MedianMs;
            public double MinMs;
            public double AllocMB;
        }

        static Stats Measure(Action action, int iterations)
        {
            action(); // warmup: JIT + first-touch page cache
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            var times = new List<double>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            double alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;

            times.Sort();
            int mid = times.Count / 2;
            double median = times.Count % 2 == 1 ? times[mid] : (times[mid - 1] + times[mid]) / 2.0;
            return new Stats
            {
                MedianMs = median,
                MinMs = times[0],
                AllocMB = alloc / (1024 * 1024),
            };
        }

        // ---- Legacy (v1.5) reference pipeline: giant string + 1 KB writer ----

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

        static void LegacyExportToFile(string path, IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            string content = LegacyFormat(entries, ctx);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                1024, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024);
            writer.Write(content);
        }

        // ---- Current pipeline: stream the formatter into a 64 KB buffered file ----

        static void ExportToFile(string path, IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
            LogExporter.WriteTo(writer, entries, ctx);
        }

        static List<LogEntry> Generate(int n)
        {
            var rnd = new Random(20261127);
            var start = new DateTime(2026, 11, 20, 8, 0, 0);
            double step = 3.0 * 3600 / Math.Max(1, n); // three hours of log traffic
            string[] levels = { "INFO", "INFO", "INFO", "INFO", "WARN", "ERROR" };
            var list = new List<LogEntry>(n);
            for (int i = 0; i < n; i++)
            {
                string msg = (i % 5) switch
                {
                    0 => "提取进度 " + (i * 100 / Math.Max(1, n)) + "% · 文件 game_" + (i % 10000) + ".nca",
                    1 => "校验通过: " + ((uint)(i * 2654435761u) % 0xFFFFFF).ToString("x6"),
                    2 => "跳过损坏文件: section_" + (i % 500) + ".bin",
                    3 => "exefs: 128 文件，0.42 GiB",
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

        static LogContext Context(DateTime from, DateTime to) => new LogContext
        {
            AppVersion = "1.8.1",
            Device = "Benchmark Host",
            AndroidVersion = "13 (API 33)",
            ThemeMode = "跟随系统",
            Palette = "默认 主色 #00696D / 辅助 #4A6364 / 强调 #4D616C",
            ExportedAt = new DateTime(2026, 11, 27, 23, 40, 0),
            RangeDescription = "全部日志",
            RangeFrom = from,
            RangeTo = to,
        };

        static void Main()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "botw_export_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            int[] sizes = { 0, 100, 500, 1_000, 5_000, 20_000, 100_000 };
            int[] iterations = { 300, 300, 200, 200, 100, 20, 5 };

            Console.WriteLine("# 日志文件导出实测（FileExportBench）");
            Console.WriteLine();
            Console.WriteLine("- 时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("- 运行时: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
                " · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Console.WriteLine("- CPU 逻辑核心: " + Environment.ProcessorCount + " · 工作站 GC（非并发）");
            Console.WriteLine("- 临时目录: " + workDir);
            Console.WriteLine("- 方法: 每量级 1 次预热 + N 次计时，取中位数（p50）与最小值；每次导出都新建并删除文件（模拟 SAF 新建文档）");
            Console.WriteLine("- 现实现: LogExporter.WriteTo → UTF-8(无 BOM) 64 KB StreamWriter → FileStream(SequentialScan)");
            Console.WriteLine("- 旧实现(v1.5): LegacyFormat 全量字符串 → 1 KB StreamWriter 单次 Write");
            Console.WriteLine("- 条目速率对 0 条无意义，记为 “-”");
            Console.WriteLine();

            var totalSw = Stopwatch.StartNew();
            for (int idx = 0; idx < sizes.Length; idx++)
            {
                int size = sizes[idx];
                int iters = iterations[idx];
                var entries = Generate(size);
                DateTime from = size > 0 ? entries[0].Timestamp : DateTime.Now.AddHours(-3);
                DateTime to = size > 0 ? entries[entries.Count - 1].Timestamp : DateTime.Now;
                var ctx = Context(from, to);

                var preview = Measure(() => LogExporter.Format(entries, ctx), iters);

                string currentPath = Path.Combine(workDir, "current_" + size + ".log");
                string legacyPath = Path.Combine(workDir, "legacy_" + size + ".log");
                var current = Measure(() => ExportToFile(currentPath, entries, ctx), iters);
                var legacy = Measure(() => LegacyExportToFile(legacyPath, entries, ctx), iters);

                long bytes = new FileInfo(currentPath).Length;
                byte[] currentBytes = File.ReadAllBytes(currentPath);
                byte[] legacyBytes = File.ReadAllBytes(legacyPath);
                bool identical = currentBytes.AsSpan().SequenceEqual(legacyBytes)
                    && string.Equals(Encoding.UTF8.GetString(currentBytes), LogExporter.Format(entries, ctx), StringComparison.Ordinal);
                double speedup = current.MedianMs > 0 ? legacy.MedianMs / current.MedianMs : 0;

                Console.WriteLine("## " + size.ToString("N0") + " 条（输出 " + FormatSize(bytes) + "，计时 " + iters + " 次）");
                Console.WriteLine();
                Console.WriteLine("| 指标 | 预览格式化（内存） | 旧实现导出(v1.5) | 现实现导出(v1.8.1) | 提速 |");
                Console.WriteLine("|---|---|---|---|---|");
                Console.WriteLine("| 中位耗时 p50 | " + Ms(preview.MedianMs) + " | " + Ms(legacy.MedianMs) + " | " + Ms(current.MedianMs) + " | " + Speedup(speedup) + " |");
                Console.WriteLine("| 最快耗时 min | " + Ms(preview.MinMs) + " | " + Ms(legacy.MinMs) + " | " + Ms(current.MinMs) + " | - |");
                Console.WriteLine("| 吞吐（条/秒, p50） | " + Rate(size, preview.MedianMs) + " | " + Rate(size, legacy.MedianMs) + " | " + Rate(size, current.MedianMs) + " | - |");
                Console.WriteLine("| 吞吐（MB/秒, p50） | " + Throughput(bytes, preview.MedianMs) + " | " + Throughput(bytes, legacy.MedianMs) + " | " + Throughput(bytes, current.MedianMs) + " | - |");
                Console.WriteLine("| 分配 | " + preview.AllocMB.ToString("0.00") + " MB | " + legacy.AllocMB.ToString("0.00") + " MB | " + current.AllocMB.ToString("0.00") + " MB | - |");
                Console.WriteLine("| 字节一致性 | - | 基准 | " + (identical ? "与旧实现完全一致" : "失败！！！") + " | - |");
                Console.WriteLine();
                if (!identical) throw new InvalidOperationException("file export bytes differ at n=" + size);

                File.Delete(currentPath);
                File.Delete(legacyPath);
            }

            totalSw.Stop();
            Console.WriteLine("总耗时: " + totalSw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            try { Directory.Delete(workDir, true); } catch { }
        }

        static string Speedup(double factor) => factor <= 0 ? "-" : factor.ToString("0.0") + "x";

        static string Ms(double ms) => ms >= 100 ? ms.ToString("0.0") + " ms" : ms.ToString("0.00") + " ms";

        static string Rate(int entries, double ms) =>
            entries <= 0 || ms <= 0 ? "-" : (entries / ms * 1000).ToString("N0") + " 条/秒";

        static string Throughput(long bytes, double ms) =>
            ms <= 0 ? "-" : (bytes / 1024.0 / 1024.0 / (ms / 1000.0)).ToString("0.0") + " MB/s";

        static string FormatSize(long bytes) =>
            bytes < 1024 ? bytes + " B" : (bytes / 1024.0).ToString("0.0") + " KB";
    }
}
