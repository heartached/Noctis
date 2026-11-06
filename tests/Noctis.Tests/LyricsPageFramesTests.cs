using Noctis.Services;
using Noctis.Services.LyricsPage;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageFramesTests
{
    [Fact]
    public void Frames_are_start_plus_n_over_fps_plus_the_page_lookahead_with_no_drift()
    {
        var t = new ShareClipTiming(42.25, 60);
        Assert.Equal(3600, LyricsPageFrames.Count(t, 60));
        Assert.Equal(930, LyricsPageFrames.Count(new ShareClipTiming(0, 15.5), 60));
        Assert.Equal(1, LyricsPageFrames.Count(new ShareClipTiming(0, 0), 60));
        Assert.Equal(42.25 + 0.08, LyricsPageFrames.TrackTime(0, 60, t), 12);
        Assert.Equal(42.25 + 3599 / 60.0 + 0.08, LyricsPageFrames.TrackTime(3599, 60, t), 12);
        for (int n = 1; n < 3600; n++)
            Assert.Equal(1 / 60.0, LyricsPageFrames.TrackTime(n, 60, t) - LyricsPageFrames.TrackTime(n - 1, 60, t), 9);
        Assert.Equal(2.5, LyricsPageFrames.ClipTime(150, 60), 12);
    }
}
