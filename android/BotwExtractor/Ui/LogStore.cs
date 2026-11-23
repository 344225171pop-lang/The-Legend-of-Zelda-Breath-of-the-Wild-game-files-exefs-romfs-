using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BotwExtractor.Ui
{
    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } = "INFO";
        public string Message { get; set; } = "";
    }

    // Thread-safe in-memory ring of runtime logs. The on-screen window shows a
    // short tail; the store keeps a longer history so exports are complete.
    public sealed class LogStore
    {
        public const int DefaultCapacity = 5000;
        readonly object _gate = new object();
        readonly List<LogEntry> _entries = new List<LogEntry>();
        readonly int _capacity;

        public LogStore(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        public int Count { get { lock (_gate) return _entries.Count; } }

        public void Add(string level, string message) =>
            Add(new LogEntry { Timestamp = DateTime.Now, Level = level ?? "INFO", Message = message ?? "" });

        public void Add(LogEntry entry)
        {
            if (entry == null) return;
            lock (_gate)
            {
                _entries.Add(entry);
                if (_entries.Count > _capacity)
                    _entries.RemoveRange(0, _entries.Count - _capacity);
            }
        }

        public List<LogEntry> Snapshot()
        {
            lock (_gate) return new List<LogEntry>(_entries);
        }

        // Inclusive [from, to] filter.
        public List<LogEntry> Filter(DateTime from, DateTime to)
        {
            lock (_gate)
                return _entries.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
        }

        public void Clear() { lock (_gate) _entries.Clear(); }
    }

    public enum LogRangePreset { All, LastHour, Last24Hours }

    public static class LogRange
    {
        public static string Describe(LogRangePreset p) => p switch
        {
            LogRangePreset.LastHour => "最近 1 小时",
            LogRangePreset.Last24Hours => "最近 24 小时",
            _ => "全部日志",
        };

        public static (DateTime From, DateTime To) Resolve(LogRangePreset p, DateTime now) => p switch
        {
            LogRangePreset.LastHour => (now.AddHours(-1), now),
            LogRangePreset.Last24Hours => (now.AddDays(-1), now),
            _ => (DateTime.MinValue, now),
        };
    }

    // System context written into every export header.
    public sealed class LogContext
    {
        public string AppVersion { get; set; } = "";
        public string Device { get; set; } = "";
        public string AndroidVersion { get; set; } = "";
        public string ThemeMode { get; set; } = "";
        public string Palette { get; set; } = "";
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        public string RangeDescription { get; set; } = "";
        public DateTime RangeFrom { get; set; }
        public DateTime RangeTo { get; set; }
    }

    public static class LogExporter
    {
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

        // Builds a standard, self-describing .log body: a context header followed
        // by one "[timestamp] [LEVEL] message" line per entry.
        public static string Format(IReadOnlyList<LogEntry> entries, LogContext ctx)
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
            sb.AppendLine("导出时间: " + ctx.ExportedAt.ToString(TimestampFormat));
            sb.AppendLine("日志范围: " + (ctx.RangeDescription ?? "") +
                " (" + ctx.RangeFrom.ToString(TimestampFormat) + " ~ " + ctx.RangeTo.ToString(TimestampFormat) + ")");
            sb.AppendLine("日志条数: " + entries.Count);
            sb.AppendLine("================================");
            foreach (var e in entries)
                sb.AppendLine("[" + e.Timestamp.ToString(TimestampFormat) + "] [" + (e.Level ?? "INFO") + "] " + (e.Message ?? ""));
            return sb.ToString();
        }

        public static string SuggestedFileName(DateTime now) =>
            "botw_log_" + now.ToString("yyyyMMdd_HHmmss") + ".log";
    }
}
