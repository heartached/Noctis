using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Converters;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Contract for the AMLL-style sweep band: a CONSTANT-width feather whose centre
/// travels linearly with progress and whose stops clamp to the word box with
/// endpoint-alpha compensation, so it can straddle token boundaries. The old
/// collapsing feather (width = min(F, raw, 1-raw)) parked the solid edge at the
/// start of every token and rushed it at 2× near the end — on multi-second held
/// words that read as the sweep stalling at every letter boundary.
/// </summary>
public class ProgressToSweepForegroundConverterTests
{
    private const double F = ProgressToSweepForegroundConverter.Feather;

    private static object? Convert(double progress, IBrush? fg = null) =>
        new ProgressToSweepForegroundConverter().Convert(
            new object?[] { progress, fg ?? Brushes.White }, typeof(IBrush), null,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void FarBeforeTheWord_IsFullyTransparent()
    {
        // Words the band hasn't reached (including the InertFuture resting state)
        // must paint nothing — a bright sliver on unsung words means this regressed.
        Assert.Same(Brushes.Transparent, Convert(-F));
        Assert.Same(Brushes.Transparent, Convert(-1.0));
        Assert.Same(Brushes.Transparent, Convert(KaraokeSweep.InertFuture));
    }

    // These build brushes, and every AvaloniaObject ctor calls
    // Dispatcher.VerifyAccess — so they have to run on the UI thread.
    [AvaloniaFact]
    public void FarPastTheWord_ReturnsTheForegroundBrush()
    {
        // Once the trailing feather has fully crossed the right edge the bound brush
        // instance comes back, keeping the user's lyric colour on completed words.
        var fg = new SolidColorBrush(Color.Parse("#111111"));
        Assert.Same(fg, Convert(1.0 + F, fg));
        Assert.Same(fg, Convert(KaraokeSweep.InertPast, fg));
    }

    [AvaloniaTheory]
    [InlineData(0.20)]
    [InlineData(0.35)]
    [InlineData(0.50)]
    [InlineData(0.65)]
    [InlineData(0.80)]
    public void SolidEdge_SitsExactlyOneFeatherBehindProgress(double progress)
    {
        // THE slow-word stutter regression test: the fully-lit edge must trail
        // `progress` by exactly one constant feather — never pinned at 0 (park)
        // and never catching up at 2× (rush).
        var brush = Assert.IsType<LinearGradientBrush>(Convert(progress));
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal(progress - F, brush.GradientStops[0].Offset, precision: 10);
        Assert.Equal(255, brush.GradientStops[0].Color.A);
    }

    [AvaloniaTheory]
    [InlineData(0.20)]
    [InlineData(0.35)]
    [InlineData(0.50)]
    [InlineData(0.65)]
    [InlineData(0.80)]
    public void LeadingTip_SitsExactlyOneFeatherAheadOfProgress(double progress)
    {
        var brush = Assert.IsType<LinearGradientBrush>(Convert(progress));
        Assert.Equal(progress + F, brush.GradientStops[1].Offset, precision: 10);
        Assert.Equal(0, brush.GradientStops[1].Color.A);
    }

    [AvaloniaFact]
    public void WordStart_ShowsTheHalfEnteredBand_NotAFullBrightSliver()
    {
        // At progress 0 the band straddles the left edge: the edge pixel is at 50%
        // alpha fading out one feather in. A full-alpha stop at offset 0 would flash
        // a bright sliver the moment the word becomes current.
        var brush = Assert.IsType<LinearGradientBrush>(Convert(0.0));
        Assert.Equal(0.0, brush.GradientStops[0].Offset, precision: 10);
        Assert.InRange(brush.GradientStops[0].Color.A, 120, 136);
        Assert.Equal(F, brush.GradientStops[1].Offset, precision: 10);
        Assert.Equal(0, brush.GradientStops[1].Color.A);
    }

    [AvaloniaFact]
    public void LeadingFeather_EntersBeforeTheWordStarts()
    {
        // Pre-roll: slightly negative progress paints the first sliver of haze at
        // the left edge, so the edge glides INTO the word instead of popping up
        // inside it after the boundary.
        var brush = Assert.IsType<LinearGradientBrush>(Convert(-F / 2));
        Assert.Equal(0.0, brush.GradientStops[0].Offset, precision: 10);
        Assert.InRange(brush.GradientStops[0].Color.A, 56, 72); // ~25%
        Assert.Equal(F / 2, brush.GradientStops[1].Offset, precision: 10);
    }

    [AvaloniaFact]
    public void TrailingFeather_FinishesAfterTheWordEnds()
    {
        // At progress 1 the right edge is only half lit; the rest brightens during
        // the overshoot window — that is what lets the band cross a word boundary
        // without the tip parking there while the solid edge rushes to close.
        var atEnd = Assert.IsType<LinearGradientBrush>(Convert(1.0));
        Assert.Equal(1.0, atEnd.GradientStops[1].Offset, precision: 10);
        Assert.InRange(atEnd.GradientStops[1].Color.A, 120, 136);
        Assert.Equal(1.0 - F, atEnd.GradientStops[0].Offset, precision: 10);
        Assert.Equal(255, atEnd.GradientStops[0].Color.A);

        var overshoot = Assert.IsType<LinearGradientBrush>(Convert(1.0 + F / 2));
        Assert.True(overshoot.GradientStops[1].Color.A > atEnd.GradientStops[1].Color.A,
            "the right edge must keep brightening while the band exits the word");
    }

    [AvaloniaTheory]
    [InlineData(-0.03)]
    [InlineData(0.0)]
    [InlineData(0.03)]
    [InlineData(0.5)]
    [InlineData(0.97)]
    [InlineData(1.0)]
    [InlineData(1.03)]
    public void StopsAlwaysStayInsideTheWord(double progress)
    {
        var brush = Assert.IsType<LinearGradientBrush>(Convert(progress));
        foreach (var stop in brush.GradientStops)
            Assert.InRange(stop.Offset, 0.0, 1.0);
    }

    [AvaloniaFact]
    public void MidSweep_UsesTheForegroundColour_WithSameRgbTail()
    {
        // The wipe must be painted in the user's lyric colour (dark text on light
        // backgrounds), fading toward alpha-0 of the SAME colour so the feather
        // never darkens toward black.
        var fg = new SolidColorBrush(Color.Parse("#111111"));
        var brush = Assert.IsType<LinearGradientBrush>(Convert(0.5, fg));
        Assert.Equal(fg.Color, brush.GradientStops[0].Color);
        Assert.Equal(0, brush.GradientStops[1].Color.A);
        Assert.Equal(fg.Color.R, brush.GradientStops[1].Color.R);
    }

    [AvaloniaFact]
    public void NarrowTokens_GetAWiderRelativeFeather()
    {
        // AMLL's feather is a fixed ~0.5em regardless of word width. A single CJK
        // character cell is ~1em wide — a width-relative 6% feather renders a near
        // hard edge there and reads as per-character stepping. When the view passes
        // layout width [2] and font size [3], the feather becomes 0.25em each side.
        var brush = Assert.IsType<LinearGradientBrush>(
            new ProgressToSweepForegroundConverter().Convert(
                new object?[] { 0.5, Brushes.White, 30.0, 30.0 }, typeof(IBrush), null,
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.25, brush.GradientStops[0].Offset, precision: 10);
        Assert.Equal(0.75, brush.GradientStops[1].Offset, precision: 10);
    }
}
