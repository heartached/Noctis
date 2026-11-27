using Avalonia;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #75: switching the mini player's style next to a screen edge grew the window
/// off the screen, because a resize keeps the top-left fixed. The new top-left keeps the
/// window on the edge it is nearest to (by its centre) and inside the work area.
/// </summary>
public class MiniPlayerPlacementTests
{
    // A 1920×1040 work area (taskbar below), and a second monitor to the LEFT of the
    // primary, so its coordinates are negative.
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect LeftMonitor = new(-2560, -200, 2560, 1400);

    [Theory]
    // Near the right edge: 420×188 card at x=1480 (right edge 1900) → 340×120 keeps x+w=1900.
    [InlineData(1480, 40, 420, 188, 340, 120, 1560, 40)]
    // Near the left edge: the left edge stays put.
    [InlineData(20, 40, 420, 188, 340, 520, 20, 40)]
    // Bottom half: the bottom edge (800 + 188 = 988) stays put.
    [InlineData(20, 800, 420, 188, 340, 520, 20, 468)]
    // Bottom-right corner: both anchors at once.
    [InlineData(1480, 800, 420, 188, 640, 412, 1260, 576)]
    // Growing past the bottom while in the TOP half (tall form): clamped up into the area.
    [InlineData(100, 400, 420, 188, 340, 840, 100, 200)]
    public void Anchored_KeepsTheNearestEdge_ThenClamps(
        int x, int y, int w, int h, double targetW, double targetH, int expectedX, int expectedY)
    {
        var current = new PixelRect(x, y, w, h);
        var p = MiniPlayerPlacement.Anchored(current, targetW, targetH, 1.0, Primary);
        Assert.Equal(new PixelPoint(expectedX, expectedY), p);
    }

    [Fact]
    public void Anchored_LargerThanTheArea_PinsToItsTopLeft()
    {
        var small = new PixelRect(0, 0, 800, 600);
        var p = MiniPlayerPlacement.Anchored(new PixelRect(500, 400, 200, 150), 960, 840, 1.0, small);
        Assert.Equal(new PixelPoint(0, 0), p);
    }

    [Theory]
    // Right half of the left monitor (centre x > -1280): keeps the right edge.
    [InlineData(-500, -100, 420, 188, -580, -100)]
    // Left half, off its left edge: clamped to -2560.
    [InlineData(-2600, 300, 420, 188, -2560, 300)]
    public void Anchored_OnAMonitorWithNegativeCoordinates(int x, int y, int w, int h, int expectedX, int expectedY)
    {
        var p = MiniPlayerPlacement.Anchored(new PixelRect(x, y, w, h), 500, 188, 1.0, LeftMonitor);
        Assert.Equal(new PixelPoint(expectedX, expectedY), p);
    }

    [Theory]
    // 340×120 DIPs at 125% = 425×150 px; right edge 1900 kept → x = 1475.
    [InlineData(1.25, 1475, 40)]
    // At 150% = 510×180 px → x = 1390.
    [InlineData(1.5, 1390, 40)]
    public void Anchored_ConvertsTheTargetSizeWithTheScreenScaling(double scaling, int expectedX, int expectedY)
    {
        // Current window already in px: 420×188 DIPs at this scaling, right edge at 1900.
        var size = MiniPlayerPlacement.ToPixels(420, 188, scaling);
        var current = new PixelRect(1900 - size.Width, 40, size.Width, size.Height);
        var p = MiniPlayerPlacement.Anchored(current, 340, 120, scaling, Primary);
        Assert.Equal(new PixelPoint(expectedX, expectedY), p);
    }

    [Theory]
    [InlineData(-50, -50, 0, 0)]          // off the top-left
    [InlineData(1800, 1000, 1500, 852)]   // off the bottom-right (420×188)
    [InlineData(300, 300, 300, 300)]      // already inside: untouched
    public void Clamp_PullsTheWindowInsideTheWorkArea(int x, int y, int expectedX, int expectedY)
    {
        var p = MiniPlayerPlacement.Clamp(new PixelPoint(x, y), new PixelSize(420, 188), Primary);
        Assert.Equal(new PixelPoint(expectedX, expectedY), p);
    }

    // ── GitHub #251: reopening a design parked in a corner ──────────────────────
    // A fixed design's window is its own size, but the stored size is the classic card's
    // (420×188 = the Bar default when Classic was never resized). The reopen clamp used the
    // classic size, so a design flush with the right/bottom edge came back moved inward.

    private static readonly PixelRect FullHd = new(0, 0, 1920, 1080);

