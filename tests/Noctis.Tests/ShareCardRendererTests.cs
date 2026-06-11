using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ShareCardRendererTests
{
    // Fake measurer: every character is 10 units wide.
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void WrapText_ShortLine_StaysSingle()
    {
        var lines = ShareCardRenderer.WrapText("hello world", 200f, Measure);
        Assert.Single(lines);
        Assert.Equal("hello world", lines[0]);
    }

    [Fact]
    public void WrapText_BreaksAtWordBoundaries()
    {
        // maxWidth 120 = 12 chars per line
        var lines = ShareCardRenderer.WrapText("and when we're apart", 120f, Measure);
        Assert.Equal(new[] { "and when", "we're apart" }, lines);
    }

    [Fact]
    public void WrapText_HardBreaksOversizedWord()
    {
        var lines = ShareCardRenderer.WrapText("abcdefghij", 50f, Measure);
        Assert.Equal(new[] { "abcde", "fghij" }, lines);
    }

    [Fact]
    public void WrapText_EmptyInput_ReturnsNoLines()
    {
        Assert.Empty(ShareCardRenderer.WrapText("   ", 100f, Measure));
        Assert.Empty(ShareCardRenderer.WrapText("", 100f, Measure));
    }

    [Theory]
    [InlineData(0, 0, 0, false)]        // black bg → white text
    [InlineData(255, 255, 255, true)]   // white bg → dark text
    [InlineData(26, 26, 46, false)]     // app navy → white text
    [InlineData(160, 160, 160, true)]   // light gray (like the Spotify gray card) → dark text
    public void UseDarkText_PicksByLuminance(byte r, byte g, byte b, bool expectDark)
    {
        Assert.Equal(expectDark, ShareCardRenderer.UseDarkText(r, g, b));
    }

    [Fact]
    public void Ellipsize_KeepsFittingText()
    {
        Assert.Equal("short", ShareCardRenderer.Ellipsize("short", 100f, Measure));
    }

    [Fact]
    public void Ellipsize_TruncatesWithEllipsis()
    {
        var result = ShareCardRenderer.Ellipsize("a very long track title", 100f, Measure);
        Assert.EndsWith("…", result);
        Assert.True(Measure(result) <= 100f);
    }

    [Fact]
    public void RenderLyricCard_Square_ProducesPng()
    {
        var png = ShareCardRenderer.RenderLyricCard(new LyricCardSpec
        {
            Title = "Those Eyes",
            Artist = "New West",
            ArtworkPath = null,
            Lines = new[] { "And when we're apart, and I'm missing you", "I close my eyes and all I see is you" },
            Format = ShareCardFormat.Square,
        });

        Assert.True(png.Length > 1000);
        // PNG magic bytes
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void RenderLyricCard_Story_ProducesPng()
    {
        var png = ShareCardRenderer.RenderLyricCard(new LyricCardSpec
        {
            Title = "Those Eyes",
            Artist = "New West",
            ArtworkPath = "Z:\\does\\not\\exist.jpg",
            Lines = new[] { "I close my eyes and all I see is you" },
            Format = ShareCardFormat.Story,
        });

        Assert.True(png.Length > 1000);
        Assert.Equal(0x89, png[0]);
    }
}
