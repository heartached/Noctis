using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Transformation;
using Noctis.Controls;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// <see cref="MediaArtwork"/> dresses the now-playing cover as a CD, a vinyl sleeve or a
/// cassette. Exactly one costume may be on screen at a time, and the animated-cover
/// decoder must only run inside the costume that is showing.
/// </summary>
public class MediaArtworkTests
{
    private static readonly (ArtworkMedium Medium, string Layout, string Animated)[] Costumes =
    {
        (ArtworkMedium.Cover, "CoverLayout", "CoverAnimated"),
        (ArtworkMedium.CompactDisc, "DiscLayout", "DiscAnimated"),
        (ArtworkMedium.Vinyl, "VinylLayout", "SleeveAnimated"),
        (ArtworkMedium.Cassette, "CassetteLayout", "CassetteAnimated"),
    };

    [AvaloniaFact]
    public void DefaultMedium_IsThePlainCover()
    {
        var art = new MediaArtwork();

        Assert.Equal(ArtworkMedium.Cover, art.Medium);
        Assert.True(art.FindControl<Control>("CoverLayout")!.IsVisible);
        Assert.False(art.FindControl<Control>("DiscLayout")!.IsVisible);
        Assert.False(art.FindControl<Control>("VinylLayout")!.IsVisible);
        Assert.False(art.FindControl<Control>("CassetteLayout")!.IsVisible);
    }

    [AvaloniaFact]
    public void Medium_ShowsExactlyOneLayout()
    {
        var art = new MediaArtwork();

        foreach (var costume in Costumes)
        {
            art.Medium = costume.Medium;
            foreach (var other in Costumes)
            {
                var layout = art.FindControl<Control>(other.Layout)!;
                Assert.Equal(other.Medium == costume.Medium, layout.IsVisible);
            }
        }
    }

    [AvaloniaFact]
    public void AnimatedCover_RunsOnlyInsideTheVisibleLayout()
    {
        var art = new MediaArtwork { AnimatedActive = true };

        foreach (var costume in Costumes)
        {
            art.Medium = costume.Medium;
            foreach (var other in Costumes)
            {
                var animated = art.FindControl<AnimatedCoverImage>(other.Animated)!;
                Assert.Equal(other.Medium == costume.Medium, animated.IsActive);
            }
        }

        art.AnimatedActive = false;
        foreach (var costume in Costumes)
            Assert.False(art.FindControl<AnimatedCoverImage>(costume.Animated)!.IsActive);
    }

    [Fact]
    public void ReelAngle_IsContinuousAcrossTheDiscWrap()
    {
        // The reels are geared 1.8:1 off the disc. Deriving them from the WRAPPED disc
        // angle snapped them by 72° every turn (1.8 × 360 is not a multiple of 360),
        // which read as the hubs glitching every five seconds.
        const double step = 72.0 / 60; // one 60 fps frame at the default speed
        for (double total = 0; total < 1800; total += step)
        {
            var delta = SpinClock.Wrap(MediaArtwork.ReelAngle(total + step) - MediaArtwork.ReelAngle(total));
            Assert.Equal(step * 1.8, delta, 6);
        }
    }

    [AvaloniaFact]
    public void Vinyl_RecordSlidesOutWhilePlaying()
    {
        var art = new MediaArtwork { Medium = ArtworkMedium.Vinyl };
        var slide = art.FindControl<Panel>("VinylSlide")!;

        Assert.Equal(0, ((TransformOperations)slide.RenderTransform!).Value.M31, 6);

        art.IsSpinning = true;
        Assert.True(((TransformOperations)slide.RenderTransform!).Value.M31 > 0);

        art.IsSpinning = false;
        Assert.Equal(0, ((TransformOperations)slide.RenderTransform!).Value.M31, 6);
    }

    [AvaloniaFact]
    public void Spinning_WithoutAWindow_DoesNotThrow()
    {
        // Detached controls have no TopLevel to drive frames; the request must be a no-op.
        var art = new MediaArtwork { Medium = ArtworkMedium.Vinyl };
        art.IsSpinning = true;
        art.IsSpinning = false;
        art.Medium = ArtworkMedium.Cover;
    }
}
