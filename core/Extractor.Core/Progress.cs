using System;

namespace Extractor.Core
{
    public sealed class ExtractProgress
    {
        public string Stage;
        public string Message;
        public long FilesDone;
        public long BytesDone;
        public double Percent = -1;
    }

    public static class ProgressReporter
    {
        public static void Report(IProgress<ExtractProgress> p, string stage, string message,
            long files = 0, long bytes = 0, double percent = -1)
        {
            p?.Report(new ExtractProgress
            {
                Stage = stage,
                Message = message,
                FilesDone = files,
                BytesDone = bytes,
                Percent = percent
            });
        }
    }
}