using System;
using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    public class ColorMathTests
    {
        [Theory]
        [InlineData("#00696D", 0x00, 0x69, 0x6D)]
        [InlineData("00696D", 0x00, 0x69, 0x6D)]
        [InlineData("#FFF", 0xFF, 0xFF, 0xFF)]
        [InlineData("#FF00696D", 0x00, 0x69, 0x6D)] // alpha ignored
        public void ParseHex_Valid(string hex, int r, int g, int b)
        {
            var c = ColorMath.ParseHex(hex);
            Assert.NotNull(c);
            Assert.Equal(r, ColorMath.Red(c.Value));
            Assert.Equal(g, ColorMath.Green(c.Value));
            Assert.Equal(b, ColorMath.Blue(c.Value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("#GGGGGG")]
        [InlineData("#12345")]
        [InlineData("xyz")]
        public void ParseHex_Invalid_ReturnsNull(string hex) => Assert.Null(ColorMath.ParseHex(hex));

        [Fact]
        public void ToHex_RoundTrips()
        {
            int c = ColorMath.Rgb(0x12, 0x34, 0x56);
            Assert.Equal("#123456", ColorMath.ToHex(c));
            Assert.Equal(c, ColorMath.ParseHex(ColorMath.ToHex(c)).Value);
        }

        [Fact]
        public void Luminance_Extremes()
        {
            Assert.Equal(0.0, ColorMath.RelativeLuminance(ColorMath.Black), 5);
            Assert.Equal(1.0, ColorMath.RelativeLuminance(ColorMath.White), 5);
        }

        [Fact]
        public void Contrast_BlackWhite_Is21()
        {
            Assert.Equal(21.0, ColorMath.ContrastRatio(ColorMath.Black, ColorMath.White), 3);
            Assert.Equal(1.0, ColorMath.ContrastRatio(ColorMath.White, ColorMath.White), 5);
        }

        [Fact]
        public void OnColor_PicksReadableOpposite()
        {
            Assert.Equal(ColorMath.White, ColorMath.OnColor(ColorMath.Black));
            Assert.Equal(ColorMath.Black, ColorMath.OnColor(ColorMath.White));
        }

        [Fact]
        public void OnColor_AlwaysMeetsAA_AcrossSpectrum()
        {
            for (int v = 0; v <= 255; v += 5)
            {
                int bg = ColorMath.Rgb(v, v, v);
                int fg = ColorMath.OnColor(bg);
                Assert.True(ColorMath.MeetsWcagAA(fg, bg),
                    $"gray {v}: ratio {ColorMath.ContrastRatio(fg, bg):F2}");
            }
        }

        [Fact]
        public void Blend_EndpointsAndMid()
        {
            Assert.Equal(ColorMath.Black, ColorMath.Blend(ColorMath.Black, ColorMath.White, 0));
            Assert.Equal(ColorMath.White, ColorMath.Blend(ColorMath.Black, ColorMath.White, 1));
            int mid = ColorMath.Blend(ColorMath.Black, ColorMath.White, 0.5);
            Assert.Equal(128, ColorMath.Red(mid));
        }

        [Fact]
        public void EnsureContrast_LiftsDarkPrimaryOnDarkSurface()
        {
            int darkSurface = ColorMath.ParseHex("#0E1515").Value;
            int darkPrimary = ColorMath.ParseHex("#00696D").Value;
            Assert.False(ColorMath.MeetsWcagAA(darkPrimary, darkSurface));
            int fixedColor = ColorMath.EnsureContrast(darkPrimary, darkSurface, 4.5);
            Assert.True(ColorMath.ContrastRatio(fixedColor, darkSurface) >= 4.5);
        }

        [Fact]
        public void EnsureContrast_LeavesPassingColorUnchanged()
        {
            int lightSurface = ColorMath.ParseHex("#F4FBFA").Value;
            int primary = ColorMath.ParseHex("#00696D").Value;
            Assert.True(ColorMath.MeetsWcagAA(primary, lightSurface));
            Assert.Equal(primary, ColorMath.EnsureContrast(primary, lightSurface, 4.5));
        }
    }
}
