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
            Assert.Equal(legacy[i], bands[i].GainDb, 6);
            Assert.Equal(ParametricEqMath.DefaultQ, bands[i].Q);
        }
    }

    [Fact]
    public void FromGraphicBands_OutOfRangeGains_AreClamped()
    {
        var legacy = new float[] { 99, -99, 0, 0, 0, 0, 0, 0, 0, 0 };
        var bands = ParametricEqMath.FromGraphicBands(legacy);
        Assert.Equal(ParametricEqMath.MaxGainDb, bands[0].GainDb);
        Assert.Equal(ParametricEqMath.MinGainDb, bands[1].GainDb);
    }
}
