using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class ColorParserTests
{
    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#ff0000", 255, 255, 0, 0)]
    [InlineData("#00ff0080", 128, 0, 255, 0)]
    [InlineData("rgb(10, 20, 30)", 255, 10, 20, 30)]
    [InlineData("rgba(10, 20, 30, 0.5)", 128, 10, 20, 30)]
    [InlineData("black", 255, 0, 0, 0)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void ParsesCssColors(string value, byte a, byte r, byte g, byte b)
    {
        var color = ColorParser.Parse(value);

        Assert.Equal(a, color.A);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }

    [Fact]
    public void ParsesHsl()
    {
        var color = ColorParser.Parse("hsl(0, 100%, 50%)");

        Assert.Equal(Color.FromRgb(255, 0, 0), color);
    }

    [Fact]
    public void UnknownColorIsTransparent()
    {
        Assert.Equal(Color.Transparent, ColorParser.Parse("not-a-color"));
        Assert.Equal(Color.Transparent, ColorParser.Parse(null));
    }

    [Fact]
    public void AppliesOpacity()
    {
        Assert.Equal(128, Color.FromRgb(10, 20, 30).WithOpacity(0.5).A);
    }
}
