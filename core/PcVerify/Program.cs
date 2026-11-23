using System;
using System.Threading;
using Extractor.Core;

class Program
{
    static int Main()
    {
        const string src = @"E:\迅雷下载\Switch[塞尔达传说 旷野之息].The Legend of Zelda Breath of the Wild";
        var opt = new ExtractOptions
        {
            ProdKeysPath = @"E:\botw_work\prod.keys",
            TitleKeysPath = @"C:\Users\34422\.switch\title.keys",
            BasePath = src + @"\The Legend of Zelda Breath of the Wild[01007EF00011E000][v0].nsz",
            UpdatePath = src + @"\The Legend of Zelda Breath of the Wild 1.9.0 [01007EF00011E800][v1114112].nsp",
            OutputDir = @"E:\botw_work\out_android_test",
            TempDir = @"E:\botw_work\out_android_test_temp",
            ExtractRomfs = true,
            ExtractExefs = true,
        };
        var progress = new Progress<ExtractProgress>(p =>
            Console.WriteLine($"[{p.Stage}] {p.Message}"));
        try
        {
            GameExtractor.Run(opt, progress, CancellationToken.None);
            Console.WriteLine("VERIFY_RUN_DONE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
            return 1;
        }
    }
}