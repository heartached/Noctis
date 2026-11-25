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
/// to do the same, instead of Settings › Keep sidebar expanded. The real strip and rail from
/// MainWindow.axaml, driven by the headless mouse.
/// </summary>
public class SidebarResizeGripTests
{
    private sealed class Rig
    {
        public required Window Host;
        public required Border Grip;
        public required Border Rail;
        public required SidebarResizeGrip Drag;
        public bool Hidden;
        public bool Pinned;
        public int Flips;

        public void Pump()
        {
            for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        }

        public void Down(double x) { Host.MouseDown(new Point(x, 300), MouseButton.Left); Pump(); }
        public void Move(double x) { Host.MouseMove(new Point(x, 300)); Pump(); }
        public void Up(double x) { Host.MouseUp(new Point(x, 300), MouseButton.Left); Pump(); }

        /// <summary>Left edge of the grip as laid out.</summary>
        public double GripLeft => Grip.Bounds.Left;
    }

    private static Rig Build(bool pinned)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        var grip = main.FindControl<Border>("SidebarResizeGrip");
        Assert.NotNull(grip);
        var rail = main.FindControl<Border>("SidebarWrapper")!;
        ((Panel)grip.Parent!).Children.Remove(grip);
        ((Panel)rail.Parent!).Children.Remove(rail);
        rail.Transitions = null;

        // The page starts where MainWindow's RootPanel margin puts it (76 / 236).
        var page = new Border { Background = Avalonia.Media.Brushes.Black };
        var host = new Window { Width = 1200, Height = 700, Content = new Panel { Children = { page, grip, rail } } };
        Rig? rig = null;
        void Layout()
        {
            rail.Width = rig!.Pinned ? SidebarEdgePeek.ExpandedWidth : SidebarEdgePeek.RailWidth;
            page.Margin = new Thickness(rig.Pinned ? 236 : 76, 0, 0, 0);
        }
        var drag = new SidebarResizeGrip(grip, () => rig!.Hidden, () => rig!.Pinned, p =>
        {
            if (rig!.Pinned == p) return false;
            rig.Pinned = p;
            rig.Flips++;
            Layout();
            return true;
        });
        rig = new Rig { Host = host, Grip = grip, Rail = rail, Drag = drag, Pinned = pinned };
        Layout();
        drag.Sync();
        host.Show();
        rig.Move(600);
        return rig;
    }

    [Theory]
    [InlineData(true, -39, null)]
    [InlineData(true, -40, false)]
    [InlineData(true, 200, null)]   // already expanded: dragging further right does nothing
    [InlineData(false, 39, null)]
    [InlineData(false, 40, true)]
    [InlineData(false, -200, null)] // already the rail: dragging further left does nothing
    [InlineData(true, 0, null)]
    [InlineData(false, 0, null)]
    public void Decide_SnapsOnlyPastTheThreshold_TowardsTheOtherState(bool pinned, double dx, bool? expected)
        => Assert.Equal(expected, SidebarResizeGrip.Decide(pinned, dx));

    [AvaloniaFact]
    public void Grip_SitsInTheGapBetweenRailAndPage_AndShowsAResizeCursor()
    {
        var r = Build(pinned: true);
        Assert.Equal(228, r.GripLeft); // rail 8 + 220
        Assert.Equal(8, r.Grip.Bounds.Width); // ends at 236, where the page begins
        Assert.NotNull(r.Grip.Cursor);
        Assert.True(r.Grip.ZIndex < r.Rail.ZIndex, "the hover-expanded rail must cover the strip");

        r.Down(232);
        r.Move(180);
        r.Up(180);
        Assert.Equal(68, r.GripLeft); // rail 8 + 60, page from 76
    }

    [AvaloniaFact]
    public void DraggingAnExpandedSidebarLeft_CollapsesIt_Once()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Move(210); // 22 px: not yet
        Assert.True(r.Pinned);
        r.Move(190); // 42 px
        Assert.False(r.Pinned);
        r.Move(300); // back the other way in the same drag: no flip-flop
        r.Up(300);
        Assert.False(r.Pinned);
        Assert.Equal(1, r.Flips);
        Assert.False(r.Drag.IsDragging);
    }

    [AvaloniaFact]
    public void DraggingTheRailRight_ExpandsAndPinsIt()
    {
        var r = Build(pinned: false);
        r.Down(72);
        r.Move(130);
        r.Up(130);
        Assert.True(r.Pinned);
        Assert.Equal(1, r.Flips);
    }

    [AvaloniaFact]
    public void APlainClick_OrATinyWiggle_DoesNothing()
    {
        var r = Build(pinned: true);
        r.Down(232);
        r.Up(232);
        r.Down(232);
        r.Move(220);
        r.Up(220);
        Assert.True(r.Pinned);
        Assert.Equal(0, r.Flips);
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

    [Fact]
    public void ToggleSidebar_IsCtrlB_OnWindows_AndCmdB_OnMac_AndWorksFromTheSearchBox()
    {
        Assert.Equal(new KeyGesture(Key.B, KeyModifiers.Control), ShortcutDefaults.For(ShortcutAction.ToggleSidebar, isMac: false));
        Assert.Equal(new KeyGesture(Key.B, KeyModifiers.Meta), ShortcutDefaults.For(ShortcutAction.ToggleSidebar, isMac: true));
        Assert.False(ShortcutDefaults.IsTextBoxKey(Key.B, KeyModifiers.Control));
        Assert.Contains(ShortcutDefaults.All, d => d.Action == ShortcutAction.ToggleSidebar && d.Group == ShortcutDefaults.GroupWindow);
    }
}
