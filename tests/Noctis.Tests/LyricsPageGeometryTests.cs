using Noctis.Services;
using Noctis.Services.LyricsPage;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageGeometryTests
{
    [Fact]
    public void Story_zones_use_the_published_reels_margins()
    {
        Assert.Equal(new SafeInsets(65, 270, 140, 672), SocialSafeZones.For(ShareCardFormat.Story, true));
        Assert.Equal(new SafeInsets(60, 60, 60, 60), SocialSafeZones.For(ShareCardFormat.Story, false));
        Assert.Equal(new SafeInsets(135, 60, 135, 60), SocialSafeZones.For(ShareCardFormat.Square, true));
        Assert.Empty(SocialSafeZones.Zones(ShareCardFormat.Story, false));
    }

    [Theory]
    [InlineData(ShareCardFormat.Story, true)]
    [InlineData(ShareCardFormat.Story, false)]
    [InlineData(ShareCardFormat.Square, true)]
    [InlineData(ShareCardFormat.Square, false)]
    public void Header_and_viewport_sit_inside_the_content_and_clear_of_every_zone(ShareCardFormat f, bool on)
    {
        var g = LyricsPageLayout.Measure(f, on);
        Assert.True(g.Content.Contains(g.Header) && g.Content.Contains(g.Viewport));
        foreach (var z in SocialSafeZones.Zones(f, on)) Assert.False(z.IntersectsWith(g.Content));
        Assert.Equal(g.Viewport.Height * 0.32f, g.AnchorY, 3);
        Assert.Equal(f == ShareCardFormat.Story ? 72f : 58f, g.LyricSize);
        Assert.Equal(g.LyricSize * 0.6f, g.BackgroundVocalSize, 3);
    }
}
