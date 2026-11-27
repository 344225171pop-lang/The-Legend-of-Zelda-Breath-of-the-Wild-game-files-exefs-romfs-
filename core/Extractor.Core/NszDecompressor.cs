using System;
using System.IO;
using System.Threading;
using ZstdSharp;

namespace Extractor.Core
{
    // Decompresses NSZ/NCZ (compressed NCA) back into a standard NCA.
    // Format reference: nsz IndependentNczDecompressorConcise.py
    public static class NszDecompressor
    {
        private sealed class Section
        {
            public long Offset;
            public long Size;
            public long CryptoType;
            public byte[] Key;
            public byte[] Counter;
        }

        public static bool IsNsz(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 0x4008) return false;
            fs.Seek(0x4000, SeekOrigin.Begin);
            Span<byte> magic = stackalloc byte[8];
            fs.ReadExactly(magic);
            return magic.SequenceEqual("NCZSECTN"u8);
        }

        // The caller owns the input stream; it is rewound to 0 here.
        public static void Decompress(Stream input, string outputPath,
            IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            Stream inFs = input;
            inFs.Seek(0, SeekOrigin.Begin);
            byte[] header = new byte[0x4000];
            inFs.ReadExactly(header);

            Span<byte> magic = stackalloc byte[8];
            inFs.ReadExactly(magic);
            if (!magic.SequenceEqual("NCZSECTN"u8))
                throw new InvalidDataException("Not an NCZ/NSZ file (missing NCZSECTN).");

            long sectionCount = ReadInt64(inFs);
            // Bound the table before allocating: a corrupt/hostile NSZ could
            // otherwise ask for billions of sections and exhaust memory.
            const long MaxSections = 4096;
            if (sectionCount <= 0 || sectionCount > MaxSections)
                throw new InvalidDataException("Unreasonable NCZ section count: " + sectionCount);
            var sections = new Section[sectionCount];
            for (int i = 0; i < sectionCount; i++) sections[i] = ReadSection(inFs);

            long posAfterTable = inFs.Position;
            inFs.ReadExactly(magic);
            bool block = magic.SequenceEqual("NCZBLOCK"u8);
            inFs.Seek(posAfterTable, SeekOrigin.Begin);

            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            outFs.Write(header);

            long totalData = sections.Length > 0
                ? sections[sections.Length - 1].Offset + sections[sections.Length - 1].Size - sections[0].Offset
                : 0;

            if (block)
                DecompressBlock(inFs, outFs, sections, totalData, progress, ct);
            else
                DecompressSolid(inFs, outFs, sections, totalData, progress, ct);
        }

        private static void DecompressBlock(Stream inFs, Stream outFs, Section[] sections,
            long totalData, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            Span<byte> magic = stackalloc byte[8];
            inFs.ReadExactly(magic); // NCZBLOCK
            ReadByte(inFs); // version
            ReadByte(inFs); // type
            ReadByte(inFs); // unused
            int exponent = ReadByte(inFs);
            int numberOfBlocks = ReadInt32(inFs);
            long decompressedSize = ReadInt64(inFs);
            int[] list = new int[numberOfBlocks];
            for (int i = 0; i < numberOfBlocks; i++) list[i] = ReadInt32(inFs);

            int blockSize = 1 << exponent;
            byte[] compBuf = new byte[blockSize];
            byte[] blockBuf = new byte[blockSize];
            using var dec = new Decompressor();

            long pos = sections[0].Offset;
            int sectionId = 0;
            Section s = sections[0];
            AesCtr crypto = null;
            int cryptoSectionId = -1;
            long written = 0;

            try
            {
                for (int b = 0; b < numberOfBlocks; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    int compSize = list[b];
                    inFs.ReadExactly(compBuf.AsSpan(0, compSize));
                    byte[] data;
                    int dataLen;
                    if (compSize < blockSize)
                    {
                        dataLen = dec.Unwrap(compBuf.AsSpan(0, compSize), blockBuf);
                        data = blockBuf;
                    }
                    else
                    {
                        dataLen = compSize;
                        data = compBuf;
                    }

                    long end = pos + dataLen;
                    int dataOffset = 0;
                    while (pos < end)
                    {
                        if (pos >= s.Offset + s.Size)
                        {
                            sectionId++;
                            s = sections[sectionId];
                        }
                        int partSize = (int)(Math.Min(end, s.Offset + s.Size) - pos);
                        if (s.CryptoType == 3 || s.CryptoType == 4)
                        {
                            if (cryptoSectionId != sectionId)
                            {
                                crypto?.Dispose();
                                crypto = new AesCtr(s.Key, s.Counter, s.Offset);
                                cryptoSectionId = sectionId;
                            }
                            crypto.CryptInPlace(data, dataOffset, partSize);
                        }
                        outFs.Write(data, dataOffset, partSize);
                        pos += partSize;
                        dataOffset += partSize;
                        written += partSize;
                    }

                    if ((b & 255) == 0)
                        ProgressReporter.Report(progress, "decompress",
                            $"解压 NSZ {written / 1048576.0:F0} / {totalData / 1048576.0:F0} MiB",
                            0, written, totalData > 0 ? written * 100.0 / totalData : -1);
                }
            }
            finally { crypto?.Dispose(); }

            ProgressReporter.Report(progress, "decompress",
                $"NSZ 解压完成 {written / 1048576.0:F0} MiB", 0, written, 100);
        }

        private static void DecompressSolid(Stream inFs, Stream outFs, Section[] sections,
            long totalData, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            using var decStream = new DecompressionStream(inFs, 1 << 20, false, true);
            byte[] buf = new byte[0x400000];
            long written = 0;
            long nextReport = 1 << 26;
            foreach (Section s in sections)
            {
                AesCtr crypto = (s.CryptoType == 3 || s.CryptoType == 4)
                    ? new AesCtr(s.Key, s.Counter, s.Offset) : null;
                try
                {
                    long i = s.Offset;
                    long end = s.Offset + s.Size;
                    while (i < end)
                    {
                        ct.ThrowIfCancellationRequested();
                        int chunkSz = (int)Math.Min(buf.Length, end - i);
                        int read = decStream.Read(buf, 0, chunkSz);
                        if (read <= 0) break;
                        crypto?.CryptInPlace(buf, 0, read);
                        outFs.Write(buf, 0, read);
                        i += read;
                        written += read;
                        if (written >= nextReport)
                        {
                            nextReport = written + (1 << 26);
                            ProgressReporter.Report(progress, "decompress",
                                $"解压 NSZ {written / 1048576.0:F0} MiB", 0, written,
                                totalData > 0 ? written * 100.0 / totalData : -1);
                        }
                    }
                }
                finally { crypto?.Dispose(); }
            }
            ProgressReporter.Report(progress, "decompress",
                $"NSZ 解压完成 {written / 1048576.0:F0} MiB", 0, written, 100);
        }

        private static Section ReadSection(Stream s)
        {
            var sec = new Section
            {
                Offset = ReadInt64(s),
                Size = ReadInt64(s),
                CryptoType = ReadInt64(s)
            };
            ReadInt64(s); // padding
            sec.Key = new byte[16]; s.ReadExactly(sec.Key);
            sec.Counter = new byte[16]; s.ReadExactly(sec.Counter);
            return sec;
        }

        private static long ReadInt64(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            s.ReadExactly(b);
            return BitConverter.ToInt64(b);
        }

        private static int ReadInt32(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return BitConverter.ToInt32(b);
        }

        private static byte ReadByte(Stream s)
        {
            int v = s.ReadByte();
            if (v < 0) throw new EndOfStreamException();
            return (byte)v;
        }
    }
}