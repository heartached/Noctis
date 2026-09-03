using System.Text.RegularExpressions;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A lyric line change used to be two motions on two clocks: the line's own scale
/// transition (a 450ms fast-opening spring, started the instant it went active) and
/// the scroll glide (650–1050ms smootherstep, started a tick later, opening at zero
/// velocity). The line popped to full size before the list had visibly moved. Both
/// now share <see cref="LineMotion"/>; these pin the curve and the XAML side of it.
/// The same XAML pins guard the "lyrics lift while sung" report: the active line and
/// regular words no longer translate — only held-note emphasis words float.
/// </summary>
public class LyricsMotionTests
{
    private static readonly string[] LyricSurfaces =
    {
        Path.Combine("src", "Noctis", "Views", "LyricsView.axaml"),
        Path.Combine("src", "Noctis", "Views", "LyricsPanelView.axaml"),
        Path.Combine("src", "Noctis", "Views", "MiniPlayerWindow.axaml"),
    };

    [Fact]
    public void Ease_StartsAtZero_EndsAtOne_OvershootsLessThanAPixel()
    {
        Assert.Equal(0, LineMotion.Ease(0));
        Assert.Equal(1, LineMotion.Ease(1));
        var prev = 0.0;
        var crossed = false;
        for (var i = 1; i <= 400; i++)
        {
            var v = LineMotion.Ease(i / 400.0);
            if (!crossed && v >= 0.99) crossed = true;
            if (!crossed)
                Assert.True(v >= prev, $"not monotone at {i / 400.0} before arrival");
            // A 131px line change can't show less than a pixel of overshoot.
            Assert.True(v <= 1 + 1.0 / 131, $"visible overshoot at {i / 400.0}: {v}");
            prev = v;
        }
        // Settled at the end of the duration — no tail after the transition ends.
        Assert.True(LineMotion.Ease(0.999) > 0.99);
    }

    /// <summary>
    /// The report that drove the curve choice: "it flows, stops, then moves again for a
    /// split second." A critically damped spring's exponential tail left ~3.5px of
    /// travel AFTER its speed had dropped below what the eye tracks (~30px/s on a
    /// 131px line change), read as a second small drift. The curve must arrive with
    /// under a pixel left once it looks stopped.
    /// </summary>
    [Fact]
    public void Ease_ArrivesCrisply_NoCreepAfterThePerceivedStop()
    {
        const double travelPx = 131;       // one ordinary line change
        const double perceptualPxPerSec = 30;
        const int n = 6500;
        var t = LineMotion.DurationMs / 1000.0;
        var pos = Enumerable.Range(0, n + 1).Select(i => travelPx * LineMotion.Ease(i / (double)n)).ToArray();
        var lastFast = 0;
        for (var i = 0; i < n; i++)
        {
            var v = Math.Abs(pos[i + 1] - pos[i]) / (t / n);
            if (v > perceptualPxPerSec) lastFast = i;
        }
        var creep = pos.Skip(lastFast).Max(p => Math.Abs(travelPx - p));
        Assert.True(creep < 1.0, $"{creep:F2}px of travel left after the motion reads as stopped at {lastFast * t / n * 1000:F0}ms");
        // And it still opens fast — that is the whole point of the spring over smootherstep.
        Assert.True(LineMotion.Ease(0.15) > 0.2);
    }

    [Fact]
    public void Ease_AcceleratesAtOnce_ButDoesNotPop()
    {
        // Smootherstep spent the first ~25% of the glide nearly motionless (6% at p=0.15);
        // the spring is well under way by then, without a step at p→0.
        Assert.True(LineMotion.Ease(0.15) > 0.2, $"too slow to open: {LineMotion.Ease(0.15):F3}");
        Assert.True(LineMotion.Ease(0.02) < 0.02, $"opens with a step: {LineMotion.Ease(0.02):F3}");
    }

    [Fact]
    public void HalfTravel_IsWhereTheCurveCrossesOneHalf()
    {
        Assert.Equal(0.5, LineMotion.Ease(LineMotion.HalfTravelMs / LineMotion.DurationMs), 0.001);
        Assert.InRange(LineMotion.HalfTravelMs, 100, 250);
    }

    [Fact]
    public void EveryLyricSurface_UsesTheSharedCurveAndDurationForLineChanges()
    {
        var expectedDuration = $"Duration=\"0:0:{LineMotion.DurationMs / 1000.0:0.##}\"";
        foreach (var rel in LyricSurfaces)
        {
            var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), rel));
            var lineStyle = LineButtonStyle(xaml, rel);
            Assert.Contains("helpers:LineMotionEase", lineStyle);
            Assert.Contains(expectedDuration, lineStyle);
            Assert.DoesNotContain("SpringEase", xaml);
        }
    }

    [Fact]
    public void ActiveLinesAndRegularWords_DoNotTranslate_OnlyEmphasisWordsFloat()
    {
        foreach (var rel in LyricSurfaces)
        {
            var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), rel));
            foreach (Match m in Regex.Matches(xaml, @"translate\(0px,-\d+(?:\.\d+)?px\)"))
            {
                var selector = OwningSelector(xaml, m.Index);
                Assert.True(selector.Contains("emphasis") && selector.Contains("current"),
                    $"{rel}: lift '{m.Value}' under selector '{selector}' — only word-cell.emphasis.current may float");
            }
        }
    }

    /// <summary>The resting/active line-button style block for that surface.</summary>
    private static string LineButtonStyle(string xaml, string rel)
    {
        var selector = rel.Contains("Panel") ? "Button.panel-lyric-line-btn\""
            : rel.Contains("MiniPlayer") ? "Button.mini-lyric-line\""
            : "Button.lyric-line-btn\"";
        var start = xaml.IndexOf("<Style Selector=\"" + selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{rel}: line button style not found");
        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        return xaml.Substring(start, end - start);
    }

    private static string OwningSelector(string xaml, int index)
    {
        var start = xaml.LastIndexOf("<Style Selector=\"", index, StringComparison.Ordinal);
        if (start < 0) return "";
        start += "<Style Selector=\"".Length;
        var end = xaml.IndexOf('"', start);
        return xaml.Substring(start, end - start);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
