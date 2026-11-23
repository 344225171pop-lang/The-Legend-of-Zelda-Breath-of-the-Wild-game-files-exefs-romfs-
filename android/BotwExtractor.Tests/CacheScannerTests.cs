using System;
using System.IO;
using System.Linq;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    public class CacheScannerTests : IDisposable
    {
        readonly string _root;
        public CacheScannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "botw_cache_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string MakeDir(string name, params int[] fileSizes)
        {
            string d = Path.Combine(_root, name);
            Directory.CreateDirectory(d);
            for (int i = 0; i < fileSizes.Length; i++)
                File.WriteAllBytes(Path.Combine(d, "f" + i + ".bin"), new byte[fileSizes[i]]);
            return d;
        }

        [Fact]
        public void DirectorySize_SumsRecursively()
        {
            string d = MakeDir("a", 100, 200);
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllBytes(Path.Combine(d, "sub", "g.bin"), new byte[300]);
            Assert.Equal(600, CacheScanner.DirectorySize(d));
        }

        [Fact]
        public void DirectorySize_Missing_ReturnsZero() =>
            Assert.Equal(0, CacheScanner.DirectorySize(Path.Combine(_root, "nope")));

        [Fact]
        public void Scan_ProtectedItem_NotCleanable()
        {
            string d = MakeDir("temp", 50);
            var items = CacheScanner.Scan(new[]
            {
                new CacheCandidate { Key = "temp", Label = "临时", Path = d, Protected = true },
            });
            Assert.Single(items);
            Assert.Equal(50, items[0].SizeBytes);
            Assert.False(items[0].Cleanable);
            Assert.NotNull(items[0].SkipReason);
        }

        [Fact]
        public void Scan_MissingDir_NotCleanable()
        {
            var items = CacheScanner.Scan(new[]
            {
                new CacheCandidate { Key = "x", Label = "缺失", Path = Path.Combine(_root, "gone") },
            });
            Assert.False(items[0].Cleanable);
            Assert.Equal(0, items[0].SizeBytes);
        }

        [Fact]
        public void Clean_RemovesOnlyCleanable_KeepsRoots_AndProtected()
        {
            string temp = MakeDir("temp", 100, 100);   // protected
            string cache = MakeDir("cache", 200, 300); // cleanable
            var items = CacheScanner.Scan(new[]
            {
                new CacheCandidate { Key = "temp", Label = "临时", Path = temp, Protected = true },
                new CacheCandidate { Key = "cache", Label = "缓存", Path = cache },
            });
            var res = CacheScanner.Clean(items);
            Assert.True(res.Success);
            Assert.Equal(500, res.FreedBytes);
            Assert.Equal(2, res.RemovedFiles);
            Assert.True(Directory.Exists(cache));
            Assert.Empty(Directory.EnumerateFiles(cache));
            // protected untouched
            Assert.Equal(200, CacheScanner.DirectorySize(temp));
        }

        [Fact]
        public void Clean_EmptyList_ReturnsZero()
        {
            var res = CacheScanner.Clean(Array.Empty<CacheItem>());
            Assert.Equal(0, res.FreedBytes);
            Assert.True(res.Success);
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(2048, "2 KB")]
        [InlineData(5 * 1024 * 1024, "5 MB")]
        public void FormatSize_Thresholds(long bytes, string expected) =>
            Assert.Equal(expected, CacheScanner.FormatSize(bytes));
    }
}
