using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
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

    [AvaloniaFact]
    public void VinylAndCassette_UseTheCarouselProportions()
    {
        // Same drawing as the album carousel's cases, laid out in pixels for the box.
        var art = new MediaArtwork { Medium = ArtworkMedium.Vinyl, Width = 417, Height = 417 };
        var window = new Window { Width = 500, Height = 500, Content = art };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var s = 417 / AlbumCarouselCard.VinylWidth;
        Assert.Equal(s, art.FindControl<Border>("VinylSleeve")!.Bounds.Width, tolerance: 1.0);
        var slide = art.FindControl<Panel>("VinylSlide")!;
        Assert.Equal(2 * AlbumCarouselCard.VinylDiscRadius * s, slide.Bounds.Width, tolerance: 1.0);
        Assert.Equal(slide.Bounds.Width, slide.Bounds.Height, tolerance: 1.0); // square: a scratch finds the disc's centre from it
        // A scratch grabs the disc anywhere on it, not only on the label art.
        Assert.NotNull(art.FindControl<Border>("VinylSpin")!.Background);

        art.Medium = ArtworkMedium.Cassette;
        Dispatcher.UIThread.RunJobs();
        var shell = art.FindControl<Canvas>("CassetteShell")!.Bounds;
        Assert.Equal(417, shell.Width, tolerance: 1.0);
        Assert.Equal(417 / AlbumCarouselCard.CassetteWidth * AlbumCarouselCard.CassetteHeight, shell.Height, tolerance: 1.0);
        window.Close();
    }

    /// <summary>Saves one PNG per costume, paused and playing, on a dark and a light page.
    /// Runs only with NOCTIS_TEST_SKIA=1 and NOCTIS_ARTWORK_SHOTS set to an output folder.</summary>
    [AvaloniaFact]
    public void Costumes_SaveRenders()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ARTWORK_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(Path.GetTempPath(), "NoctisArtworkShot.png");
        using (var bmp = new SKBitmap(512, 512))
        using (var canvas = new SKCanvas(bmp))
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(512, 512), new[] { SKColors.SteelBlue, SKColors.MediumPurple }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, 512, 512, paint);
            using var white = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
            canvas.DrawCircle(256, 220, 110, white);
            using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(160) };
            canvas.DrawRect(40, 400, 432, 60, dark);
            using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }

        foreach (var medium in new[] { ArtworkMedium.CompactDisc, ArtworkMedium.Vinyl, ArtworkMedium.Cassette })
            foreach (var light in new[] { false, true })
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 40, Margin = new Avalonia.Thickness(30) };
                MediaArtwork Art(bool spinning) => new() { Medium = medium, SourcePath = path, DecodeWidth = 768, Width = 420, Height = 420, IsSpinning = spinning };
                row.Children.Add(Art(false));
                row.Children.Add(Art(true));
                var window = new Window
                {
                    Width = 960, Height = 480, Content = row,
                    Background = new SolidColorBrush(Color.Parse(light ? "#F2F2F4" : "#141414")),
                    RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
                };
                window.Show();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1200)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"lyrics-{medium}-{(light ? "light" : "dark")}.png"));
                window.Close();
            }
    }
}
