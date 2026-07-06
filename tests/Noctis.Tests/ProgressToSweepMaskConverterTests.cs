using Avalonia.Media;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

public class ProgressToSweepMaskConverterTests
{
    private static object? Convert(double progress) =>
        new ProgressToSweepMaskConverter().Convert(progress, typeof(IBrush), null,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ZeroProgress_IsFullyTransparent()
    {
        // A bright sliver on the left edge of unsung words means this regressed.
        Assert.Same(Brushes.Transparent, Convert(0.0));
        Assert.Same(Brushes.Transparent, Convert(-0.1));
    }

    [Fact]
    public void FullProgress_IsFullyOpaque()
    {
        // A dimmed right edge on sung words means this regressed.
        Assert.Same(Brushes.White, Convert(1.0));
        Assert.Same(Brushes.White, Convert(1.1));
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
}
