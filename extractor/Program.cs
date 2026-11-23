using System;
using System.Diagnostics;
using System.IO;
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

class Program
{
    static int Main()
    {
        const string prodKeys = @"E:\botw_work\prod.keys";
        const string titleKeys = @"C:\Users\34422\.switch\title.keys";
        const string baseNcaPath = @"E:\botw_work\nsp\base\861707001401bbce36c7e421efac76d4.nca";
        const string patchNcaPath = @"E:\botw_work\nsp\update\94a679f15eb8b1a5ee59b8c284f71ae3.nca";
        const string outRoot = @"E:\botw_work\out\01007EF00011E000";

        var keySet = ExternalKeyReader.ReadKeyFile(prodKeys, titleKeys);
        using var baseStorage = new LocalStorage(baseNcaPath, FileAccess.Read);
        using var patchStorage = new LocalStorage(patchNcaPath, FileAccess.Read);
        var baseNca = new Nca(keySet, baseStorage);
        var patchNca = new Nca(keySet, patchStorage);

        Console.WriteLine($"Base : TitleId={baseNca.Header.TitleId:X16} Code={baseNca.SectionExists(NcaSectionType.Code)} Data={baseNca.SectionExists(NcaSectionType.Data)}");
        Console.WriteLine($"Patch: TitleId={patchNca.Header.TitleId:X16} Code={patchNca.SectionExists(NcaSectionType.Code)} Data={patchNca.SectionExists(NcaSectionType.Data)}");

        ExtractSection(baseNca, patchNca, NcaSectionType.Code, Path.Combine(outRoot, "exefs"));
        ExtractSection(baseNca, patchNca, NcaSectionType.Data, Path.Combine(outRoot, "romfs"));
        Console.WriteLine("ALL DONE");
        return 0;
    }

    static void ExtractSection(Nca baseNca, Nca patchNca, NcaSectionType type, string outDir)
    {
        Console.WriteLine($"--- {type} -> {outDir}");
        var sw = Stopwatch.StartNew();
        IFileSystem fs = patchNca.SectionExists(type)
            ? baseNca.OpenFileSystemWithPatch(patchNca, type, IntegrityCheckLevel.None)
            : baseNca.OpenFileSystem(type, IntegrityCheckLevel.None);

        Directory.CreateDirectory(outDir);
        long files = 0, bytes = 0;
        foreach (DirectoryEntryEx e in fs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
        {
            string rel = e.FullPath.TrimStart('/');
            string dest = Path.Combine(outDir, rel.Replace('/', '\\'));
            if (e.Type == DirectoryEntryType.Directory)
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            ExtractFile(fs, e, dest);
            files++; bytes += e.Size;
            if (files % 200 == 0)
                Console.WriteLine($"  {files} files, {bytes / 1073741824.0:F2} GiB, {sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine($"  DONE {type}: {files} files, {bytes / 1073741824.0:F2} GiB in {sw.Elapsed:mm\\:ss}");
    }

    static void ExtractFile(IFileSystem fs, DirectoryEntryEx e, string dest)
    {
        var fileRef = new UniqueRef<IFile>();
        fs.OpenFile(ref fileRef, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
        using (fileRef)
        using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            IFile f = fileRef.Get;
            long offset = 0, remaining = e.Size;
            byte[] buf = new byte[1024 * 1024];
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
