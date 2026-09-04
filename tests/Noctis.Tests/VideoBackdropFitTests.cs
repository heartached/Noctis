using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics-page video backdrop decodes into a buffer that keeps the clip's aspect
/// ratio but never exceeds 960 px on the long side — the cap is what keeps a 4K clip
/// from copying ~33 MB per frame through the UI thread.
/// </summary>
public class VideoBackdropFitTests
{
    [Theory]
    [InlineData(3840, 2160, 960, 540)]   // 4K landscape → capped, 16:9 kept
    [InlineData(1080, 1920, 540, 960)]   // portrait clip → capped on the tall side
    [InlineData(640, 480, 640, 480)]     // already under the cap → untouched
    [InlineData(960, 960, 960, 960)]     // exactly at the cap
    [InlineData(0, 0, 960, 540)]         // unknown dimensions → 16:9 at the cap
    public void FitBuffer_CapsLongSideAndKeepsAspect(int w, int h, int expectedW, int expectedH)
        => Assert.Equal((expectedW, expectedH), VideoBackdrop.FitBuffer(w, h));

    [Fact]
    public void FitBuffer_RoundsToEvenDimensions()
    {
        var (w, h) = VideoBackdrop.FitBuffer(1999, 1001);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(w <= VideoBackdrop.MaxLongSide);
        Assert.True(h <= VideoBackdrop.MaxLongSide);
    }
}
