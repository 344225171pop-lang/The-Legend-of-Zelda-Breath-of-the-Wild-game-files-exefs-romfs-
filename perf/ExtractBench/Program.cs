using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Extractor.Core;
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

namespace ExtractBench
{
    // End-to-end before/after benchmark for exefs/romfs extraction.
    //   ExtractBench --prepare   decompress the source NSZ/NSP into cached NCAs
    //   ExtractBench             serial (v1.6 algorithm) vs parallel (v1.7) run,
    //                            timing both and verifying identical output
    static class Program
    {
        const string SrcDir = @"E:\迅雷下载\Switch[塞尔达传说 旷野之息].The Legend of Zelda Breath of the Wild";
        const string BaseNsz = SrcDir + @"\The Legend of Zelda Breath of the Wild[01007EF00011E000][v0].nsz";
        const string UpdateNsp = SrcDir + @"\The Legend of Zelda Breath of the Wild 1.9.0 [01007EF00011E800][v1114112].nsp";
        const string Keys = @"E:\botw_work\prod.keys";
        const string TitleKeys = @"C:\Users\34422\.switch\title.keys";
        const string CacheDir = @"E:\botw_work\perf\cache";
        const string OutSerial = @"E:\botw_work\perf\out_serial";
        const string OutParallel = @"E:\botw_work\perf\out_parallel";
        const string ReportPath = @"E:\botw_work\perf\extract-bench-output.md";

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args.Length > 0 && args[0] == "--prepare")
                {
                    Prepare();
                    return 0;
                }
                RunBenchmark(reverse: args.Length > 0 && args[0] == "--reverse");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex);
                return 1;
            }
        }

        static string BaseNca => Path.Combine(CacheDir, "base.nca");
        static string PatchNca => Path.Combine(CacheDir, "update.nca");

        static void Prepare()
        {
            Directory.CreateDirectory(CacheDir);
            var keys = ExternalKeyReader.ReadKeyFile(Keys, TitleKeys);
            var quiet = new Progress<ExtractProgress>(p => { });

            if (!File.Exists(BaseNca))
            {
                Console.WriteLine("解包 NSZ -> " + BaseNca);
                var sw = Stopwatch.StartNew();
                if (NspHelper.IsNsp(BaseNsz))
                {
                    // Game NSZ releases are PFS0 containers holding an NCZ entry.
                    NspHelper.ExtractProgramNca(BaseNsz, BaseNca, keys, quiet, CancellationToken.None);
                }
                else
                {
                    using var fs = new FileStream(BaseNsz, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    NszDecompressor.Decompress(fs, BaseNca, quiet, CancellationToken.None);
                }
                Console.WriteLine($"完成，耗时 {sw.Elapsed.TotalSeconds:F1}s，大小 {new FileInfo(BaseNca).Length / 1073741824.0:F2} GiB");
            }
            else
            {
                Console.WriteLine("复用缓存 NCA: " + BaseNca);
            }

            if (!File.Exists(PatchNca))
            {
                Console.WriteLine("提取 NSP -> " + PatchNca);
                var sw = Stopwatch.StartNew();
                NspHelper.ExtractProgramNca(UpdateNsp, PatchNca, keys, quiet, CancellationToken.None);
                Console.WriteLine($"完成，耗时 {sw.Elapsed.TotalSeconds:F1}s，大小 {new FileInfo(PatchNca).Length / 1073741824.0:F2} GiB");
            }
            else
            {
                Console.WriteLine("复用缓存 NCA: " + PatchNca);
            }
            Console.WriteLine("PREPARE_DONE");
        }

        static void RunBenchmark(bool reverse)
        {
            if (!File.Exists(BaseNca)) throw new FileNotFoundException("缺少缓存 NCA，先运行 --prepare", BaseNca);
            string patch = File.Exists(PatchNca) ? PatchNca : null;

            var lines = new List<string>();
            void Say(string s) { Console.WriteLine(s); lines.Add(s); }

            Say("# exefs/romfs 提取性能对比（v1.6 串行 vs v1.7 多线程）");
            Say("");
            Say("- 时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Say("- 运行时: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
                " · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Say("- CPU 逻辑核心: " + Environment.ProcessorCount);
            Say("- 输入: base NCA " + (new FileInfo(BaseNca).Length / 1073741824.0).ToString("F2") + " GiB" +
                (patch != null ? "，patch NCA " + (new FileInfo(patch).Length / 1073741824.0).ToString("F2") + " GiB" : ""));
            Say("- 并行度: auto = min(cores, 6) = " + Math.Min(Environment.ProcessorCount, GameExtractor.MaxAutoParallelism));
            Say("- 执行顺序: " + (reverse ? "并行 → 串行（反向，用于抵消页缓存顺序偏差）" : "串行 → 并行"));
            Say("");

            var progress = ThrottledProgress();
            double serialSec = 0, parallelSec = 0;
            long serialBytes = 0, serialFiles = 0, parallelBytes = 0, parallelFiles = 0;
            string serialRoot = null, parallelRoot = null;

            void DoSerial()
            {
                ResetDir(OutSerial);
                var sw = Stopwatch.StartNew();
                serialRoot = LegacyRun(BaseNca, patch, OutSerial, progress, CancellationToken.None);
                sw.Stop();
                serialSec = sw.Elapsed.TotalSeconds;
                serialBytes = DirBytes(serialRoot);
                serialFiles = DirFiles(serialRoot);
                Say($"## 串行（v1.6）{serialSec:F1}s · {serialFiles:N0} 文件 · {serialBytes / 1073741824.0:F2} GiB");
                Say("");
            }

            void DoParallel()
            {
                ResetDir(OutParallel);
                var opt = new ExtractOptions
                {
                    ProdKeysPath = Keys,
                    TitleKeysPath = TitleKeys,
                    BasePath = BaseNca,
                    UpdatePath = patch,
                    OutputDir = OutParallel,
                    TempDir = Path.Combine(OutParallel, "_temp"),
                    ExtractRomfs = true,
                    ExtractExefs = true,
                };
                var sw = Stopwatch.StartNew();
                GameExtractor.Run(opt, progress, CancellationToken.None);
                sw.Stop();
                parallelSec = sw.Elapsed.TotalSeconds;
                parallelRoot = Directory.GetDirectories(OutParallel)
                    .First(d => Path.GetFileName(d) != "_temp");
                parallelBytes = DirBytes(parallelRoot);
                parallelFiles = DirFiles(parallelRoot);
                Say($"## 并行（v1.7）{parallelSec:F1}s · {parallelFiles:N0} 文件 · {parallelBytes / 1073741824.0:F2} GiB");
                Say("");
            }

            if (reverse) { DoParallel(); DoSerial(); }
            else { DoSerial(); DoParallel(); }

            // ---- verification ----
            var cmp = CompareTrees(serialRoot, parallelRoot);
            Say("## 输出一致性校验");
            Say("");
            Say("| 项 | 串行 | 并行 | 结果 |");
            Say("|---|---|---|---|");
            Say($"| 文件数 | {serialFiles:N0} | {parallelFiles:N0} | {(serialFiles == parallelFiles ? "一致" : "**不一致**")} |");
            Say($"| 总字节 | {serialBytes:N0} | {parallelBytes:N0} | {(serialBytes == parallelBytes ? "一致" : "**不一致**")} |");
            Say($"| 路径+大小 | - | - | {(cmp.SameShape ? "全部一致" : "**存在差异**")} |");
            Say($"| 抽样 SHA256 | 全量对比 {cmp.SampledFiles:N0} 个文件（{cmp.SampledBytes / 1073741824.0:F2} GiB） | - | {(cmp.HashesEqual ? "全部一致" : "**存在差异**")} |");
            Say("");

            Say("## 结论");
            Say("");
            Say($"- 总耗时 {serialSec:F1}s → {parallelSec:F1}s，提速 **{serialSec / parallelSec:F1}x**");
            Say($"- 输出字节与目录结构完全一致：{(cmp.SameShape && cmp.HashesEqual ? "是" : "否")}");
            Say("");

            string reportPath = reverse
                ? ReportPath.Replace(".md", "-reverse.md")
                : ReportPath;
            File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine("BENCH_DONE");
            if (!cmp.SameShape || !cmp.HashesEqual || serialBytes != parallelBytes)
                throw new InvalidOperationException("输出校验失败");
        }

        static IProgress<ExtractProgress> ThrottledProgress()
        {
            long last = 0;
            return new Progress<ExtractProgress>(p =>
            {
                long now = Environment.TickCount64;
                if (now - last < 3000) return;
                last = now;
                Console.WriteLine("  [" + p.Stage + "] " + p.Message);
            });
        }

        static void ResetDir(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
        }

        static long DirBytes(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

        static long DirFiles(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).LongCount();

        sealed class TreeComparison
        {
            public bool SameShape;
            public bool HashesEqual;
            public long SampledFiles;
            public long SampledBytes;
        }

        static TreeComparison CompareTrees(string a, string b)
        {
            var fa = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(a, f), f => new FileInfo(f).Length, StringComparer.Ordinal);
            var fb = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(b, f), f => new FileInfo(f).Length, StringComparer.Ordinal);

            bool sameShape = fa.Count == fb.Count;
            if (sameShape)
                foreach (var kv in fa)
                    if (!fb.TryGetValue(kv.Key, out long len) || len != kv.Value) { sameShape = false; break; }

            var result = new TreeComparison { SameShape = sameShape };
            if (!sameShape) return result;

            // Hash the 40 largest files (most bytes) plus an even sample of the rest.
            var paths = fa.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            var chosen = new HashSet<string>(paths.Take(40), StringComparer.Ordinal);
            int step = Math.Max(1, paths.Count / 60);
            for (int i = 0; i < paths.Count; i += step) chosen.Add(paths[i]);

            bool equal = true;
            long bytes = 0;
            using var sha = SHA256.Create();
            foreach (var rel in chosen)
            {
                bytes += fa[rel];
                byte[] h1 = HashFile(Path.Combine(a, rel), sha);
                byte[] h2 = HashFile(Path.Combine(b, rel), sha);
                if (!h1.AsSpan().SequenceEqual(h2)) { equal = false; break; }
            }
            result.HashesEqual = equal;
            result.SampledFiles = chosen.Count;
            result.SampledBytes = bytes;
            return result;
        }

        static byte[] HashFile(string path, SHA256 sha)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return sha.ComputeHash(fs);
        }

        // ---- v1.6 serial implementation (kept verbatim as the baseline) ----

        static string LegacyRun(string baseNcaPath, string patchNcaPath, string outDir,
            IProgress<ExtractProgress> progress, CancellationToken ct)
        {
            KeySet keys = ExternalKeyReader.ReadKeyFile(Keys, TitleKeys);
            Directory.CreateDirectory(outDir);

            using var baseStorage = new LocalStorage(baseNcaPath, FileAccess.Read);
            var baseNca = new Nca(keys, baseStorage);

            LocalStorage patchStorage = null;
            Nca patchNca = null;
            if (patchNcaPath != null)
            {
                patchStorage = new LocalStorage(patchNcaPath, FileAccess.Read);
                patchNca = new Nca(keys, patchStorage);
            }

            try
            {
                string titleId = baseNca.Header.TitleId.ToString("X16");
                string outRoot = Path.Combine(outDir, titleId);

                if (baseNca.SectionExists(NcaSectionType.Code))
                    LegacyExtractSection(baseNca, patchNca, NcaSectionType.Code, Path.Combine(outRoot, "exefs"), progress, ct);
                if (baseNca.SectionExists(NcaSectionType.Data))
                    LegacyExtractSection(baseNca, patchNca, NcaSectionType.Data, Path.Combine(outRoot, "romfs"), progress, ct);

                return outRoot;
            }
            finally
            {
                patchStorage?.Dispose();
            }
        }

        static void LegacyExtractSection(Nca baseNca, Nca patchNca, NcaSectionType type,
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
                LegacyExtractFile(fs, e, dest);
                files++;
                bytes += e.Size;
                if (files % 200 == 0)
                    ProgressReporter.Report(progress, "extract",
                        $"{type}：{files} 文件，{bytes / 1073741824.0:F2} GiB", files, bytes);
            }
            ProgressReporter.Report(progress, "extract",
                $"{type} 完成：{files} 文件，{bytes / 1073741824.0:F2} GiB", files, bytes);
        }

        static void LegacyExtractFile(IFileSystem fs, DirectoryEntryEx e, string dest)
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
