using System;
using System.Collections.Generic;
using System.IO;
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
        // True while timestamps are non-decreasing; enables the binary-search
        // fast path in Filter. Falls back to a linear scan once violated.
        bool _sorted = true;

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
                if (_entries.Count > 0 && entry.Timestamp < _entries[_entries.Count - 1].Timestamp)
                    _sorted = false;
                _entries.Add(entry);
                if (_entries.Count > _capacity)
                    _entries.RemoveRange(0, _entries.Count - _capacity);
            }
        }

        public List<LogEntry> Snapshot()
        {
            lock (_gate) return new List<LogEntry>(_entries);
        }

        // Inclusive [from, to] filter. Sorted stores use two binary searches
        // (O(log n) locate + O(k) copy, pre-sized result); out-of-order stores
        // fall back to a single linear pass. No LINQ closures/iterators.
        public List<LogEntry> Filter(DateTime from, DateTime to)
        {
            lock (_gate)
            {
                if (_sorted)
                {
                    int lo = LowerBound(from);
                    int hi = UpperBound(to);
                    int n = hi - lo;
                    var result = new List<LogEntry>(n > 0 ? n : 0);
                    for (int i = lo; i < hi; i++) result.Add(_entries[i]);
                    return result;
                }
                var list = new List<LogEntry>();
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.Timestamp >= from && e.Timestamp <= to) list.Add(e);
                }
                return list;
            }
        }

        // First index with Timestamp >= t.
        int LowerBound(DateTime t)
        {
            int lo = 0, hi = _entries.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_entries[mid].Timestamp < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // First index with Timestamp > t.
        int UpperBound(DateTime t)
        {
            int lo = 0, hi = _entries.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_entries[mid].Timestamp <= t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public void Clear() { lock (_gate) { _entries.Clear(); _sorted = true; } }
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

        // Single source of truth for the .log format: a context header followed
        // by one "[timestamp] [LEVEL] message" line per entry. Streams straight
        // into any TextWriter (no intermediate giant string, no per-line
        // concatenation), so exports can write incrementally with buffered IO.
        public static void WriteTo(TextWriter writer, IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            ctx ??= new LogContext();
            entries ??= Array.Empty<LogEntry>();
            writer.WriteLine("==== BOTW Extractor 运行日志 ====");
            writer.Write("应用版本: "); writer.WriteLine(ctx.AppVersion ?? "");
            writer.Write("设备: "); writer.WriteLine(ctx.Device ?? "");
            writer.Write("Android 版本: "); writer.WriteLine(ctx.AndroidVersion ?? "");
            writer.Write("主题模式: "); writer.WriteLine(ctx.ThemeMode ?? "");
            writer.Write("配色方案: "); writer.WriteLine(ctx.Palette ?? "");
            writer.Write("导出时间: "); writer.WriteLine(ctx.ExportedAt.ToString(TimestampFormat));
            writer.Write("日志范围: "); writer.Write(ctx.RangeDescription ?? "");
            writer.Write(" ("); writer.Write(ctx.RangeFrom.ToString(TimestampFormat));
            writer.Write(" ~ "); writer.Write(ctx.RangeTo.ToString(TimestampFormat));
            writer.WriteLine(")");
            writer.Write("日志条数: "); writer.WriteLine(entries.Count);
            writer.WriteLine("================================");
            // One reusable char buffer for every line: timestamp formatted via
            // TryFormat (no per-line string alloc), level/message copied in,
            // then a single Write call per line instead of six small ones.
            string nl = Environment.NewLine;
            char[] buf = new char[256];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string level = e.Level ?? "INFO";
                string msg = e.Message ?? "";
                int need = 29 + level.Length + msg.Length + nl.Length; // "[ts] [level] msg" + newline
                if (need > buf.Length) Array.Resize(ref buf, Math.Max(need, buf.Length * 2));
                int p = 0;
                buf[p++] = '[';
                e.Timestamp.TryFormat(buf.AsSpan(p), out int tw, TimestampFormat);
                p += tw;
                buf[p++] = ']'; buf[p++] = ' '; buf[p++] = '[';
                level.AsSpan().CopyTo(buf.AsSpan(p)); p += level.Length;
                buf[p++] = ']'; buf[p++] = ' ';
                msg.AsSpan().CopyTo(buf.AsSpan(p)); p += msg.Length;
                nl.AsSpan().CopyTo(buf.AsSpan(p)); p += nl.Length;
                writer.Write(buf, 0, p);
            }
        }

        // Builds the full .log body as a string (used by the preview dialog).
        // Delegates to WriteTo so preview and exported file are byte-identical;
        // the StringBuilder is pre-sized to avoid repeated grow-and-copy.
        public static string Format(IReadOnlyList<LogEntry> entries, LogContext ctx)
        {
            ctx ??= new LogContext();
            entries ??= Array.Empty<LogEntry>();
            var sb = new StringBuilder(256 + entries.Count * 80);
            using (var w = new StringWriter(sb)) WriteTo(w, entries, ctx);
            return sb.ToString();
        }

        public static string SuggestedFileName(DateTime now) =>
            "botw_log_" + now.ToString("yyyyMMdd_HHmmss") + ".log";
    }
}
