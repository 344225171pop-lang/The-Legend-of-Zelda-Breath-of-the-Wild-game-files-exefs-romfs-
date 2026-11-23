using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Extractor.Core;
using Xunit;

namespace BotwExtractor.Tests
{
    // Verifies the parallel section copier used for exefs/romfs extraction:
    // output must be byte-identical to a serial copy, large files must be
    // correctly reassembled from concurrently written chunks, and failures or
    // cancellation must surface instead of hanging.
    public class ParallelSectionCopierTests
    {
        sealed class MemoryFile : IRandomFileSource
        {
            readonly byte[] _data;
            readonly Func<long, int, Exception> _failAt;
            public int ReadCount;

            public MemoryFile(byte[] data, Func<long, int, Exception> failAt = null)
            {
                _data = data;
                _failAt = failAt;
            }

            public long Length => _data.Length;

            public int Read(long offset, Span<byte> destination)
            {
                Interlocked.Increment(ref ReadCount);
                var ex = _failAt?.Invoke(offset, destination.Length);
                if (ex != null) throw ex;
                if (offset >= _data.Length) return 0;
                int n = (int)Math.Min(destination.Length, _data.Length - offset);
                _data.AsSpan((int)offset, n).CopyTo(destination);
                return n;
            }

            public void Dispose() { }
        }

        sealed class MemorySession : ISectionSession
        {
            readonly Dictionary<string, byte[]> _files;
            readonly Func<string, long, int, Exception> _fail;

            public MemorySession(Dictionary<string, byte[]> files, Func<string, long, int, Exception> fail)
            {
                _files = files;
                _fail = fail;
            }

            public IRandomFileSource OpenFile(string relativePath)
            {
                if (!_files.TryGetValue(relativePath, out var data))
                    throw new FileNotFoundException(relativePath);
                return new MemoryFile(data, (o, n) => _fail?.Invoke(relativePath, o, n));
            }

            public void Dispose() { }
        }

        sealed class MemoryFactory : ISectionSourceFactory
        {
            readonly Dictionary<string, byte[]> _files;
            readonly List<string> _dirs;
            readonly Func<string, long, int, Exception> _fail;
            public int SessionsOpened;

            public MemoryFactory(Dictionary<string, byte[]> files, IEnumerable<string> dirs = null,
                Func<string, long, int, Exception> fail = null)
            {
                _files = files;
                _dirs = dirs?.ToList() ?? new List<string>();
                _fail = fail;
            }

            public List<SourceEntry> Enumerate()
            {
                var list = new List<SourceEntry>();
                foreach (var d in _dirs)
                    list.Add(new SourceEntry { RelativePath = d, IsDirectory = true });
                foreach (var kv in _files)
                    list.Add(new SourceEntry { RelativePath = kv.Key, Size = kv.Value.Length, IsDirectory = false });
                return list;
            }

            public ISectionSession OpenSession()
            {
                Interlocked.Increment(ref SessionsOpened);
                return new MemorySession(_files, _fail);
            }
        }

        sealed class TempTree : IDisposable
        {
            public string Root { get; }
            public TempTree()
            {
                Root = Path.Combine(Path.GetTempPath(), "botw_copier_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }
            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { }
            }
        }

        static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            var rnd = new Random(seed);
            rnd.NextBytes(data);
            return data;
        }

        static Dictionary<string, string> ReadTree(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                map[rel] = Convert.ToBase64String(File.ReadAllBytes(f));
            }
            return map;
        }

        static Dictionary<string, byte[]> SampleFiles()
        {
            return new Dictionary<string, byte[]>
            {
                ["a.bin"] = Pattern(1024, 1),
                ["empty.dat"] = Array.Empty<byte>(),
                ["dir/b.txt"] = Pattern(777, 2),
                ["dir/sub/c.txt"] = Pattern(5000, 3),
                ["中文/消息.txt"] = Encoding.UTF8.GetBytes("塞尔达传说 旷野之息"),
            };
        }

        [Fact]
        public void Copy_SmallFiles_MatchSourceBytesAndStructure()
        {
            using var tree = new TempTree();
            var files = SampleFiles();
            var factory = new MemoryFactory(files, new[] { "emptydir", "dir/sub" });
            var result = ParallelSectionCopier.Copy(
                factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 4 },
                null, CancellationToken.None);

