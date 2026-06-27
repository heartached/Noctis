using System;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

public class NowPlayingBrushConverterTests
{
    [Fact]
    public void IsMatch_SameNonEmptyGuid_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        Assert.True(NowPlayingBrushConverter.IsMatch(id, id));
    }

    [Fact]
    public void IsMatch_DifferentGuids_ReturnsFalse()
    {
        Assert.False(NowPlayingBrushConverter.IsMatch(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void IsMatch_CurrentNull_ReturnsFalse()
    {
        Assert.False(NowPlayingBrushConverter.IsMatch(Guid.NewGuid(), null));
    }

    [Fact]
    public void IsMatch_BothEmptyGuid_ReturnsFalse()
    {
        // Guid.Empty is a "no track" sentinel; two empties must not all highlight.
        Assert.False(NowPlayingBrushConverter.IsMatch(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void IsMatch_NonGuidValues_ReturnsFalse()
    {
        Assert.False(NowPlayingBrushConverter.IsMatch("x", "x"));
    }
}
