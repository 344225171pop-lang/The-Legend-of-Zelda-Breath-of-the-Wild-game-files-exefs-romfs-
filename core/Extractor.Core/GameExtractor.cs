using System;
using System.Collections.Generic;
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
    public sealed class ExtractOptions
    {
        public string ProdKeysPath;
        public string TitleKeysPath;
        public string BasePath;
        public string UpdatePath;
        public string OutputDir;
        public string TempDir;
        public bool ExtractRomfs = true;
        public bool ExtractExefs = true;
    }

    public static class GameExtractor
    {
        public static void Run(ExtractOptions opt, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(opt.ProdKeysPath)) throw new ArgumentException("缺少 prod.keys");
            if (string.IsNullOrEmpty(opt.BasePath)) throw new ArgumentException("缺少输入文件");
            if (string.IsNullOrEmpty(opt.OutputDir)) throw new ArgumentException("缺少输出目录");

            KeySet keys = ExternalKeyReader.ReadKeyFile(opt.ProdKeysPath, opt.TitleKeysPath);
            Directory.CreateDirectory(opt.OutputDir);
            string tempDir = string.IsNullOrEmpty(opt.TempDir) ? Path.Combine(opt.OutputDir, "_temp") : opt.TempDir;
            Directory.CreateDirectory(tempDir);

            var tempFiles = new List<string>();
            try
            {
                string baseNcaPath = ResolveNca(opt.BasePath, tempDir, keys, progress, ct, tempFiles, "base");
                string updateNcaPath = string.IsNullOrEmpty(opt.UpdatePath)
                    ? null
                    : ResolveNca(opt.UpdatePath, tempDir, keys, progress, ct, tempFiles, "update");

                using var baseStorage = new LocalStorage(baseNcaPath, FileAccess.Read);
                var baseNca = new Nca(keys, baseStorage);

                LocalStorage patchStorage = null;
                Nca patchNca = null;
                if (updateNcaPath != null)
                {
                    patchStorage = new LocalStorage(updateNcaPath, FileAccess.Read);
                    patchNca = new Nca(keys, patchStorage);
                }

                try
                {
                    string titleId = baseNca.Header.TitleId.ToString("X16");
                    string outRoot = Path.Combine(opt.OutputDir, titleId);
                    ProgressReporter.Report(progress, "extract",
                        $"Base TitleId={titleId} Code={baseNca.SectionExists(NcaSectionType.Code)} Data={baseNca.SectionExists(NcaSectionType.Data)}");

                    if (opt.ExtractExefs && baseNca.SectionExists(NcaSectionType.Code))
                        ExtractSection(baseNca, patchNca, NcaSectionType.Code, Path.Combine(outRoot, "exefs"), progress, ct);
                    else if (opt.ExtractExefs)
                        ProgressReporter.Report(progress, "extract", "无 Code 段，跳过 exefs");

                    if (opt.ExtractRomfs && baseNca.SectionExists(NcaSectionType.Data))
                        ExtractSection(baseNca, patchNca, NcaSectionType.Data, Path.Combine(outRoot, "romfs"), progress, ct);
                    else if (opt.ExtractRomfs)
                        ProgressReporter.Report(progress, "extract", "无 Data 段，跳过 romfs");

                    ProgressReporter.Report(progress, "done", $"完成：{outRoot}", 0, 0, 100);
                }
                finally
                {
                    patchStorage?.Dispose();
                }
            }
            finally
            {
                foreach (string t in tempFiles)
                {
                    try { File.Delete(t); } catch { }
                }
            }
        }

        private static string ResolveNca(string input, string tempDir, KeySet keys,
            IProgress<ExtractProgress> progress, CancellationToken ct, List<string> tempFiles, string tag)
        {
            // .nsz files are usually PFS0 containers holding .ncz entries, so
            // check for the container first; a standalone NCZ is handled next.
            if (NspHelper.IsNsp(input))
            {
                string dest = Path.Combine(tempDir, tag + ".nca");
                ProgressReporter.Report(progress, "extract", $"从 NSP/NSZ 容器提取 NCA：{Path.GetFileName(input)}");
                NspHelper.ExtractProgramNca(input, dest, keys, progress, ct);
                tempFiles.Add(dest);
                return dest;
            }
            if (NszDecompressor.IsNsz(input))
            {
                string dest = Path.Combine(tempDir, tag + ".nca");
                ProgressReporter.Report(progress, "decompress", $"解压 NSZ：{Path.GetFileName(input)}");
                using var inFs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                NszDecompressor.Decompress(inFs, dest, progress, ct);
                tempFiles.Add(dest);
                return dest;
            }
            return input; // already a raw NCA
        }

        private static void ExtractSection(Nca baseNca, Nca patchNca, NcaSectionType type,
            string outDir, IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            IFileSystem fs = (patchNca != null && patchNca.SectionExists(type))
                ? baseNca.OpenFileSystemWithPatch(patchNca, type, IntegrityCheckLevel.None)
                : baseNca.OpenFileSystem(type, IntegrityCheckLevel.None);

            Directory.CreateDirectory(outDir);
            long files = 0, bytes = 0;
            foreach (DirectoryEntryEx e in fs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = e.FullPath.TrimStart('/');
                string dest = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (e.Type == DirectoryEntryType.Directory)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                ExtractFile(fs, e, dest);
                files++;
                bytes += e.Size;
                if (files % 200 == 0)
                    ProgressReporter.Report(progress, "extract",
                        $"{type}：{files} 文件，{bytes / 1073741824.0:F2} GiB", files, bytes);
            }
            ProgressReporter.Report(progress, "extract",
                $"{type} 完成：{files} 文件，{bytes / 1073741824.0:F2} GiB", files, bytes);
        }

        private static void ExtractFile(IFileSystem fs, DirectoryEntryEx e, string dest)
        {
            var fileRef = new UniqueRef<IFile>();
            fs.OpenFile(ref fileRef, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            using (fileRef)
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                IFile f = fileRef.Get;
                long offset = 0, remaining = e.Size;
                byte[] buf = new byte[1 << 20];
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    f.Read(out long read, offset, buf.AsSpan(0, toRead)).ThrowIfFailure();
                    if (read <= 0) throw new IOException($"Short read: {e.FullPath}");
                    output.Write(buf, 0, (int)read);
                    offset += read;
                    remaining -= read;
                }
            }
        }
    }
}