using System;

namespace BotwExtractor.Ui
{
    // Abstraction over persistent key/value storage so the same logic runs against
    // Android SharedPreferences in the app and an in-memory dictionary in tests.
    public interface ISettingsStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    public sealed class ThemePalette
    {
        public int Primary { get; set; }
        public int Secondary { get; set; }
        public int Tertiary { get; set; }

        public ThemePalette Clone() => new ThemePalette { Primary = Primary, Secondary = Secondary, Tertiary = Tertiary };
    }

    public static class NightMode
    {
        public const string System = "system";
        public const string Light = "light";
        public const string Dark = "dark";

        public static string Normalize(string v) =>
            v == Light ? Light : v == Dark ? Dark : System;

        public static string Describe(string v) => Normalize(v) switch
        {
            Light => "浅色",
            Dark => "深色",
            _ => "跟随系统",
        };

        // Values match AndroidX AppCompatDelegate.ModeNight* so the app can assign
        // them directly without this layer referencing Android.
        public static int ToAppCompatMode(string v) => Normalize(v) switch
        {
            Light => 1,   // MODE_NIGHT_NO
            Dark => 2,    // MODE_NIGHT_YES
            _ => -1,      // MODE_NIGHT_FOLLOW_SYSTEM
        };
    }

    public sealed class ThemePrefs
    {
        public const string KeyNightMode = "theme_night_mode";
        public const string KeyCustomFlag = "theme_custom_palette";
        public const string KeyPalette = "theme_palette";

        // Baseline "Sheikah teal" defaults (light). Dark mode uses the DayNight
        // resource palette unless the user has saved a custom palette.
        public static ThemePalette DefaultPalette() => new ThemePalette
        {
            Primary = unchecked((int)0xFF00696D),
            Secondary = unchecked((int)0xFF4A6364),
            Tertiary = unchecked((int)0xFF4D616C),
        };

        readonly ISettingsStore _store;
        public ThemePrefs(ISettingsStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }

        public string GetNightMode() => NightMode.Normalize(_store.Get(KeyNightMode));
        public void SetNightMode(string mode) => _store.Set(KeyNightMode, NightMode.Normalize(mode));

        public bool HasCustomPalette => _store.Get(KeyCustomFlag) == "1";

        public ThemePalette GetPalette()
        {
            if (HasCustomPalette && TryParse(_store.Get(KeyPalette), out var p)) return p;
            return DefaultPalette();
        }

        public void SetPalette(ThemePalette p)
        {
            if (p == null) return;
            _store.Set(KeyPalette, Serialize(p));
            _store.Set(KeyCustomFlag, "1");
        }

        public void ResetPalette()
        {
            _store.Remove(KeyPalette);
            _store.Remove(KeyCustomFlag);
        }

        public static string Serialize(ThemePalette p) =>
            ColorMath.ToHex(p.Primary) + "|" + ColorMath.ToHex(p.Secondary) + "|" + ColorMath.ToHex(p.Tertiary);

        public static bool TryParse(string s, out ThemePalette palette)
        {
            palette = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split('|');
            if (parts.Length != 3) return false;
            var a = ColorMath.ParseHex(parts[0]);
            var b = ColorMath.ParseHex(parts[1]);
            var c = ColorMath.ParseHex(parts[2]);
            if (a == null || b == null || c == null) return false;
            palette = new ThemePalette { Primary = a.Value, Secondary = b.Value, Tertiary = c.Value };
            return true;
        }

        public string DescribePalette()
        {
            var p = GetPalette();
            string tag = HasCustomPalette ? "自定义" : "默认";
            return tag + " 主色 " + ColorMath.ToHex(p.Primary) +
                " / 辅助 " + ColorMath.ToHex(p.Secondary) +
                " / 强调 " + ColorMath.ToHex(p.Tertiary);
        }
    }
}
