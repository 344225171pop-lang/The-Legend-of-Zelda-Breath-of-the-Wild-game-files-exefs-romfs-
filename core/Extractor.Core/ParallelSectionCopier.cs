using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Extractor.Core
{
    // One entry of an extracted section tree (path is relative, '/'-separated).
    public sealed class SourceEntry
    {
        public string RelativePath { get; set; } = "";
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
    }

    // Random-access view of a single file inside a section. Reads are both
    // offset-based and independent of any shared stream position, so a worker
    // can hand different ranges of the same file to different threads.
    public interface IRandomFileSource : IDisposable
    {
        long Length { get; }
        int Read(long offset, Span<byte> destination);
    }

    // Per-worker session: one independent view of the whole section. Each
    // worker owns its own session because the underlying NCA file system is
    // not thread-safe; separate sessions are fully independent.
    public interface ISectionSession : IDisposable
    {
        IRandomFileSource OpenFile(string relativePath);
    }

    public interface ISectionSourceFactory
    {
        // Single-threaded, called once before copying starts.
        List<SourceEntry> Enumerate();
        // Called once per worker thread.
        ISectionSession OpenSession();
    }

    public sealed class ParallelCopyOptions
    {
        public int Parallelism { get; set; } = 4;
        // Files larger than this are split into chunks copied by several workers.
        public long ChunkThresholdBytes { get; set; } = 32L << 20;
        public int BufferSize { get; set; } = 1 << 20;
        // Report progress at most once per this many bytes.
        public long ProgressIntervalBytes { get; set; } = 32L << 20;
    }

    public sealed class ParallelCopyResult
    {
        public long FileCount;
        public long ByteCount;
        public double ElapsedMs;
        public int WorkersUsed;
    }

    // Copies a section tree out of an NCA onto local storage using a pool of
    // worker threads. Small files are copied whole by one worker; large files
    // are split into chunks that workers write in parallel at fixed offsets.
    // Output is byte-identical to a serial copy of the same tree.
    public static class ParallelSectionCopier
    {
        struct CopyTask
        {
            public string RelativePath;
            public string Destination;
            public long Size;
            public long Offset;
            public long Length;
            public bool WholeFile;
            public int FileId;
        }

        public static ParallelCopyResult Copy(
            IReadOnlyList<SourceEntry> entries,
            string outRoot,
            ISectionSourceFactory factory,
            ParallelCopyOptions options,
            Action<long, long> onProgress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var opt = options ?? new ParallelCopyOptions();
            int parallelism = Math.Max(1, opt.Parallelism);
            int bufferSize = Math.Max(64 * 1024, opt.BufferSize);
            long threshold = Math.Max(1, opt.ChunkThresholdBytes);
            long reportInterval = Math.Max(1 << 20, opt.ProgressIntervalBytes);

            // 1) Create every directory once, up front (deduplicated). Workers
            //    then never race on directory creation for the common case.
            var created = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                string dest = MapDestination(outRoot, e.RelativePath);
                if (e.IsDirectory)
                {
                    if (created.Add(dest)) Directory.CreateDirectory(dest);
                }
                else
                {
                    string parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent) && created.Add(parent))
                        Directory.CreateDirectory(parent);
                }
            }

            // 2) Build the work list. Large files become pre-allocated chunks so
            //    several workers can fill one file concurrently. remaining[]
            //    tracks unfinished chunks per file so progress counts real files.
            var tasks = new List<CopyTask>(entries.Count);
            long fileCount = 0, totalBytes = 0;
            int fileId = 0;
            var remaining = new List<int>(entries.Count);
            foreach (var e in entries)
            {
                if (e.IsDirectory) continue;
                fileCount++;
                totalBytes += e.Size;
                string dest = MapDestination(outRoot, e.RelativePath);
                if (e.Size > threshold)
                {
                    long chunks = Math.Min(parallelism * 2L, (e.Size + threshold - 1) / threshold);
                    if (chunks > 1)
                    {
                        long chunkSize = (e.Size + chunks - 1) / chunks;
                        using (var pre = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                   FileShare.ReadWrite, 1, FileOptions.None))
                            pre.SetLength(e.Size);
                        int count = 0;
                        for (long off = 0; off < e.Size; off += chunkSize)
                        {
                            long len = Math.Min(chunkSize, e.Size - off);
                            tasks.Add(new CopyTask
                            {
                                RelativePath = e.RelativePath,
                                Destination = dest,
                                Size = e.Size,
                                Offset = off,
                                Length = len,
                                WholeFile = false,
                                FileId = fileId,
                            });
                            count++;
                        }
                        remaining.Add(count);
                        fileId++;
                        continue;
                    }
                }
                tasks.Add(new CopyTask
                {
                    RelativePath = e.RelativePath,
                    Destination = dest,
                    Size = e.Size,
                    Offset = 0,
                    Length = e.Size,
                    WholeFile = true,
                    FileId = fileId,
                });
                remaining.Add(1);
                fileId++;
            }

            var result = new ParallelCopyResult { FileCount = fileCount, ByteCount = totalBytes };
            if (tasks.Count == 0)
            {
                result.WorkersUsed = 0;
                return result;
            }

            // 3) Largest tasks first: big files start immediately and small
            //    files fill the remaining slots, which shortens the tail.
            tasks.Sort((a, b) => b.Length.CompareTo(a.Length));

            int workerCount = Math.Min(parallelism, tasks.Count);
            result.WorkersUsed = workerCount;

            var queue = new System.Collections.Concurrent.ConcurrentQueue<CopyTask>(tasks);
            int[] chunksLeft = remaining.ToArray();
            long filesDone = 0, bytesDone = 0, nextReportAt = reportInterval;
            var started = System.Diagnostics.Stopwatch.StartNew();

            Exception error = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                void Work()
                {
                    ISectionSession session = null;
                    try
                    {
                        session = factory.OpenSession();
                        var buffer = new byte[bufferSize];
                        while (queue.TryDequeue(out CopyTask task))
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            using (var src = session.OpenFile(task.RelativePath))
                                CopyOne(src, task, buffer, linked.Token);

                            long bytes = Interlocked.Add(ref bytesDone, task.Length);
                            if (Interlocked.Decrement(ref chunksLeft[task.FileId]) == 0)
                                Interlocked.Increment(ref filesDone);
                            if (bytes >= Interlocked.Read(ref nextReportAt))
                            {
                                Interlocked.Exchange(ref nextReportAt, bytes + reportInterval);
                                onProgress?.Invoke(Interlocked.Read(ref filesDone), bytes);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        // Cancelled by caller or by a sibling worker's failure.
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref error, ex, null) == null)
                            linked.Cancel();
                    }
                    finally
                    {
                        session?.Dispose();
                    }
                }

                if (workerCount == 1)
                {
                    Work();
                }
                else
                {
                    // Dedicated threads: these workers are IO/decrypt-bound and
                    // block for the whole copy, so they must not starve (or be
                    // delayed by) the shared thread pool.
                    var workers = new Task[workerCount];
                    for (int i = 0; i < workerCount; i++)
                        workers[i] = Task.Factory.StartNew(Work, CancellationToken.None,
                            TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Task.WaitAll(workers);
                }
            }

            if (error != null) throw error;
            ct.ThrowIfCancellationRequested();

            started.Stop();
            result.ElapsedMs = started.Elapsed.TotalMilliseconds;
            onProgress?.Invoke(Interlocked.Read(ref filesDone), Interlocked.Read(ref bytesDone));
            return result;
        }

        static void CopyOne(IRandomFileSource src, in CopyTask task, byte[] buffer, CancellationToken ct)
        {
            if (task.WholeFile)
            {
                using (var output = new FileStream(task.Destination, FileMode.Create, FileAccess.Write,
                           FileShare.None, 1 << 16, FileOptions.SequentialScan))
                {
                    long pos = 0;
                    while (pos < task.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(buffer.Length, task.Length - pos);
                        int read = src.Read(pos, buffer.AsSpan(0, want));
                        if (read <= 0) throw new IOException("Short read: " + task.RelativePath);
                        output.Write(buffer, 0, read);
                        pos += read;
                    }
                }
                return;
            }

            // Fixed-offset parallel chunk: positional write into the file that
            // the planner pre-allocated, so no seek state is shared.
            using (var output = new FileStream(task.Destination, FileMode.Open, FileAccess.Write,
                       FileShare.ReadWrite, 1, FileOptions.RandomAccess))
            {
                long pos = 0;
                while (pos < task.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, task.Length - pos);
                    int read = src.Read(task.Offset + pos, buffer.AsSpan(0, want));
                    if (read <= 0) throw new IOException("Short read: " + task.RelativePath);
                    RandomAccess.Write(output.SafeFileHandle, buffer.AsSpan(0, read), task.Offset + pos);
                    pos += read;
                }
            }
        }

        static string MapDestination(string outRoot, string relativePath)
        {
            string rel = relativePath ?? "";
            while (rel.StartsWith("/", StringComparison.Ordinal)) rel = rel.Substring(1);
            if (rel.Length == 0) return outRoot;
            return Path.Combine(outRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
