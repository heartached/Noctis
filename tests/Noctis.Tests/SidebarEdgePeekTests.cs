using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #171: with the lyrics page up in fullscreen the sidebar is hidden
/// (MainWindow.UpdateImmersiveLyricsState), and the mouse at the left edge never brought it
/// back. The real strip and rail from MainWindow.axaml, driven by the headless mouse.
/// </summary>
public class SidebarEdgePeekTests
{
    private sealed class Rig
    {
        public required Window Host;
        public required Border Edge;
        public required Border Rail;
        public required SidebarEdgePeek Peek;
        public bool Hidden = true;
        public bool HoverExpand = true;
        public bool Pinned;
        public bool Expanded;

        public void Move(double x, double y = 300)
        {
            Host.MouseMove(new Point(x, y));
            for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Build(bool hoverExpand = true, bool pinned = false)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        var edge = main.FindControl<Border>("SidebarRevealEdge");
        Assert.NotNull(edge); // the left-edge strip that brings the hidden rail back
        var rail = main.FindControl<Border>("SidebarWrapper")!;
        ((Panel)edge.Parent!).Children.Remove(edge);
        ((Panel)rail.Parent!).Children.Remove(rail);
        rail.Transitions = null;
        // What the shell does when the sidebar hides (MainWindow's IsSidebarHidden handler).
        rail.Width = 0;
        rail.IsVisible = false;

        var page = new Border { Background = Avalonia.Media.Brushes.Black }; // the lyrics page
        var host = new Window { Width = 1200, Height = 700, Content = new Panel { Children = { page, edge, rail } } };
        Rig? rig = null;
        var peek = new SidebarEdgePeek(edge, rail, () => rig!.Hidden, () => rig!.HoverExpand,
            () => rig!.Pinned, e => rig!.Expanded = e);
        rig = new Rig { Host = host, Edge = edge, Rail = rail, Peek = peek, HoverExpand = hoverExpand, Pinned = pinned };
        peek.SyncHidden();
        host.Show();
        rig.Move(600);
        return rig;
    }

    [AvaloniaFact]
    public void HiddenSidebar_SlidesIn_AtTheLeftEdge_AndAwayAgain()
    {
        var r = Build(hoverExpand: false);
        Assert.False(r.Rail.IsVisible);

        r.Move(0); // the very first column of pixels, as a fullscreen window gives it
        Assert.True(r.Peek.IsPeeking, "the mouse at the left edge must reveal the sidebar");
        Assert.True(r.Rail.IsVisible);
        Assert.Equal(SidebarEdgePeek.RailWidth, r.Rail.Width);

        r.Move(30); // onto the rail itself: it stays
        Assert.True(r.Rail.IsVisible);
        Assert.Equal(SidebarEdgePeek.RailWidth, r.Rail.Width); // hover expand off: icons only
        Assert.False(r.Expanded);

        r.Move(600); // back onto the lyrics: tucked away again
        Assert.False(r.Peek.IsPeeking);
        Assert.False(r.Rail.IsVisible);
        Assert.Equal(0, r.Rail.Width);
        r.Host.Close();
    }

    [AvaloniaFact]
    public void Peek_ExpandsOnHover_WhenHoverExpandIsOn()
    {
        var r = Build(hoverExpand: true);
        r.Move(2);
        r.Move(30);
        Assert.Equal(SidebarEdgePeek.ExpandedWidth, r.Rail.Width);
        Assert.True(r.Expanded);
        r.Move(900);
        Assert.False(r.Rail.IsVisible);
        Assert.False(r.Expanded);
        r.Host.Close();
    }

    [AvaloniaFact]
    public void Peek_IsPinnedWide_WhenKeepExpandedIsOn()
    {
        var r = Build(hoverExpand: false, pinned: true);
        r.Move(1);
        Assert.Equal(SidebarEdgePeek.ExpandedWidth, r.Rail.Width);
        r.Host.Close();
    }

    [AvaloniaFact]
    public void Edge_OnlyExists_WhileTheSidebarIsHidden()
    {
        var r = Build();
        Assert.True(r.Edge.IsVisible);
        r.Hidden = false;
        r.Peek.SyncHidden();
        Assert.False(r.Edge.IsVisible);
        // Not hidden: the edge does nothing (the normal hover handler owns the rail).
        r.Move(0);
        Assert.False(r.Peek.IsPeeking);
        r.Host.Close();
    }

    /// <summary>GitHub #233 (Hide sidebar until hover): keyboard focus inside the slid-in rail
    /// keeps it there with the pointer gone, and focus leaving tucks it away.</summary>
    [AvaloniaFact]
    public void KeyboardFocusInsideTheRail_KeepsItOpen()
    {
        var r = Build(hoverExpand: false);
        r.Move(0);
        r.Move(30);
        // A focusable stand-in for the rail's rows (unbound here, so the real rows are empty).
        var target = new Button { Content = "Row", Focusable = true };
        r.Rail.Child = target;
        Dispatcher.UIThread.RunJobs();
        Assert.True(target.Focus());
        Dispatcher.UIThread.RunJobs();

        r.Move(700); // pointer back on the page, focus still in the rail
        Assert.True(r.Peek.IsPeeking, "focus inside the sidebar must keep it open");
        Assert.True(r.Rail.IsVisible);

        // Focus moves onto the page (a control far from the rail).
        var onPage = new Button { Content = "Page", Focusable = true, Margin = new Thickness(800, 0, 0, 0) };
        ((Panel)r.Host.Content!).Children.Add(onPage);
        Dispatcher.UIThread.RunJobs();
        Assert.True(onPage.Focus());
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        Assert.False(r.Peek.IsPeeking);
        Assert.False(r.Rail.IsVisible);
        r.Host.Close();
    }

    /// <summary>With a CloseDelay the rail waits out the grace period before tucking away,
    /// and coming back within it keeps the rail.</summary>
    [AvaloniaFact]
    public async Task CloseDelay_KeepsTheRail_ForTheGracePeriod()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var r = Build(hoverExpand: false);
        // Rebuild the peek with a delay over the same strip and rail.
        var hidden = true;
        var peek = new SidebarEdgePeek(r.Edge, r.Rail, () => hidden, () => false, () => false, _ => { })
        {
            CloseDelay = TimeSpan.FromMilliseconds(150),
        };
        r.Hidden = false; // silence the first peek
        peek.SyncHidden();

        r.Move(0);
        Assert.True(peek.IsPeeking);
        r.Move(700);
        Assert.True(peek.IsPeeking, "still inside the grace period");
        r.Move(30); // back onto the rail in time
        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
        Assert.True(peek.IsPeeking);

        r.Move(700);
        await Task.Delay(250);
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        Assert.False(peek.IsPeeking);
        Assert.False(r.Rail.IsVisible);
        r.Host.Close();
    }
}
