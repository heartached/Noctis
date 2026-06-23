using System;
using System.Collections.Generic;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ShareClipTimingTests
{
    private static ShareClipLine L(double? seconds, bool selected) =>
        new(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, selected);

    [Fact]
    public void Compute_SyncedMiddleSelection_UsesNextLineAsEnd()
    {
        var lines = new List<ShareClipLine> { L(0, false), L(10, true), L(14, true), L(30, false) };
        var t = ShareClipTiming.Compute(lines, null);
        Assert.Equal(10, t.StartSeconds, 3);
        Assert.Equal(20, t.DurationSeconds, 3); // 30 - 10, within [6,60]
    }

    [Fact]
    public void Compute_ShortSpan_ClampedUpToMin()
    {
        var lines = new List<ShareClipLine> { L(10, true), L(13, false) };
        var t = ShareClipTiming.Compute(lines, null);
        Assert.Equal(10, t.StartSeconds, 3);
        Assert.Equal(6, t.DurationSeconds, 3); // 13-10=3 → floored to 6
    }

    [Fact]
    public void Compute_LongSpan_ClampedDownToMax()
    {
        var lines = new List<ShareClipLine> { L(0, true), L(200, false) };
        var t = ShareClipTiming.Compute(lines, null);
        Assert.Equal(0, t.StartSeconds, 3);
        Assert.Equal(60, t.DurationSeconds, 3); // 200 → capped to 60
    }

    [Fact]
    public void Compute_SelectionIncludesFinalLine_UsesFallback()
    {
        var lines = new List<ShareClipLine> { L(0, false), L(50, true), L(54, true) };
        var t = ShareClipTiming.Compute(lines, null);
        Assert.Equal(50, t.StartSeconds, 3);
        Assert.Equal(15, t.DurationSeconds, 3); // no line after last selected → fallback
    }

    [Fact]
    public void Compute_Unsynced_UsesPlaybackPositionAndFallback()
    {
        var lines = new List<ShareClipLine> { L(null, true), L(null, true) };
        var t = ShareClipTiming.Compute(lines, 42.0);
        Assert.Equal(42, t.StartSeconds, 3);
        Assert.Equal(15, t.DurationSeconds, 3);
    }

    [Fact]
    public void Compute_UnsyncedNoPosition_StartsAtZero()
    {
        var lines = new List<ShareClipLine> { L(null, true) };
        var t = ShareClipTiming.Compute(lines, null);
        Assert.Equal(0, t.StartSeconds, 3);
        Assert.Equal(15, t.DurationSeconds, 3);
    }
}
