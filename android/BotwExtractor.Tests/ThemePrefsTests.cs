using System.Collections.Generic;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    public class FakeStore : ISettingsStore
    {
        readonly Dictionary<string, string> _data = new Dictionary<string, string>();
        public string Get(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _data[key] = value;
        public void Remove(string key) => _data.Remove(key);
    }

    public class ThemePrefsTests
    {
        [Fact]
        public void NightMode_DefaultsToSystem()
        {
            var p = new ThemePrefs(new FakeStore());
            Assert.Equal(NightMode.System, p.GetNightMode());
            Assert.Equal(-1, NightMode.ToAppCompatMode(p.GetNightMode()));
        }

        [Theory]
        [InlineData(NightMode.Light, 1)]
        [InlineData(NightMode.Dark, 2)]
        [InlineData(NightMode.System, -1)]
        [InlineData("garbage", -1)]
        public void NightMode_SetGetAndMapping(string mode, int expected)
        {
            var p = new ThemePrefs(new FakeStore());
            p.SetNightMode(mode);
            Assert.Equal(expected, NightMode.ToAppCompatMode(p.GetNightMode()));
        }

        [Fact]
        public void Palette_DefaultWhenNoCustom()
        {
            var p = new ThemePrefs(new FakeStore());
            Assert.False(p.HasCustomPalette);
            var pal = p.GetPalette();
            Assert.Equal(ThemePrefs.DefaultPalette().Primary, pal.Primary);
        }

        [Fact]
        public void Palette_SetPersistsAndRoundTrips()
        {
            var store = new FakeStore();
            var p = new ThemePrefs(store);
            var pal = new ThemePalette { Primary = ColorMath.Rgb(1, 2, 3), Secondary = ColorMath.Rgb(4, 5, 6), Tertiary = ColorMath.Rgb(7, 8, 9) };
            p.SetPalette(pal);
            Assert.True(p.HasCustomPalette);

            // A fresh instance over the same store reads the persisted values.
            var p2 = new ThemePrefs(store);
            var got = p2.GetPalette();
            Assert.Equal(pal.Primary, got.Primary);
            Assert.Equal(pal.Secondary, got.Secondary);
            Assert.Equal(pal.Tertiary, got.Tertiary);
        }

        [Fact]
        public void Palette_ResetRestoresDefaults()
        {
            var p = new ThemePrefs(new FakeStore());
            p.SetPalette(new ThemePalette { Primary = ColorMath.Black, Secondary = ColorMath.Black, Tertiary = ColorMath.Black });
            p.ResetPalette();
            Assert.False(p.HasCustomPalette);
            Assert.Equal(ThemePrefs.DefaultPalette().Primary, p.GetPalette().Primary);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#000000|#111111")]
        [InlineData("zzzzzz|#111111|#222222")]
        public void TryParse_Invalid_ReturnsFalse(string s)
        {
            Assert.False(ThemePrefs.TryParse(s, out _));
        }

        [Fact]
        public void TryParse_Valid_ReturnsPalette()
        {
            Assert.True(ThemePrefs.TryParse("#010203|#040506|#070809", out var p));
            Assert.Equal(ColorMath.Rgb(1, 2, 3), p.Primary);
            Assert.Equal(ColorMath.Rgb(7, 8, 9), p.Tertiary);
        }

        [Fact]
        public void DescribePalette_ReflectsCustomFlag()
        {
            var p = new ThemePrefs(new FakeStore());
            Assert.Contains("默认", p.DescribePalette());
            p.SetPalette(ThemePrefs.DefaultPalette());
            Assert.Contains("自定义", p.DescribePalette());
        }
    }
}
