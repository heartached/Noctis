using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi 11-25): drag the sidebar's right edge to collapse or expand it, and Ctrl/⌘+B
/// to do the same, instead of Settings › Keep sidebar expanded. Discord 11-27: within one held
/// drag the state follows the pointer back every time it turns (it flipped once, then stuck),
/// and the expanded sidebar resizes like Spotify's library (Halil); a click on the edge folds
/// or unfolds it like Ctrl+B (Duckyvictus). The real strip and rail from MainWindow.axaml,
/// driven by the headless mouse and touch.
/// </summary>
public class SidebarResizeGripTests
{
    private sealed class Rig
    {
        public required Window Host;
        public required Border Grip;
        public required Border Rail;
        public required Border Page;
        public SidebarResizeGrip Drag = null!;
        public bool Hidden;
        public bool Pinned;
        public double Saved = SidebarWidthMetrics.Default;
        public int Flips;
        public readonly List<double> Commits = new();

        public void Pump()
        {
            for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        }

        public void Down(double x) { Host.MouseDown(new Point(x, 300), MouseButton.Left); Pump(); }
        public void Move(double x) { Host.MouseMove(new Point(x, 300), RawInputModifiers.LeftMouseButton); Pump(); }
        public void Up(double x) { Host.MouseUp(new Point(x, 300), MouseButton.Left); Pump(); }

        /// <summary>Left edge of the grip as laid out.</summary>
        public double GripLeft => Grip.Bounds.Left;

        /// <summary>What MainWindow lays out for the state: rail width and page margin.</summary>
        public void Layout()
        {
            var width = Pinned ? Drag.ExpandedWidth : SidebarEdgePeek.RailWidth;
            Rail.Width = width;
            Page.Margin = new Thickness(width + 16, 0, 0, 0);
            Drag.Sync();
            Pump();
        }
    }

    private static Rig Build(bool pinned, double saved = SidebarWidthMetrics.Default)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        var grip = main.FindControl<Border>("SidebarResizeGrip");
        Assert.NotNull(grip);
        var rail = main.FindControl<Border>("SidebarWrapper")!;
        ((Panel)grip.Parent!).Children.Remove(grip);
        ((Panel)rail.Parent!).Children.Remove(rail);
        rail.Transitions = null;

