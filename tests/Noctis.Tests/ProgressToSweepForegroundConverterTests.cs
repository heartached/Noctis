using Avalonia.Media;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

public class ProgressToSweepForegroundConverterTests
{
    private static object? Convert(double progress, IBrush? fg = null) =>
        new ProgressToSweepForegroundConverter().Convert(
            new object?[] { progress, fg ?? Brushes.White }, typeof(IBrush), null,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ZeroProgress_IsFullyTransparent()
    {
        // A bright sliver on the left edge of unsung words means this regressed.
        Assert.Same(Brushes.Transparent, Convert(0.0));
        Assert.Same(Brushes.Transparent, Convert(-0.1));
    }

    [Fact]
    public void FullProgress_ReturnsTheForegroundBrush()
    {
        // A dimmed right edge on sung words means this regressed. Returning the
        // bound brush instance keeps the user's lyric colour on completed words.
        var fg = new SolidColorBrush(Color.Parse("#111111"));
        Assert.Same(fg, Convert(1.0, fg));
        Assert.Same(fg, Convert(1.1, fg));
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.75)]
    [InlineData(0.98)]
    public void RevealEdgeTracksProgressLinearly(double progress)
    {
        // The 50% point of the feather band must sit exactly at `progress`. Any
        // remapping (easing, extended travel range) makes the edge dwell off-glyph
        // at word boundaries, which reads as the sweep stalling between words.
        var brush = Assert.IsType<LinearGradientBrush>(Convert(progress));
        var stops = brush.GradientStops;
        Assert.Equal(2, stops.Count);
        var centre = (stops[0].Offset + stops[1].Offset) / 2.0;
        Assert.Equal(progress, centre, precision: 10);
    }

    [Fact]
    public void FeatherShrinksAtBoundaries_SoBandNeverLeavesTheWord()
    {
        // Near the start the band must not extend below 0 (sliver) …
        var startBrush = Assert.IsType<LinearGradientBrush>(Convert(0.01));
        Assert.True(startBrush.GradientStops[0].Offset >= 0.0);

        // … and near the end it must not extend past 1 (unfinished-looking word).
        var endBrush = Assert.IsType<LinearGradientBrush>(Convert(0.99));
        Assert.True(endBrush.GradientStops[1].Offset <= 1.0);
    }

    [Fact]
    public void MidSweep_UsesTheForegroundColour_WithTransparentTail()
    {
        // The wipe must be painted in the user's lyric colour (dark text on light
        // backgrounds), fading to alpha-0 of the SAME colour so the feather never
        // darkens toward black.
        var fg = new SolidColorBrush(Color.Parse("#111111"));
        var brush = Assert.IsType<LinearGradientBrush>(Convert(0.5, fg));
        Assert.Equal(fg.Color, brush.GradientStops[0].Color);
        Assert.Equal(0, brush.GradientStops[1].Color.A);
        Assert.Equal(fg.Color.R, brush.GradientStops[1].Color.R);
    }
}