    /// <summary>Top-left of a window of <paramref name="size"/> flush in a corner.</summary>
    private static PixelPoint Corner(string corner, PixelSize size, PixelRect area) => corner switch
    {
        "TL" => new PixelPoint(area.X, area.Y),
        "TR" => new PixelPoint(area.Right - size.Width, area.Y),
        "BR" => new PixelPoint(area.Right - size.Width, area.Bottom - size.Height),
        "BL" => new PixelPoint(area.X, area.Bottom - size.Height),
        _ => throw new ArgumentOutOfRangeException(nameof(corner)),
    };

    /// <summary>What reopening did: Clamp with the size MainWindow measured.</summary>
    private static PixelPoint Reopen(PixelPoint saved, double w, double h, double scaling, PixelRect area)
        => MiniPlayerPlacement.Clamp(saved, MiniPlayerPlacement.ToPixels(w, h, scaling), area);

    [Theory]
    // Pill (360×140) with the stored classic 420×188: old code shifted left/up by the
    // difference in pixels — 60/48 at 100%, 75/60 at 125%, 90/72 at 150%.
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.0, "TL", 0, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.0, "TR", -60, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.0, "BR", -60, -48)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.0, "BL", 0, -48)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.25, "TL", 0, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.25, "TR", -75, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.25, "BR", -75, -60)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.25, "BL", 0, -60)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.5, "TL", 0, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.5, "TR", -90, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.5, "BR", -90, -72)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Pill, 1.5, "BL", 0, -72)]
    // Sleeve (340×372) is narrower but taller than 420×188: only the right edge moved.
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Sleeve, 1.0, "TR", -80, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Sleeve, 1.0, "BR", -80, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Sleeve, 1.25, "BR", -100, 0)]
    [InlineData(Noctis.ViewModels.MiniPlayerForm.Sleeve, 1.5, "BR", -120, 0)]
    public void Design_ReopensExactlyWhereItWasLeft_InEveryCorner(
        Noctis.ViewModels.MiniPlayerForm design, double scaling, string corner, int oldDx, int oldDy)
    {
        var (dw, dh) = Noctis.ViewModels.MiniPlayerViewModel.CanonicalSize(design);
        var saved = Corner(corner, MiniPlayerPlacement.ToPixels(dw, dh, scaling), FullHd);

        // Before: measured at the classic size.
        var old = Reopen(saved, 420, 188, scaling, FullHd);
        Assert.Equal(new PixelPoint(saved.X + oldDx, saved.Y + oldDy), old);

        // Now: measured at the size the design opens at.
        var (w, h) = MiniPlayerPlacement.OpeningSize(design, 420, 188);
        Assert.Equal(saved, Reopen(saved, w, h, scaling, FullHd));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Classic_ReopensExactlyWhereItWasLeft_InEveryCorner(double scaling)
    {
        foreach (var corner in new[] { "TL", "TR", "BR", "BL" })
        {
            var saved = Corner(corner, MiniPlayerPlacement.ToPixels(420, 188, scaling), FullHd);
            var (w, h) = MiniPlayerPlacement.OpeningSize(null, 420, 188);
            Assert.Equal(saved, Reopen(saved, w, h, scaling, FullHd));
        }
    }

    [Theory]
    [InlineData("TL")]
    [InlineData("TR")]
    [InlineData("BR")]
    [InlineData("BL")]
    public void Design_OnAMonitorWithNegativeCoordinates_ReopensWhereItWasLeft(string corner)
    {
        var (w, h) = MiniPlayerPlacement.OpeningSize(Noctis.ViewModels.MiniPlayerForm.Pill, 420, 188);
        var saved = Corner(corner, MiniPlayerPlacement.ToPixels(w, h, 1.25), LeftMonitor);
        Assert.Equal(saved, Reopen(saved, w, h, 1.25, LeftMonitor));
    }

    [Fact]
    public void Design_SavedPartlyOffScreen_IsStillPulledBackInside()
    {
        var (w, h) = MiniPlayerPlacement.OpeningSize(Noctis.ViewModels.MiniPlayerForm.Pill, 420, 188);
        // Half off the bottom-right (e.g. the taskbar moved or the resolution dropped).
        var p = Reopen(new PixelPoint(1800, 1000), w, h, 1.0, FullHd);
        Assert.Equal(new PixelPoint(1920 - 360, 1080 - 140), p);
    }

    [Fact]
    public void ToPixels_TreatsAMissingScalingAsOne()
    {
        Assert.Equal(new PixelSize(420, 188), MiniPlayerPlacement.ToPixels(420, 188, 0));
        Assert.Equal(new PixelSize(420, 188), MiniPlayerPlacement.ToPixels(420, 188, double.NaN));
    }
}
