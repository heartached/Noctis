using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ParametricEqMathTests
{
    private static ParametricEqBand Band(double freq, double gain, double q = ParametricEqMath.DefaultQ)
        => new() { FrequencyHz = freq, GainDb = gain, Q = q };

    [Fact]
    public void ZeroGainBand_HasFlatResponse()
    {
        Assert.Equal(0.0, ParametricEqMath.PeakingResponseDb(1000, 0, 1.41, 1000));
        Assert.Equal(0.0, ParametricEqMath.PeakingResponseDb(1000, 0, 1.41, 60));
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(-6.0)]
    [InlineData(12.0)]
    public void PeakingBand_ReachesGainAtCenterFrequency(double gainDb)
    {
        var response = ParametricEqMath.PeakingResponseDb(1000, gainDb, 1.41, 1000);
        Assert.Equal(gainDb, response, 1);
    }

    [Fact]
    public void PeakingBand_DecaysAwayFromCenter()
    {
        var atCenter = ParametricEqMath.PeakingResponseDb(1000, 6, 1.41, 1000);
        var twoOctavesUp = ParametricEqMath.PeakingResponseDb(1000, 6, 1.41, 4000);
        var farAway = ParametricEqMath.PeakingResponseDb(1000, 6, 1.41, 16000);

        Assert.True(twoOctavesUp < atCenter / 2);
        Assert.True(farAway < 0.5);
    }

    [Fact]
    public void HigherQ_IsNarrower()
    {
        // One octave from center, the narrow filter must contribute less.
        var wide = ParametricEqMath.PeakingResponseDb(1000, 6, 0.5, 2000);
        var narrow = ParametricEqMath.PeakingResponseDb(1000, 6, 5.0, 2000);
        Assert.True(narrow < wide);
    }

    [Fact]
    public void CompositeResponse_AddsInDb()
    {
        var bands = new[] { Band(1000, 4), Band(1000, 3) };
        var single4 = ParametricEqMath.PeakingResponseDb(1000, 4, ParametricEqMath.DefaultQ, 1000);
        var single3 = ParametricEqMath.PeakingResponseDb(1000, 3, ParametricEqMath.DefaultQ, 1000);
        Assert.Equal(single4 + single3, ParametricEqMath.CompositeResponseDb(bands, 1000), 6);
    }

    [Fact]
    public void MapToGraphicBands_FlatBands_ProduceZeros()
    {
        var mapped = ParametricEqMath.MapToGraphicBands(ParametricEqMath.FromGraphicBands(null));
        Assert.Equal(10, mapped.Length);
        Assert.All(mapped, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void MapToGraphicBands_PeaksAtNearestGraphicFrequency()
    {
        var mapped = ParametricEqMath.MapToGraphicBands(new[] { Band(1000, 6, 2.0) });
        // Index 4 is the 1 kHz graphic band.
        Assert.Equal(6f, mapped[4], 0.5f);
        for (var i = 0; i < mapped.Length; i++)
        {
            if (i == 4) continue;
            Assert.True(mapped[i] < mapped[4], $"band {i} should be below the 1 kHz peak");
        }
    }

    [Fact]
    public void MapToGraphicBands_ClampsToVlcRange()
    {
        var bands = new[] { Band(1000, 12, 0.5), Band(1100, 12, 0.5), Band(900, 12, 0.5) };
        var mapped = ParametricEqMath.MapToGraphicBands(bands);
        Assert.All(mapped, v => Assert.InRange(v, -12f, 12f));
    }

    [Fact]
    public void FromGraphicBands_MigratesLegacyGains()
    {
        var legacy = new float[] { 1, 2, 3, 4, 5, -1, -2, -3, -4, -5 };
        var bands = ParametricEqMath.FromGraphicBands(legacy);

        Assert.Equal(10, bands.Count);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ParametricEqMath.GraphicBandFrequencies[i], bands[i].FrequencyHz);
            Assert.Equal(ParametricEqMath.DefaultQ, bands[i].Q);
        }
        // The contract is the CURVE, not the slider values: mapping the bands
        // back must reproduce the graphic gains at the graphic frequencies.
        var roundTrip = ParametricEqMath.MapToGraphicBands(bands);
        for (var i = 0; i < 10; i++)
            Assert.Equal(legacy[i], roundTrip[i], 0.25f);
    }

    [Theory]
    // VLC "Full bass" — adjacent boosted bass bands overlap the hardest.
    [InlineData(new float[] { -8, 9.6f, 9.6f, 5.6f, 1.6f, -4, -8, -10.3f, -11.2f, -11.2f })]
    // VLC "Rock" — boost/cut alternation plus the near-colocated 12/14/16 kHz cluster.
    [InlineData(new float[] { 8, 4.8f, -5.6f, -8, -3.2f, 4, 8.8f, 11.2f, 11.2f, 11.2f })]
    // VLC "Headphones".
    [InlineData(new float[] { 4.8f, 11, 5.6f, -3.2f, -2.4f, 1.6f, 4.8f, 9.6f, 12, 12 })]
    public void FromGraphicBands_RoundTrip_DoesNotOvershoot(float[] curve)
    {
        // Preset→Custom regression (Discord, 2026-08-14): loading a preset into
        // the band editor and re-mapping it must reproduce the preset curve, not
        // the sum of overlapping Q=1.41 filters — that overshot by up to +14 dB
        // ("adjust one slider and the volume goes to 300%").
        var roundTrip = ParametricEqMath.MapToGraphicBands(ParametricEqMath.FromGraphicBands(curve));
        for (var i = 0; i < 10; i++)
            Assert.Equal(curve[i], roundTrip[i], 0.25f);
    }

    [Fact]
    public void ApplyUserPreamp_Zero_IsIdentity()
    {
        // Flat's zeroed preamp must survive untouched so the flat-bypass
        // branch (no filter in the chain) still triggers.
        Assert.Equal(0f, ParametricEqMath.ApplyUserPreamp(0f, 0.0));
        Assert.Equal(ParametricEqMath.VlcEqUnityPreampDb,
            ParametricEqMath.ApplyUserPreamp(ParametricEqMath.VlcEqUnityPreampDb, 0.0));
    }

    [Fact]
    public void ApplyUserPreamp_OffsetsRelativeToNative()
    {
        // -6 dB user preamp on a unity curve = 6 dB under native.
        Assert.Equal(ParametricEqMath.VlcEqUnityPreampDb - 6f,
            ParametricEqMath.ApplyUserPreamp(ParametricEqMath.VlcEqUnityPreampDb, -6.0), 3f);
        // Flat preset (preamp zeroed for the bypass) + user preamp: unity is
        // restored first, so the net change is exactly the user's dB.
        Assert.Equal(ParametricEqMath.VlcEqUnityPreampDb - 6f,
            ParametricEqMath.ApplyUserPreamp(0f, -6.0), 3f);
    }

    [Fact]
    public void ApplyUserPreamp_ClampsToVlcRangeAndUserBounds()
    {
        // User value beyond the UI bounds is clamped to them first.
        Assert.Equal(ParametricEqMath.VlcEqUnityPreampDb + (float)ParametricEqMath.EqPreampMaxDb,
            ParametricEqMath.ApplyUserPreamp(ParametricEqMath.VlcEqUnityPreampDb, 99.0), 3f);
        // Resolved preamp never leaves VLC's -20..20.
        Assert.Equal(-20f, ParametricEqMath.ApplyUserPreamp(-15f, -12.0));
    }

    [Fact]
    public void VlcEqUnityPreamp_CancelsVlcInputFactor()
    {
        // VLC's equalizer scales its input by EQZ_IN_FACTOR = 0.25 (equalizer.c);
        // the unity preamp must cancel it exactly or every non-flat custom curve
        // shifts the overall level (the "EQ makes everything quieter" bug).
        var linear = Math.Pow(10.0, ParametricEqMath.VlcEqUnityPreampDb / 20.0) * 0.25;
        Assert.Equal(1.0, linear, 3);
    }

    [Fact]
    public void FromGraphicBands_OutOfRangeGains_AreClamped()
    {
        var legacy = new float[] { 99, -99, 0, 0, 0, 0, 0, 0, 0, 0 };
        var bands = ParametricEqMath.FromGraphicBands(legacy);
        Assert.All(bands, b => Assert.InRange(b.GainDb, ParametricEqMath.MinGainDb, ParametricEqMath.MaxGainDb));
    }
}
