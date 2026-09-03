using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// <see cref="HeartIcon"/> is the one favorite glyph for the whole app. Exactly one glyph
/// shows per state (red on / inherited-or-given off / nothing for badge overlays), it
/// measures to <see cref="HeartIcon.Size"/>, and the hidden glyph rests small and
/// transparent so the next toggle pops it in.
/// </summary>
public class HeartIconTests
{
    private static HeartIcon Layout(HeartIcon heart)
    {
        heart.Measure(new Size(100, 100));
        heart.Arrange(new Rect(heart.DesiredSize));
        return heart;
    }

    [AvaloniaFact]
    public void Off_ShowsTheOffGlyph_AndRestsTheOnGlyphSmall()
    {
        var heart = Layout(new HeartIcon { Size = 16 });

        var visible = heart.VisibleGlyph;
        Assert.NotNull(visible);
        Assert.Equal(16, visible!.Width);

        // The hidden red glyph waits at scale 0.7 / opacity 0 — the pop's start pose.
        var hidden = Assert.Single(heart.Children, c => !c.IsVisible);
        Assert.Equal(0, hidden.Opacity);
        Assert.Equal(TransformOperations.Parse("scale(0.7)").ToString(), hidden.RenderTransform?.ToString());
    }

    [AvaloniaFact]
    public void On_ShowsTheRedGlyph_Only()
    {
        var heart = Layout(new HeartIcon { IsFavorite = true, Size = 12 });

        var visible = heart.VisibleGlyph!;
        Assert.Equal("#ffe74856", ((ISolidColorBrush)visible.Foreground!).Color.ToString());
        Assert.Single(heart.Children, c => c.IsVisible);
    }

    [AvaloniaFact]
    public void Toggling_SwapsTheVisibleGlyph_BothWays()
    {
        var heart = Layout(new HeartIcon());
        var off = heart.VisibleGlyph!;

        heart.IsFavorite = true;
        var on = heart.VisibleGlyph!;
        Assert.NotSame(off, on);
        Assert.False(off.IsVisible);

        heart.IsFavorite = false;
        Assert.Same(off, heart.VisibleGlyph);
        Assert.False(on.IsVisible);
        Assert.Equal(1, off.Opacity);
    }

    [AvaloniaFact]
    public void BadgeMode_ShowsNothingUntilFavorited()
    {
        var heart = Layout(new HeartIcon { ShowWhenOff = false });
        Assert.Null(heart.VisibleGlyph);

        heart.IsFavorite = true;
        Assert.NotNull(heart.VisibleGlyph);
    }

    [AvaloniaFact]
    public void OffBrushAndOpacity_ApplyToTheOffGlyph()
    {
        var heart = Layout(new HeartIcon { OffBrush = Brushes.White, OffOpacity = 0.28 });
        var off = heart.VisibleGlyph!;

        Assert.Same(Brushes.White, off.Foreground);
        Assert.Equal(0.28, off.Opacity, 3);
    }

    [AvaloniaFact]
    public void MeasuresToItsSize()
    {
        var heart = Layout(new HeartIcon { Size = 18 });

        Assert.Equal(18, heart.DesiredSize.Width, 3);
        Assert.Equal(18, heart.DesiredSize.Height, 3);
    }
}
