using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class LyricsScrollAnchorTests
{
    // A tall lyric list: 40 lines of 60px in a 400px viewport.
    private const double Viewport = 400;
    private const double Extent = 2400;
    private const double LineHeight = 60;

    [Fact]
    public void AnchorsActiveLineAtAnchorRatio()
    {
        // Line 10 starts at 600; anchor is 22% down the viewport (88px), plus half the line.
        var offset = LyricsScrollAnchor.ComputeAnchorOffset(600, LineHeight, Viewport, Extent);
        Assert.Equal(600 - 88 + 30, offset, 3);
    }

    [Fact]
    public void NeverScrollsAboveTheTop()
    {
        // The first lines sit above the anchor line and must not push the offset negative.
        var offset = LyricsScrollAnchor.ComputeAnchorOffset(0, LineHeight, Viewport, Extent);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void StopsAtTheEndOfTheContent()
    {
        // End of the song: anchoring the last line would need to scroll past the content.
        // The offset must stop at Extent - Viewport, otherwise the callers animate towards
        // an offset the scroll viewer coerces away and the cascade stagger shifts the
        // remaining lines with no scrolling behind it.
        var lastLineTop = Extent - LineHeight;
        var offset = LyricsScrollAnchor.ComputeAnchorOffset(lastLineTop, LineHeight, Viewport, Extent);
        Assert.Equal(Extent - Viewport, offset);
    }

    [Fact]
    public void SuccessiveTailLinesResolveToTheSameOffset()
    {
        // Once the tail is fully on screen, advancing the active line must not move it —
        // this is the "lyrics keep sliding at the end of the song" regression.
        var a = LyricsScrollAnchor.ComputeAnchorOffset(Extent - LineHeight * 3, LineHeight, Viewport, Extent);
        var b = LyricsScrollAnchor.ComputeAnchorOffset(Extent - LineHeight * 2, LineHeight, Viewport, Extent);
        var c = LyricsScrollAnchor.ComputeAnchorOffset(Extent - LineHeight, LineHeight, Viewport, Extent);
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void ContentShorterThanViewportStaysAtTop()
    {
        // Whole song fits on screen: nothing is scrollable, so the offset stays at 0
        // rather than being clamped to a negative maximum.
        var offset = LyricsScrollAnchor.ComputeAnchorOffset(240, LineHeight, Viewport, 300);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void UnmeasuredViewportDoesNotPinToZero()
    {
        // First layout pass reports Viewport/Extent as 0. The target must survive so the
        // initial jump-to-line still lands once layout settles.
        var offset = LyricsScrollAnchor.ComputeAnchorOffset(600, LineHeight, 0, 0);
        Assert.Equal(630, offset, 3);
    }
}
