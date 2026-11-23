using System;
using System.IO;
using System.Threading;
using LibHac;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Path = System.IO.Path;

namespace Extractor.Core
{
    public static class NspHelper
    {
        public static bool IsNsp(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return false;
            Span<byte> magic = stackalloc byte[4];
            fs.ReadExactly(magic);
            return magic.SequenceEqual("PFS0"u8);
        }

        // Extracts the Program NCA out of an NSP/NSZ (PFS0) container to destPath.
        // Entries may be plain .nca or compressed .ncz (NSZ); NCZ entries are
        // decompressed back into a standard NCA.
        public static string ExtractProgramNca(string nspPath, string destPath, KeySet keys,
            IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            using var storage = new LocalStorage(nspPath, FileAccess.Read);
            var pfs = new PartitionFileSystem();
            pfs.Initialize(storage).ThrowIfFailure();

            foreach (DirectoryEntryEx e in pfs.EnumerateEntries("/", "*", SearchOptions.Default))
            {
                ct.ThrowIfCancellationRequested();
                if (e.Type == DirectoryEntryType.Directory) continue;

                var fr = new UniqueRef<IFile>();
                pfs.OpenFile(ref fr, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                IFile f = fr.Get;

                bool isProgram;
                try
                {
                    var nca = new Nca(keys, new FileStorage(f));
                    isProgram = nca.Header.ContentType == NcaContentType.Program;
                }
                catch
                {
                    isProgram = false;
                }

                if (!isProgram)
                {
                    fr.Destroy();
                    continue;
                }

                f.GetSize(out long size).ThrowIfFailure();

                // NCZ entries keep the original encrypted NCA header at 0..0x4000,
                // followed by the NCZSECTN magic.
                bool isNcz = false;
                if (size >= 0x4008)
                {
                    byte[] magic = new byte[8];
                    f.Read(out long rd, 0x4000, magic).ThrowIfFailure();
                    isNcz = rd == 8 && magic.AsSpan().SequenceEqual("NCZSECTN"u8);
                }

                if (isNcz)
                {
                    ProgressReporter.Report(progress, "decompress",
                        $"解压容器内 NCZ：{Path.GetFileName(e.FullPath)}");
                    using var stream = new IFileAsStream(f);
                    NszDecompressor.Decompress(stream, destPath, progress, ct);
                }
                else
                {
                    using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    byte[] buf = new byte[1 << 20];
                    long offset = 0;
                    while (offset < size)
                    {
                        ct.ThrowIfCancellationRequested();
                        int toRead = (int)Math.Min(buf.Length, size - offset);
                        f.Read(out long read, offset, buf.AsSpan(0, toRead)).ThrowIfFailure();
                        if (read <= 0) throw new IOException("Short read while extracting NCA from NSP.");
                        outFs.Write(buf, 0, (int)read);
                        offset += read;
                        if ((offset & 0x3FFFFFF) == 0)
                            ProgressReporter.Report(progress, "extract",
                                $"提取 NCA {offset / 1048576.0:F0} / {size / 1048576.0:F0} MiB",
                                0, offset, size > 0 ? offset * 100.0 / size : -1);
                    }
                }
                fr.Destroy();
                return destPath;
            }

            throw new InvalidDataException("No Program NCA found in NSP: " + Path.GetFileName(nspPath));
        }
    }
}