            Assert.Equal(files.Count, result.FileCount);
            Assert.Equal(files.Values.Sum(v => (long)v.Length), result.ByteCount);
            Assert.True(Directory.Exists(Path.Combine(tree.Root, "emptydir")));
            Assert.True(Directory.Exists(Path.Combine(tree.Root, "dir", "sub")));
            var onDisk = ReadTree(tree.Root);
            Assert.Equal(files.Count, onDisk.Count);
            foreach (var kv in files)
                Assert.Equal(Convert.ToBase64String(kv.Value), onDisk[kv.Key]);
        }

        [Fact]
        public void Copy_LargeFile_IsSplitIntoChunksAndReassembled()
        {
            using var tree = new TempTree();
            var data = Pattern(300_000, 7);
            var factory = new MemoryFactory(new Dictionary<string, byte[]> { ["big.bin"] = data });
            var result = ParallelSectionCopier.Copy(
                factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 4, ChunkThresholdBytes = 64 * 1024 },
                null, CancellationToken.None);

            Assert.Equal(300_000, result.ByteCount);
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(tree.Root, "big.bin")));
            // 300 KB / 64 KB threshold -> 5 chunks, capped at parallelism*2 = 8
            Assert.True(result.WorkersUsed > 1);
        }

        [Fact]
        public void Copy_ExactChunkMultiple_NoTrailingGarbage()
        {
            using var tree = new TempTree();
            var data = Pattern(256 * 1024, 11); // exactly 4 x 64 KB
            var factory = new MemoryFactory(new Dictionary<string, byte[]> { ["edge.bin"] = data });
            ParallelSectionCopier.Copy(
                factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 3, ChunkThresholdBytes = 64 * 1024 },
                null, CancellationToken.None);

            Assert.Equal(data, File.ReadAllBytes(Path.Combine(tree.Root, "edge.bin")));
        }

        [Fact]
        public void Copy_SerialAndParallel_ProduceIdenticalTrees()
        {
            var files = SampleFiles();
            var big = Pattern(400_000, 21);
            files["big.bin"] = big;

            using var serial = new TempTree();
            using var parallel = new TempTree();
            var f1 = new MemoryFactory(files);
            var f2 = new MemoryFactory(files);

            ParallelSectionCopier.Copy(f1.Enumerate(), serial.Root, f1,
                new ParallelCopyOptions { Parallelism = 1, ChunkThresholdBytes = 64 * 1024 },
                null, CancellationToken.None);
            ParallelSectionCopier.Copy(f2.Enumerate(), parallel.Root, f2,
                new ParallelCopyOptions { Parallelism = 6, ChunkThresholdBytes = 64 * 1024 },
                null, CancellationToken.None);

            var a = ReadTree(serial.Root);
            var b = ReadTree(parallel.Root);
            Assert.Equal(a.Count, b.Count);
            foreach (var kv in a)
            {
                Assert.True(b.ContainsKey(kv.Key), "missing: " + kv.Key);
                Assert.Equal(kv.Value, b[kv.Key]);
            }
        }

        [Fact]
        public void Copy_AlreadyCancelled_Throws()
        {
            using var tree = new TempTree();
            var factory = new MemoryFactory(SampleFiles());
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                    new ParallelCopyOptions { Parallelism = 4 }, null, cts.Token));
        }

        [Fact]
        public void Copy_CancelledDuringCopy_ThrowsAndStops()
        {
            using var tree = new TempTree();
            var files = new Dictionary<string, byte[]>();
            for (int i = 0; i < 40; i++)
                files["f" + i + ".bin"] = Pattern(256 * 1024, 100 + i);

            using var cts = new CancellationTokenSource();
            long seen = 0;
            var factory = new MemoryFactory(files, fail: (path, offset, len) =>
            {
                if (Interlocked.Increment(ref seen) == 6) cts.Cancel();
                return null;
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
                ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                    new ParallelCopyOptions { Parallelism = 2 }, null, cts.Token));
        }

        [Fact]
        public void Copy_SourceFailure_IsPropagated()
        {
            using var tree = new TempTree();
            var files = new Dictionary<string, byte[]>
            {
                ["ok.bin"] = Pattern(1024, 1),
                ["bad.bin"] = Pattern(1024, 2),
            };
            var factory = new MemoryFactory(files, fail: (path, offset, len) =>
                path == "bad.bin" ? new IOException("simulated read failure") : null);

            var ex = Assert.ThrowsAny<Exception>(() =>
                ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                    new ParallelCopyOptions { Parallelism = 3 }, null, CancellationToken.None));
            Assert.Contains("simulated read failure", ex.Message);
        }

        [Fact]
        public void Copy_EmptyEntryList_IsNoOp()
        {
            using var tree = new TempTree();
            var factory = new MemoryFactory(new Dictionary<string, byte[]>());
            var result = ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 4 }, null, CancellationToken.None);
            Assert.Equal(0, result.FileCount);
            Assert.Equal(0, result.ByteCount);
            Assert.Equal(0, result.WorkersUsed);
        }

        [Fact]
        public void Copy_OneSessionPerWorker_AtMost()
        {
            using var tree = new TempTree();
            var files = new Dictionary<string, byte[]>();
            for (int i = 0; i < 24; i++)
                files["f" + i] = Pattern(2048, i);

            var factory = new MemoryFactory(files);
            var result = ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 4 }, null, CancellationToken.None);

            Assert.Equal(4, result.WorkersUsed);
            Assert.InRange(factory.SessionsOpened, 1, 4);
            Assert.Equal(files.Count, ReadTree(tree.Root).Count);
        }

        [Fact]
        public void Copy_ProgressCallback_ReportsFinalTotals()
        {
            using var tree = new TempTree();
            var files = SampleFiles();
            var factory = new MemoryFactory(files);
            long lastFiles = -1, lastBytes = -1;
            ParallelSectionCopier.Copy(factory.Enumerate(), tree.Root, factory,
                new ParallelCopyOptions { Parallelism = 2, ProgressIntervalBytes = 1 << 20 },
                (f, b) => { lastFiles = f; lastBytes = b; }, CancellationToken.None);

            Assert.Equal(files.Count, lastFiles);
            Assert.Equal(files.Values.Sum(v => (long)v.Length), lastBytes);
        }
    }
}
