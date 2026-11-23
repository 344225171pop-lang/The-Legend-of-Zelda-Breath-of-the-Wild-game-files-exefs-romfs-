using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        // 0 = auto (min(logical cores, 6)). 1 forces the serial path, which is
        // useful for benchmarking and for very slow storage.
        public int Parallelism;
    }

    public static class GameExtractor
    {
        public const int MaxAutoParallelism = 6;

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

                string titleId;
                bool hasCode, hasData;
                using (var probeStorage = new LocalStorage(baseNcaPath, FileAccess.Read))
                {
                    var probe = new Nca(keys, probeStorage);
                    titleId = probe.Header.TitleId.ToString("X16");
                    hasCode = probe.SectionExists(NcaSectionType.Code);
                    hasData = probe.SectionExists(NcaSectionType.Data);
                }

                string outRoot = Path.Combine(opt.OutputDir, titleId);
                ProgressReporter.Report(progress, "extract",
                    $"Base TitleId={titleId} Code={hasCode} Data={hasData}");

                bool doExefs = opt.ExtractExefs && hasCode;
                bool doRomfs = opt.ExtractRomfs && hasData;
                if (opt.ExtractExefs && !hasCode)
                    ProgressReporter.Report(progress, "extract", "无 Code 段，跳过 exefs");
                if (opt.ExtractRomfs && !hasData)
                    ProgressReporter.Report(progress, "extract", "无 Data 段，跳过 romfs");

                int maxPar = opt.Parallelism > 0
                    ? opt.Parallelism
                    : Math.Min(Environment.ProcessorCount, MaxAutoParallelism);

                if (doExefs || doRomfs)
                {
                    // exefs and romfs use independent NCA handles, so they run
                    // simultaneously instead of one blocking the other. When both
                    // are requested the small exefs gets a smaller share.
                    int exefsPar = maxPar, romfsPar = maxPar;
                    if (doExefs && doRomfs)
                    {
                        exefsPar = Math.Max(1, Math.Min(2, maxPar / 2));
                        romfsPar = Math.Max(2, maxPar - exefsPar);
                    }

                    var jobs = new List<Task>(2);
                    if (doExefs)
                        jobs.Add(Task.Factory.StartNew(() => RunSection(keys, baseNcaPath, updateNcaPath,
                                NcaSectionType.Code, Path.Combine(outRoot, "exefs"), exefsPar, false, progress, ct),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                    if (doRomfs)
                        jobs.Add(Task.Factory.StartNew(() => RunSection(keys, baseNcaPath, updateNcaPath,
                                NcaSectionType.Data, Path.Combine(outRoot, "romfs"), romfsPar, true, progress, ct),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

                    try
                    {
                        Task.WaitAll(jobs.ToArray());
                    }
                    catch (AggregateException ae)
                    {
                        throw Unwrap(ae);
                    }
                }

                ProgressReporter.Report(progress, "done", $"完成：{outRoot}", 0, 0, 100);
            }
            finally
            {
                foreach (string t in tempFiles)
                {
                    try { File.Delete(t); } catch { }
                }
            }
        }

        static Exception Unwrap(AggregateException ae)
        {
            var flat = ae.Flatten().InnerExceptions;
            foreach (var e in flat)
                if (!(e is OperationCanceledException)) return e;
            return flat[0];
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

        private static void RunSection(KeySet keys, string baseNcaPath, string patchNcaPath,
            NcaSectionType type, string outDir, int parallelism, bool reportPercent,
            IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(outDir);
            var factory = new NcaSectionSourceFactory(keys, baseNcaPath, patchNcaPath, type);

            var entries = factory.Enumerate();
            long totalBytes = 0;
            foreach (var e in entries)
                if (!e.IsDirectory) totalBytes += e.Size;

            var result = ParallelSectionCopier.Copy(
                entries, outDir, factory,
                new ParallelCopyOptions { Parallelism = parallelism },
                (files, bytes) =>
                {
                    double percent = reportPercent && totalBytes > 0 ? bytes * 100.0 / totalBytes : -1;
                    ProgressReporter.Report(progress, "extract",
                        $"{type}：{files} 文件，{bytes / 1073741824.0:F2} GiB", files, bytes, percent);
                },
                ct);

            ProgressReporter.Report(progress, "extract",
                $"{type} 完成：{result.FileCount} 文件，{result.ByteCount / 1073741824.0:F2} GiB",
                result.FileCount, result.ByteCount);
        }

        // Independent NCA view used by one worker thread. Each session owns its
        // own LocalStorage/Nca/IFileSystem instances because LibHac file systems
        // are not thread-safe; separate instances are.
        private sealed class NcaSectionSourceFactory : ISectionSourceFactory
        {
            readonly KeySet _keys;
            readonly string _basePath;
            readonly string _patchPath;
            readonly NcaSectionType _type;

            public NcaSectionSourceFactory(KeySet keys, string basePath, string patchPath, NcaSectionType type)
            {
                _keys = keys;
                _basePath = basePath;
                _patchPath = patchPath;
                _type = type;
            }

            public List<SourceEntry> Enumerate()
            {
                using var session = (NcaSectionSession)OpenSession();
                var list = new List<SourceEntry>();
                foreach (DirectoryEntryEx e in session.FileSystem.EnumerateEntries(
                             "/", "*", SearchOptions.RecurseSubdirectories))
                {
                    list.Add(new SourceEntry
                    {
                        RelativePath = e.FullPath.TrimStart('/'),
                        Size = e.Type == DirectoryEntryType.Directory ? 0 : e.Size,
                        IsDirectory = e.Type == DirectoryEntryType.Directory,
                    });
                }
                return list;
            }

            public ISectionSession OpenSession()
            {
                var baseStorage = new LocalStorage(_basePath, FileAccess.Read);
                try
                {
                    var baseNca = new Nca(_keys, baseStorage);
                    LocalStorage patchStorage = null;
                    Nca patchNca = null;
                    try
                    {
                        if (!string.IsNullOrEmpty(_patchPath))
                        {
                            patchStorage = new LocalStorage(_patchPath, FileAccess.Read);
                            patchNca = new Nca(_keys, patchStorage);
                        }

                        IFileSystem fs = (patchNca != null && patchNca.SectionExists(_type))
                            ? baseNca.OpenFileSystemWithPatch(patchNca, _type, IntegrityCheckLevel.None)
                            : baseNca.OpenFileSystem(_type, IntegrityCheckLevel.None);

                        var session = new NcaSectionSession(fs, patchStorage, baseStorage);
                        patchStorage = null; // ownership moved to the session
                        baseStorage = null;
                        return session;
                    }
                    finally
                    {
                        patchStorage?.Dispose();
                    }
                }
                finally
                {
                    baseStorage?.Dispose();
                }
            }
        }

        private sealed class NcaSectionSession : ISectionSession
        {
            LocalStorage _patchStorage;
            LocalStorage _baseStorage;

            public NcaSectionSession(IFileSystem fs, LocalStorage patchStorage, LocalStorage baseStorage)
            {
                FileSystem = fs;
                _patchStorage = patchStorage;
                _baseStorage = baseStorage;
            }

            public IFileSystem FileSystem { get; private set; }

            public IRandomFileSource OpenFile(string relativePath)
            {
                var fileRef = new UniqueRef<IFile>();
                string path = "/" + (relativePath ?? "").TrimStart('/');
                FileSystem.OpenFile(ref fileRef, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                try
                {
                    fileRef.Get.GetSize(out long size).ThrowIfFailure();
                    return new LibHacFileSource(fileRef, size);
                }
                catch
                {
                    fileRef.Destroy();
                    throw;
                }
            }

            public void Dispose()
            {
                try { FileSystem?.Dispose(); } catch { }
                try { _patchStorage?.Dispose(); } catch { }
                try { _baseStorage?.Dispose(); } catch { }
                FileSystem = null;
                _patchStorage = null;
                _baseStorage = null;
            }
        }

        private sealed class LibHacFileSource : IRandomFileSource
        {
            UniqueRef<IFile> _fileRef;
            bool _disposed;

            public LibHacFileSource(UniqueRef<IFile> fileRef, long length)
            {
                _fileRef = fileRef;
                Length = length;
            }

            public long Length { get; }

            public int Read(long offset, Span<byte> destination)
            {
                _fileRef.Get.Read(out long read, offset, destination).ThrowIfFailure();
                return (int)read;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _fileRef.Destroy();
            }
        }
    }
}
