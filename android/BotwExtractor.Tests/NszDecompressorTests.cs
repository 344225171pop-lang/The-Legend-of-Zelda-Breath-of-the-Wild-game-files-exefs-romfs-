using System;
using System.IO;
using System.Text;
using Extractor.Core;
using Xunit;

namespace BotwExtractor.Tests
{
    // Defensive-header tests for the NSZ/NCZ decompressor: containers are
    // third-party input, so a corrupt or hostile header must fail fast with a
    // clear InvalidDataException instead of allocating wildly or crashing.
    public class NszDecompressorTests
    {
        const int HeaderSize = 0x4000;

        static byte[] BuildNcz(byte[] sectionTable, string magic = "NCZSECTN")
        {
            var ms = new MemoryStream();
            ms.Write(new byte[HeaderSize], 0, HeaderSize);
            var m = Encoding.ASCII.GetBytes(magic);
            ms.Write(m, 0, m.Length);
            ms.Write(sectionTable, 0, sectionTable.Length);
            return ms.ToArray();
        }

        static byte[] Int64(long v) => BitConverter.GetBytes(v);

        static string TempOutputPath() =>
            Path.Combine(Path.GetTempPath(), "botw_nsz_" + Guid.NewGuid().ToString("N") + ".nca");

        static InvalidDataException DecompressExpectingFailure(byte[] data)
        {
            string outPath = TempOutputPath();
            try
            {
                using var ms = new MemoryStream(data);
                return Assert.Throws<InvalidDataException>(() =>
                    NszDecompressor.Decompress(ms, outPath, null, default));
            }
            finally
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Decompress_MissingMagic_Throws()
        {
            var bad = new byte[HeaderSize + 8]; // zeroed 0x4000 header, no NCZSECTN
            bad[HeaderSize] = (byte)'X';
            DecompressExpectingFailure(bad);
        }

        [Fact]
        public void Decompress_ExcessiveSectionCount_Throws()
        {
            var ex = DecompressExpectingFailure(BuildNcz(Int64(1_000_000_000)));
            Assert.Contains("section count", ex.Message);
        }

        [Fact]
        public void Decompress_ZeroSectionCount_Throws()
        {
            DecompressExpectingFailure(BuildNcz(Int64(0)));
        }

        [Fact]
        public void Decompress_TruncatedSectionTable_Throws()
        {
            // Claims 2 sections but supplies only a partial first entry.
            var table = new byte[8 + 8 + 8]; // count is prepended by the helper? no:
            var data = new MemoryStream();
            data.Write(new byte[HeaderSize], 0, HeaderSize);
            var m = Encoding.ASCII.GetBytes("NCZSECTN");
            data.Write(m, 0, m.Length);
            data.Write(Int64(2), 0, 8);
            data.Write(table, 0, table.Length);
            var bytes = data.ToArray();
            Assert.ThrowsAny<Exception>(() =>
            {
                using var ms = new MemoryStream(bytes);
                NszDecompressor.Decompress(ms, TempOutputPath(), null, default);
            });
        }

        [Fact]
        public void IsNsz_PlainNca_ReturnsFalse()
        {
            string path = Path.Combine(Path.GetTempPath(), "botw_plain_" + Guid.NewGuid().ToString("N") + ".nca");
            try
            {
                File.WriteAllBytes(path, new byte[HeaderSize + 0x100]);
                Assert.False(NszDecompressor.IsNsz(path));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
