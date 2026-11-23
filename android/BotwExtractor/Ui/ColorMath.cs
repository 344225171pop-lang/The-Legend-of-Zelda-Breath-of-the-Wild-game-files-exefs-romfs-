using System;

namespace BotwExtractor.Ui
{
    // Pure, Android-free color utilities shared by the app and the test project.
    // Implements the WCAG 2.x relative-luminance / contrast-ratio formulas so the
    // UI can guarantee accessible text-on-color pairs in both light and dark mode.
    public static class ColorMath
    {
        public const int Black = unchecked((int)0xFF000000);
        public const int White = unchecked((int)0xFFFFFFFF);

        public static int Rgb(int r, int g, int b)
        {
            r = Clamp8(r); g = Clamp8(g); b = Clamp8(b);
            return unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
        }

        public static int Red(int c) => (c >> 16) & 0xFF;
        public static int Green(int c) => (c >> 8) & 0xFF;
        public static int Blue(int c) => c & 0xFF;

        static int Clamp8(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        // Accepts #RGB, #RRGGBB and #AARRGGBB (alpha ignored). Returns null when invalid.
        public static int? ParseHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim();
            if (hex[0] == '#') hex = hex.Substring(1);
            if (hex.Length == 3)
            {
                var sb = new System.Text.StringBuilder(6);
                foreach (char ch in hex) { sb.Append(ch); sb.Append(ch); }
                hex = sb.ToString();
            }
            if (hex.Length == 8) hex = hex.Substring(2); // drop alpha
            if (hex.Length != 6) return null;
            foreach (char ch in hex)
            {
                bool ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!ok) return null;
            }
            int v = Convert.ToInt32(hex, 16);
            return Rgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        public static string ToHex(int c) => "#" + (c & 0xFFFFFF).ToString("X6");

        // WCAG 2.x relative luminance (sRGB).
        public static double RelativeLuminance(int c)
        {
            double Lin(int v8)
            {
                double s = v8 / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(Red(c)) + 0.7152 * Lin(Green(c)) + 0.0722 * Lin(Blue(c));
        }

        // WCAG contrast ratio in [1, 21].
        public static double ContrastRatio(int a, int b)
        {
            double la = RelativeLuminance(a), lb = RelativeLuminance(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        public static bool MeetsWcagAA(int foreground, int background) =>
            ContrastRatio(foreground, background) >= 4.5;

        // Picks black or white, whichever gives the higher contrast against bg.
        // The max of the two is always >= 4.5, so the result is WCAG AA for normal text.
        public static int OnColor(int background) =>
            ContrastRatio(White, background) >= ContrastRatio(Black, background) ? White : Black;

        // Linear blend: t=0 -> a, t=1 -> b.
        public static int Blend(int a, int b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            int r = (int)Math.Round(Red(a) + (Red(b) - Red(a)) * t);
            int g = (int)Math.Round(Green(a) + (Green(b) - Green(a)) * t);
            int bl = (int)Math.Round(Blue(a) + (Blue(b) - Blue(a)) * t);
            return Rgb(r, g, bl);
        }

        // Returns fg unchanged when it already meets min ratio against bg; otherwise
        // blends fg toward white (on dark bg) or black (on light bg) until it does.
        // Used for text drawn directly on the surface (e.g. section headers) so a
        // user-chosen dark primary stays readable in dark mode and vice versa.
        public static int EnsureContrast(int fg, int bg, double minRatio)
        {
            if (ContrastRatio(fg, bg) >= minRatio) return fg;
            int target = RelativeLuminance(bg) < 0.5 ? White : Black;
            for (double t = 0.05; t <= 1.0001; t += 0.05)
            {
                int c = Blend(fg, target, t);
                if (ContrastRatio(c, bg) >= minRatio) return c;
            }
            return target;
        }
    }
}
