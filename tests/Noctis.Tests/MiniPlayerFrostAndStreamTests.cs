using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mini player: the frosted band must sit just above the controls in PIXELS (so a tall
/// card keeps its cover sharp), and drawer rows must stream in slices rather than
/// inflate in one burst on open.
/// </summary>
public class MiniPlayerFrostAndStreamTests
{
    // Controls block at the canonical 340×520 card ≈ 0.34 of the height (177 px):
    // the anchored stops must reproduce the measured 0.40 / 0.54 / 0.64 there.
    [Fact]
    public void FrostBand_MatchesTheMeasuredCanonicalCard()
    {
        var stops = MiniFrostBand.Compute(520, 177);
        Assert.Equal(0.64, stops.Full, 2);
        Assert.Equal(0.54, stops.Mid, 2);
        Assert.Equal(0.40, stops.Start, 2);
    }

    [Fact]
    public void FrostBand_StaysPinnedToTheControlsOnATallCard()
    {
        // Same controls, a 760 px card: the frost begins ~135 px above the controls,
        // not at 40% of the card (which would be 216 px above them).
        var stops = MiniFrostBand.Compute(760, 177);
        var startPx = stops.Start * 760;
        var controlsTopPx = 760 - 177;
        Assert.InRange(controlsTopPx - startPx, 130, 140);
        // 0.59 of the card here vs the fixed 0.40 the old resource used.
        Assert.True(stops.Start > 0.55, $"start {stops.Start} should sit low on a tall card");
        Assert.True(stops.Start < stops.Mid && stops.Mid < stops.Full);
    }

    [Fact]
    public void FrostBand_DegenerateSizesFallBackToTheOldStops()
    {
        Assert.Equal(new MiniFrostBand.Stops(0.40, 0.54, 0.64), MiniFrostBand.Compute(0, 100));
        Assert.Equal(new MiniFrostBand.Stops(0.40, 0.54, 0.64), MiniFrostBand.Compute(double.NaN, 100));
        // Controls taller than the host: everything clamps into 0..1 and stays ordered.
        var s = MiniFrostBand.Compute(100, 400);
        Assert.InRange(s.Start, 0, 1);
        Assert.True(s.Start <= s.Mid && s.Mid <= s.Full);
    }

    // Brushes are AvaloniaObjects with thread affinity: build them on the UI thread.
    [AvaloniaFact]
    public void FrostBand_ApplyMovesTheStopsInPlace()
    {
        var mask = MiniFrostBand.CreateMask();
        var scrim = MiniFrostBand.CreateScrim();
        MiniFrostBand.Apply(mask, scrim, new MiniFrostBand.Stops(0.7, 0.8, 0.9));
        Assert.Equal(0.7, mask.GradientStops[0].Offset, 9);
        Assert.Equal(0.8, mask.GradientStops[1].Offset, 9);
        Assert.Equal(0.9, mask.GradientStops[2].Offset, 9);
        Assert.Equal(0.7, scrim.GradientStops[0].Offset, 9);
        Assert.Equal(0.9, scrim.GradientStops[1].Offset, 9);
        Assert.Equal(1.0, scrim.GradientStops[2].Offset, 9);
    }

    [Fact]
    public void Chunks_FirstSliceIsSmallThenSteady()
    {
        var items = Enumerable.Range(0, 100).ToList();
        var slices = StreamingFill.Chunks(items, first: 8, chunk: 10);
        Assert.Equal(8, slices[0].Count);
        Assert.All(slices.Skip(1).Take(slices.Count - 2), s => Assert.Equal(10, s.Count));
        Assert.Equal(100, slices.Sum(s => s.Count));
        Assert.Equal(items, slices.SelectMany(s => s));
    }

    [AvaloniaFact]
    public async Task Into_HoldsTheRemainingSlicesWhileTheGateIsClosed()
    {
        var target = new BulkObservableCollection<int>();
        var open = false;
        var gen = 1;
        StreamingFill.Into(target, Enumerable.Range(0, 30).ToList(), gen, () => gen, first: 8, chunk: 10, gate: () => open);
        Assert.Equal(8, target.Count);

        var end = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < end) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
        Assert.Equal(8, target.Count);

        open = true;
        end = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < end && target.Count < 30) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
        Assert.Equal(30, target.Count);
    }

    [Fact]
    public void Chunks_SmallListsAreOneSlice_EmptyIsNone()
    {
        Assert.Single(StreamingFill.Chunks(new[] { 1, 2, 3 }, 8, 10));
        Assert.Empty(StreamingFill.Chunks(Array.Empty<int>(), 8, 10));
        // Degenerate sizes never loop forever.
        Assert.Equal(5, StreamingFill.Chunks(Enumerable.Range(0, 5).ToList(), 0, -1).Count);
    }
}
