using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Held-note glow envelope (AMLL initEmphasizeAnimation): a bell over word progress
/// whose peak scales with the hold length — short holds barely glow, multi-second
/// notes bloom — replacing the old fixed 0.5-opacity glow that snapped on and froze.
/// </summary>
public class EmphasisBellTests
{
    [Fact]
    public void Envelope_IsZeroAtTheEdges_PeaksMidWord()
    {
        Assert.Equal(0.0, EmphasisBell.Envelope(0.0), 6);
        Assert.Equal(1.0, EmphasisBell.Envelope(0.5), 6);
        Assert.Equal(0.0, EmphasisBell.Envelope(1.0), 6);
    }

    [Fact]
    public void Envelope_RisesThenReleases()
    {
        // Strictly monotone on each half — the glow must breathe through the hold,
        // never plateau-and-freeze like the old class transition did.
        var prev = EmphasisBell.Envelope(0.0);
        for (var x = 0.05; x <= 0.50001; x += 0.05)
        {
            var v = EmphasisBell.Envelope(x);
            Assert.True(v > prev, $"envelope must rise through x={x:0.00}");
            prev = v;
        }
        for (var x = 0.55; x <= 1.00001; x += 0.05)
        {
            var v = EmphasisBell.Envelope(x);
            Assert.True(v < prev, $"envelope must release through x={x:0.00}");
            prev = v;
        }
    }

    [Fact]
    public void Envelope_IsZeroOutsideTheWord()
    {
        // Pre-roll, overshoot and the inert sentinels all sit outside [0..1].
        Assert.Equal(0.0, EmphasisBell.Envelope(-0.5), 6);
        Assert.Equal(0.0, EmphasisBell.Envelope(1.5), 6);
        Assert.Equal(0.0, EmphasisBell.Envelope(KaraokeSweep.InertFuture), 6);
        Assert.Equal(0.0, EmphasisBell.Envelope(KaraokeSweep.InertPast), 6);
    }

    [Fact]
    public void Strength_GrowsWithHoldLength()
    {
        Assert.True(EmphasisBell.Strength(1000) < EmphasisBell.Strength(2000));
        Assert.True(EmphasisBell.Strength(2000) < EmphasisBell.Strength(4000));
        Assert.True(EmphasisBell.Strength(4000) < EmphasisBell.Strength(8000));
    }

    [Fact]
    public void Strength_MatchesAmllAnchors()
    {
        // f(du/3000)·0.5 capped at 0.8, f(x)=x³ below 1 and √x above: 3s → exactly
        // 0.5; very long holds cap at 0.8; a barely-held note is nearly invisible.
        Assert.Equal(0.5, EmphasisBell.Strength(3000), 6);
        Assert.Equal(0.8, EmphasisBell.Strength(30000), 6);
        Assert.True(EmphasisBell.Strength(1200) < 0.05);
    }

    [Fact]
    public void Evaluate_PeaksAtStrength_AndIsZeroOutsideTheWord()
    {
        Assert.Equal(EmphasisBell.Strength(3000), EmphasisBell.Evaluate(0.5, 3000), 6);
        Assert.Equal(0.0, EmphasisBell.Evaluate(KaraokeSweep.InertFuture, 3000), 6);
        Assert.Equal(0.0, EmphasisBell.Evaluate(KaraokeSweep.InertPast, 3000), 6);
    }
}