        // The page starts where MainWindow's RootPanel margin puts it (rail + 16).
        var page = new Border { Background = Avalonia.Media.Brushes.Black };
        var host = new Window { Width = 1200, Height = 700, Content = new Panel { Children = { page, grip, rail } } };
        var rig = new Rig { Host = host, Grip = grip, Rail = rail, Page = page, Pinned = pinned, Saved = saved };
        rig.Drag = new SidebarResizeGrip(grip, () => rig.Hidden, () => rig.Pinned,
            p =>
            {
                if (rig.Pinned == p) return false;
                rig.Pinned = p;
                rig.Flips++;
                rig.Layout();
                return true;
            },
            () => rig.Saved,
            width =>
            {
                rig.Rail.Width = width;
                rig.Page.Margin = new Thickness(width + 16, 0, 0, 0);
            },
            width =>
            {
                rig.Commits.Add(width);
                rig.Saved = SidebarWidthMetrics.Clamp(width);
                rig.Layout();
            });
        host.Show();
        rig.Layout();
        rig.Move(600);
        return rig;
    }

    [Theory]
    [InlineData(true, 110, true)]     // CollapseBelow: still expanded
    [InlineData(true, 109.9, false)]
    [InlineData(true, 120, true)]     // the dead band keeps the state either way
    [InlineData(false, 120, false)]
    [InlineData(false, 130, false)]   // ExpandAbove: still the rail
    [InlineData(false, 130.1, true)]
    [InlineData(true, 500, true)]
    [InlineData(false, -200, false)]
    public void Decide_SnapsPastTheDeadBand_AroundTheSnapPoint(bool pinned, double width, bool expected)
    {
        Assert.Equal(120, SidebarResizeGrip.SnapPoint); // halfway between the rail (60) and the min (180)
        Assert.Equal(expected, SidebarResizeGrip.Decide(pinned, width));
    }

    [AvaloniaFact]
    public void Grip_SitsInTheGapBetweenRailAndPage_AndShowsAResizeCursor()
    {
        var r = Build(pinned: true);
        Assert.Equal(228, r.GripLeft); // rail 8 + 220
        Assert.Equal(8, r.Grip.Bounds.Width); // ends at 236, where the page begins
        Assert.NotNull(r.Grip.Cursor);
        Assert.True(r.Grip.ZIndex < r.Rail.ZIndex, "the hover-expanded rail must cover the strip");

        r.Down(232);
        r.Move(100);
        r.Up(100);
        Assert.Equal(68, r.GripLeft); // rail 8 + 60, page from 76
    }

    /// <summary>The 11-27 report: drag back and forth while holding, and it only ever went one
    /// way (SidebarResizeGrip flipped once per drag, measured from the press).</summary>
    [AvaloniaFact]
    public void OneHeldDrag_FollowsThePointer_EveryTimeItTurns()
    {
        var r = Build(pinned: true);
        r.Down(232); // 4 px into the strip: the pointer asks for a rail of x - 12
        r.Move(110);
        Assert.False(r.Pinned);
        Assert.Equal(SidebarEdgePeek.RailWidth, r.Rail.Width);
        r.Move(150);
        Assert.True(r.Pinned);
        Assert.Equal(SidebarWidthMetrics.Min, r.Rail.Width); // opens at the narrowest, short of the pointer
        r.Move(115);
        Assert.False(r.Pinned);
        r.Move(300);
        Assert.True(r.Pinned);
        Assert.Equal(288, r.Rail.Width); // past the min it follows the pointer
        Assert.Equal(296, r.GripLeft);
        Assert.Equal(304, r.Page.Margin.Left);
        r.Up(300);
        Assert.Equal(4, r.Flips);
        Assert.False(r.Drag.IsDragging);
        Assert.Equal(new[] { 288.0 }, r.Commits);
    }

    [AvaloniaFact]
    public void AtTheSnapPoint_ItDoesNotFlicker()
    {
        var r = Build(pinned: true);
        r.Down(232);
        foreach (var x in new[] { 130.0, 125, 135, 122 }) // asks for 110..123: inside the band
            r.Move(x);
        Assert.True(r.Pinned);
        Assert.Equal(0, r.Flips);
        r.Move(121); // 109: collapses
        foreach (var x in new[] { 125.0, 140, 130, 142 }) // 113..130: still inside it
            r.Move(x);
        Assert.False(r.Pinned);
        Assert.Equal(1, r.Flips);
        r.Move(143); // 131: opens
        Assert.True(r.Pinned);
        Assert.Equal(2, r.Flips);
        r.Up(143);
    }

    [AvaloniaFact]
    public void DraggingTheExpandedEdge_ResizesLive_WithinTheRange_AndSavesOnRelease()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Move(272);
        Assert.Equal(260, r.Rail.Width);
        Assert.Equal(268, r.GripLeft);
        Assert.Empty(r.Commits); // live: saved only on release
        r.Move(900);
        Assert.Equal(SidebarWidthMetrics.Max, r.Rail.Width);
        r.Move(160); // asks for 148: the narrowest, not yet the rail
        Assert.True(r.Pinned);
        Assert.Equal(SidebarWidthMetrics.Min, r.Rail.Width);
        r.Move(250);
        r.Up(250);
        Assert.Equal(new[] { 238.0 }, r.Commits);
        Assert.Equal(238, r.Saved);
        Assert.Equal(0, r.Flips);
    }

    [AvaloniaFact]
    public void TheRail_OpensAtTheNarrowest_NotTheSavedWidth_ThenFollows()
    {
        var r = Build(pinned: false, saved: 300);
        Assert.Equal(68, r.GripLeft);
        r.Down(72);
        r.Move(150);
        Assert.True(r.Pinned);
        Assert.Equal(SidebarWidthMetrics.Min, r.Rail.Width);
        r.Move(250);
        Assert.Equal(238, r.Rail.Width);
        r.Up(250);
        Assert.Equal(new[] { 238.0 }, r.Commits);
        Assert.Equal(1, r.Flips);
    }

    [AvaloniaFact]
    public void ADragThatEndsOnTheRail_KeepsTheSavedWidth_ForTheNextExpand()
    {
        var r = Build(pinned: true, saved: 300);
        Assert.Equal(308, r.GripLeft);
        r.Down(312);
        r.Move(250); // resized to 238 first
        r.Move(100); // then collapsed
        r.Up(100);
        Assert.False(r.Pinned);
        Assert.Empty(r.Commits);
        Assert.Equal(300, r.Drag.ExpandedWidth); // Ctrl+B / hover open at the saved width
    }

    [Theory]
    [InlineData(213, 213)]
    [InlineData(214, 220)]   // within the detent: the stock width
    [InlineData(226, 220)]
    [InlineData(227, 227)]
    [InlineData(100, SidebarWidthMetrics.Min)]
    [InlineData(5000, SidebarWidthMetrics.Max)]
    public void Resized_ClampsToTheRange_AndHoldsAtTheStockWidth(double asked, double expected)
        => Assert.Equal(expected, SidebarResizeGrip.Resized(asked));

    [AvaloniaFact]
    public void AClick_FoldsAndUnfolds_AtTheSavedWidth()
    {
        var r = Build(pinned: true, saved: 300);
        r.Down(312);
        r.Up(312);
        Assert.False(r.Pinned);
        Assert.Equal(SidebarEdgePeek.RailWidth, r.Rail.Width);
        Assert.Equal(68, r.GripLeft);
        r.Down(72);
        r.Up(72);
        Assert.True(r.Pinned);
        Assert.Equal(300, r.Rail.Width); // the saved width, as Ctrl+B
        Assert.Equal(308, r.GripLeft);
        Assert.Equal(2, r.Flips);
        Assert.Empty(r.Commits);
        Assert.False(r.Drag.IsDragging);
    }

    [AvaloniaFact]
    public void AWobbleWithinTheSlop_IsStillAClick_AndDoesNotResize()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Move(234);
        r.Move(229);
        Assert.Equal(SidebarWidthMetrics.Default, r.Rail.Width);
        r.Up(231);
        Assert.False(r.Pinned);
        Assert.Equal(1, r.Flips);
        Assert.Empty(r.Commits);
    }

    [AvaloniaFact]
    public void ADragPastTheSlop_DoesNotToggleOnRelease_EvenBackAtThePress()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Move(260); // resized to 248
        Assert.Equal(248, r.Rail.Width);
        r.Move(232);
        r.Up(232);
        Assert.True(r.Pinned);
        Assert.Equal(0, r.Flips);
        Assert.Empty(r.Commits); // back at the saved width: nothing to save
        Assert.Equal(SidebarWidthMetrics.Default, r.Rail.Width);
    }

    [AvaloniaFact]
    public void AfterAnInDragFlip_TheReleaseDoesNotToggleAgain()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Move(100); // collapsed by the drag
        r.Up(100);
        Assert.False(r.Pinned);
        Assert.Equal(1, r.Flips);

        r.Down(72);
        r.Move(150); // opened by the drag
        r.Move(72);  // folded again, back where it was pressed
        r.Move(150); // and opened again
        r.Up(150);
        Assert.True(r.Pinned);
        Assert.Equal(4, r.Flips);
    }

    [AvaloniaFact]
    public void Touch_TapToggles_AndDragResizes()
    {
        var r = Build(pinned: true);
        var tap = r.Host.TouchBegin(new Point(232, 300));
        r.Pump();
        r.Host.TouchEnd(tap, new Point(232, 300));
        r.Pump();
        Assert.False(r.Pinned);
        Assert.Equal(1, r.Flips);

        var drag = r.Host.TouchBegin(new Point(72, 300));
        r.Pump();
        r.Host.TouchMove(drag, new Point(200, 300));
        r.Pump();
        r.Host.TouchMove(drag, new Point(280, 300));
        r.Pump();
        r.Host.TouchEnd(drag, new Point(280, 300));
        r.Pump();
        Assert.True(r.Pinned);
        Assert.Equal(268, r.Rail.Width);
        Assert.Equal(2, r.Flips);
        Assert.Equal(new[] { 268.0 }, r.Commits);
    }

    [AvaloniaFact]
    public void TheGrip_NamesBothActions_AndIsNoTabStop()
    {
        var r = Build(pinned: true);
        var tip = ToolTip.GetTip(r.Grip) as string;
        Assert.False(string.IsNullOrEmpty(tip));
        Assert.Contains("B)", tip);
        Assert.False(r.Grip.Focusable);
    }

    [AvaloniaFact]
    public void ADragBackToTheSameWidth_ChangesNothing()
    {
        var r = Build(pinned: true);
        r.Down(229);
        r.Move(200);
        r.Move(229);
        r.Up(229);
        Assert.True(r.Pinned);
        Assert.Equal(0, r.Flips);
        Assert.Empty(r.Commits);
        Assert.Equal(SidebarWidthMetrics.Default, r.Rail.Width);
    }

    [AvaloniaFact]
    public void PressingThePage_NextToTheGrip_DoesNotDrag()
    {
        var r = Build(pinned: true);
        r.Down(240); // the page's first pixels (the album grid's first column)
        r.Move(150);
        r.Up(150);
        Assert.True(r.Pinned);
        Assert.False(r.Drag.IsDragging);
    }

    [AvaloniaFact]
    public void HiddenSidebar_HasNoGrip()
    {
        var r = Build(pinned: true);
        r.Hidden = true;
        r.Drag.Sync();
        Assert.False(r.Grip.IsVisible);
        r.Hidden = false;
        r.Drag.Sync();
        Assert.True(r.Grip.IsVisible);
    }

    [AvaloniaFact]
    public void TrySetSidebarPinned_FlipsKeepExpanded_ButNotWithHideUntilHover()
    {
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        Assert.False(settings.SidebarAlwaysExpanded);

        Assert.True(settings.TrySetSidebarPinned(true));
        Assert.True(settings.SidebarAlwaysExpanded);
        Assert.False(settings.TrySetSidebarPinned(true)); // no change

        Assert.True(settings.TrySetSidebarPinned(false));
        Assert.False(settings.SidebarAlwaysExpanded);

        // GitHub #233: a pinned-open sidebar and a hidden one cannot both hold.
        settings.SidebarAutoHide = true;
        Assert.False(settings.TrySetSidebarPinned(true));
        Assert.False(settings.SidebarAlwaysExpanded);
    }

    [AvaloniaFact]
    public void SidebarExpandedWidth_DefaultsToTheOldWidth_AndStaysInRange()
    {
        Assert.Equal(220, new AppSettings().SidebarExpandedWidth);
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        Assert.Equal(220, settings.SidebarExpandedWidth);
        settings.SetSidebarExpandedWidth(271.6);
        Assert.Equal(272, settings.SidebarExpandedWidth);
        settings.SetSidebarExpandedWidth(5000);
        Assert.Equal(SidebarWidthMetrics.Max, settings.SidebarExpandedWidth);
        settings.SidebarExpandedWidth = 10; // a binding or a reset writing past the range
        Assert.Equal(SidebarWidthMetrics.Min, settings.SidebarExpandedWidth);
        settings.SetSidebarExpandedWidth(double.NaN);
        Assert.Equal(SidebarWidthMetrics.Default, settings.SidebarExpandedWidth);

        // A hand-edited or older settings file is clamped on load.
        var stored = new AppSettings { SidebarExpandedWidth = 9999 };
        stored.ClampToValidRanges();
        Assert.Equal(SidebarWidthMetrics.Max, stored.SidebarExpandedWidth);
        stored.SidebarExpandedWidth = double.NegativeInfinity;
        stored.ClampToValidRanges();
        Assert.Equal(SidebarWidthMetrics.Default, stored.SidebarExpandedWidth);
    }

    [Fact]
    public void ToggleSidebar_IsCtrlB_OnWindows_AndCmdB_OnMac_AndWorksFromTheSearchBox()
    {
        Assert.Equal(new KeyGesture(Key.B, KeyModifiers.Control), ShortcutDefaults.For(ShortcutAction.ToggleSidebar, isMac: false));
        Assert.Equal(new KeyGesture(Key.B, KeyModifiers.Meta), ShortcutDefaults.For(ShortcutAction.ToggleSidebar, isMac: true));
        Assert.False(ShortcutDefaults.IsTextBoxKey(Key.B, KeyModifiers.Control));
        Assert.Contains(ShortcutDefaults.All, d => d.Action == ShortcutAction.ToggleSidebar && d.Group == ShortcutDefaults.GroupWindow);
    }
}
