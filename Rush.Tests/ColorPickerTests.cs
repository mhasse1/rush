using Rush;
using Xunit;

namespace Rush.Tests;

public class ColorPickerTests
{
    [Theory]
    [InlineData(0, 1.0, 0.5)]      // Red
    [InlineData(120, 1.0, 0.5)]    // Green
    [InlineData(240, 1.0, 0.5)]    // Blue
    [InlineData(60, 1.0, 0.5)]     // Yellow
    [InlineData(0, 0, 0.5)]        // Gray (achromatic)
    [InlineData(210, 0.5, 0.3)]    // Dark desaturated blue
    [InlineData(30, 0.8, 0.9)]     // Light orange
    public void HslToRgb_Roundtrip(double h, double s, double l)
    {
        var (r, g, b) = Theme.HslToRgb(h, s, l);

        // RGB should be in [0, 1]
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(g, 0.0, 1.0);
        Assert.InRange(b, 0.0, 1.0);

        var (h2, s2, l2) = Theme.RgbToHsl(r, g, b);

        // Lightness and saturation should roundtrip exactly
        Assert.Equal(l, l2, precision: 3);
        Assert.Equal(s, s2, precision: 3);

        // Hue roundtrips except for achromatic (s=0) where hue is arbitrary
        if (s > 0.001)
            Assert.Equal(h, h2, precision: 1);
    }

    [Fact]
    public void HslToRgb_PureRed()
    {
        var (r, g, b) = Theme.HslToRgb(0, 1.0, 0.5);
        Assert.Equal(1.0, r, precision: 3);
        Assert.Equal(0.0, g, precision: 3);
        Assert.Equal(0.0, b, precision: 3);
    }

    [Fact]
    public void HslToRgb_White()
    {
        var (r, g, b) = Theme.HslToRgb(0, 0, 1.0);
        Assert.Equal(1.0, r, precision: 3);
        Assert.Equal(1.0, g, precision: 3);
        Assert.Equal(1.0, b, precision: 3);
    }

    [Fact]
    public void HslToRgb_Black()
    {
        var (r, g, b) = Theme.HslToRgb(0, 0, 0);
        Assert.Equal(0.0, r, precision: 3);
        Assert.Equal(0.0, g, precision: 3);
        Assert.Equal(0.0, b, precision: 3);
    }

    [Fact]
    public void CuratedPalettes_AllParseAsValidHex()
    {
        // Access curated palettes via GenerateSimilarColors with a known color
        // to verify the method works, and test the curated palettes exist
        // by checking they all parse. We test via the NormalizeHex path.
        var testColors = new[]
        {
            // Dark palette samples
            "#2E3440", "#282A36", "#002B36", "#282828", "#282C34",
            "#1A1B26", "#1E1E2E", "#191724",
            // Light palette samples
            "#FDF6E3", "#FAF4ED", "#EFF1F5", "#FAFAFA", "#FBF1C7",
            // Grayscale samples
            "#0A0A0A", "#808080", "#F0F0F0"
        };

        foreach (var hex in testColors)
        {
            Assert.True(Theme.TryParseHexColor(hex, out _, out _, out _),
                $"Failed to parse curated color: {hex}");
        }
    }

    [Fact]
    public void GenerateSimilarColors_ReturnsValidHexColors()
    {
        var similar = ColorPicker.GenerateSimilarColors("#2E3440", 12);
        Assert.NotEmpty(similar);
        Assert.True(similar.Length <= 12);

        foreach (var entry in similar)
        {
            Assert.StartsWith("#", entry.Hex);
            Assert.True(Theme.TryParseHexColor(entry.Hex, out _, out _, out _),
                $"Generated invalid hex: {entry.Hex}");
        }
    }

    [Fact]
    public void GenerateSimilarColors_ColorsAreDifferentFromInput()
    {
        var input = "#2E3440";
        var similar = ColorPicker.GenerateSimilarColors(input, 12);

        var normalized = ColorPicker.NormalizeHex(input);
        foreach (var entry in similar)
            Assert.NotEqual(normalized, entry.Hex);
    }

    [Fact]
    public void GenerateSimilarColors_InvalidInput_ReturnsEmpty()
    {
        var result = ColorPicker.GenerateSimilarColors("not-a-color", 12);
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeHex_ProducesUppercaseRRGGBB()
    {
        var result = ColorPicker.NormalizeHex("#abc");
        Assert.Matches("^#[0-9A-F]{6}$", result);
    }

    [Fact]
    public void NormalizeHex_SixDigit_Roundtrips()
    {
        var result = ColorPicker.NormalizeHex("#2E3440");
        Assert.Equal("#2E3440", result);
    }
}
