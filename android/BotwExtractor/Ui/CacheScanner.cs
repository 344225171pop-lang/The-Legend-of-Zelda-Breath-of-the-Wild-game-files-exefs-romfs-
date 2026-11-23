using System;
using System.Collections.Generic;
using System.IO;

namespace BotwExtractor.Ui
{
    public sealed class CacheCandidate
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Path { get; set; }
        // When true the item is shown but excluded from cleaning (e.g. the
        // extraction temp dir while a job is running).
        public bool Protected { get; set; }
        public string ProtectedReason { get; set; } = "任务进行中，暂不可清理";
    }

    public sealed class CacheItem
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public bool Cleanable { get; set; }
        public string SkipReason { get; set; }
    }

    public sealed class CleanResult
    {
        public long FreedBytes { get; set; }
        public int RemovedFiles { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    // Pure filesystem helpers (System.IO only) so they can be unit-tested off-device.
    public static class CacheScanner
    {
        public static long DirectorySize(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        public static List<CacheItem> Scan(IEnumerable<CacheCandidate> candidates)
        {
            var items = new List<CacheItem>();
            if (candidates == null) return items;
            foreach (var c in candidates)
            {
                if (c == null) continue;
                bool exists = !string.IsNullOrEmpty(c.Path) && Directory.Exists(c.Path);
                long size = exists ? DirectorySize(c.Path) : 0;
                bool cleanable = exists && !c.Protected;
                items.Add(new CacheItem
                {
                    Key = c.Key,
                    Label = c.Label,
                    Path = c.Path,
                    SizeBytes = size,
                    Cleanable = cleanable,
                    SkipReason = c.Protected ? c.ProtectedReason : (exists ? null : "目录不存在"),
                });
            }
            return items;
        }

        public static long TotalSize(IEnumerable<CacheItem> items)
        {
            long t = 0;
            if (items != null) foreach (var i in items) t += i.SizeBytes;
            return t;
        }

        // Deletes the contents of cleanable items only, keeping the root folders.
        // Protected / non-existent items are never touched.
        public static CleanResult Clean(IEnumerable<CacheItem> items)
        {
            var result = new CleanResult();
            if (items == null) return result;
            foreach (var item in items)
            {
                if (item == null || !item.Cleanable) continue;
                if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path)) continue;
                long before = DirectorySize(item.Path);
                int removed = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); removed++; }
                        catch (Exception ex) { result.Errors.Add(item.Label + ": " + ex.Message); }
                    }
                    foreach (var d in Directory.EnumerateDirectories(item.Path))
                    {
                        try { Directory.Delete(d, true); }
                        catch (Exception ex) { result.Errors.Add(item.Label + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) { result.Errors.Add(item.Label + ": " + ex.Message); }
                result.RemovedFiles += removed;
                result.FreedBytes += before - DirectorySize(item.Path);
            }
            return result;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.#") + " GB";
        }
    }
}
