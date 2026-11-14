using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Tester 11-09: a song dragged by its handle near the Queue's top or bottom edge should scroll the
/// list by itself, and scrolling with the other hand froze the dragged song. The list scrolls faster
/// the nearer the finger is to the edge and stops at its ends; the row stays under the finger and
/// drops where it is, by the scroll since the press as well.
/// </summary>
public class MobileQueueAutoScrollTests
{
    private const double Row = 68;   // an Up Next row's height (QueuePage.axaml)

    private static (MobileFixtures.Rig Rig, Window Window, QueuePage Page) OpenQueue(bool history, int count)
    {
        var songs = Enumerable.Range(0, count).Select(i => MobileFixtures.Song($"A{i:D3}")).ToArray();
        var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.ShowQueueHistory = history;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        for (var i = 0; i < 2; i++) rig.Shell.Player.NextCommand.Execute(null);   // two played songs
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        page.CompleteSlide();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (rig, window, page);
    }

    private static ScrollViewer Scroll(QueuePage page) => MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
    private static ItemsControl List(QueuePage page) => MobileFixtures.Named<ItemsControl>(page, "QueueList");

    private static Point HandleCentre(Control container, Window window)
    {
        var handle = container.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
        return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
    }

    private static double Top(Control c, Window window) => c.TranslatePoint(default, window)!.Value.Y;

    private static double ListEdge(QueuePage page, Window window, bool bottom) =>
        Scroll(page).TranslatePoint(new Point(0, bottom ? Scroll(page).Bounds.Height : 0), window)!.Value.Y;

    [Fact]
    public void AutoScrollSpeed_GrowsTowardTheEdge_AndTheOffsetStopsAtTheEnds()
    {
        const double h = 600, max = QueueGesture.AutoScrollMaxDipPerSecond, edge = QueueGesture.AutoScrollEdgeDip;
        Assert.Equal(0, QueueGesture.AutoScrollVelocity(300, h));             // the middle never scrolls
        Assert.Equal(0, QueueGesture.AutoScrollVelocity(edge, h));            // nor the band's inner line
        Assert.Equal(0, QueueGesture.AutoScrollVelocity(h - edge, h));
        Assert.Equal(-max / 4, QueueGesture.AutoScrollVelocity(edge / 2, h), 6);
        Assert.Equal(max / 4, QueueGesture.AutoScrollVelocity(h - edge / 2, h), 6);
        Assert.Equal(-max, QueueGesture.AutoScrollVelocity(0, h));
        Assert.Equal(max, QueueGesture.AutoScrollVelocity(h, h));
        Assert.Equal(-max, QueueGesture.AutoScrollVelocity(-200, h));         // dragged past the edge: full speed
        Assert.Equal(max, QueueGesture.AutoScrollVelocity(h + 200, h));
        // Nearer the edge, faster.
        Assert.True(-QueueGesture.AutoScrollVelocity(10, h) > -QueueGesture.AutoScrollVelocity(40, h));
        Assert.True(QueueGesture.AutoScrollVelocity(h - 10, h) > QueueGesture.AutoScrollVelocity(h - 40, h));
        // A short list's bands are a third of it each, so its middle stays still.
        Assert.Equal(0, QueueGesture.AutoScrollVelocity(45, 90));
        Assert.Equal(-max, QueueGesture.AutoScrollVelocity(0, 90));
        Assert.Equal(0, QueueGesture.AutoScrollVelocity(0, 0));

        Assert.Equal(160, QueueGesture.AutoScrollOffset(100, 1200, 0.05, 1000), 6);
        Assert.Equal(1000, QueueGesture.AutoScrollOffset(990, 1200, 0.05, 1000));   // stops at the end
        Assert.Equal(0, QueueGesture.AutoScrollOffset(10, -1200, 0.05, 1000));      // and at the top
        Assert.Equal(0, QueueGesture.AutoScrollOffset(0, 1200, 0.05, -50));         // nothing to scroll
    }

    /// <summary>Drags Up Next's first row to the list's bottom edge, lets the auto-scroll run for
    /// <paramref name="frames"/> frames, then lets go. Returns the finger's travel, the scroll, and
    /// the dragged song.</summary>
    private static (double Travel, double Scrolled, Track Song) DragToBottomAndScroll(MobileFixtures.Rig rig, Window window, QueuePage page, int frames)
    {
        var scroll = Scroll(page);
        var list = List(page);
        var container = list.ContainerFromIndex(0)!;
        var song = rig.Shell.Player.UpNext[0];
        var start = HandleCentre(container, window);
        var finger = new Point(start.X, ListEdge(page, window, bottom: true) - 8);
        var before = scroll.Offset.Y;

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(finger, RawInputModifiers.LeftMouseButton);
        var rowAtFinger = Top(container, window);
        for (var i = 0; i < frames; i++)
        {
            page.StepAutoScroll(0.05);
            window.UpdateLayout();
        }
        // Its place scrolled far off the top, the dragged row is still the same row, showing its song,
        // under the finger.
        Assert.Same(container, list.ContainerFromIndex(0));
        Assert.Same(song, container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("queue-row")).DataContext);
        Assert.InRange(Top(container, window), rowAtFinger - 2, rowAtFinger + 2);   // within layout rounding

        window.MouseUp(finger, MouseButton.Left, RawInputModifiers.None);
        // Read after the release: a frame the input pump ran just before it still scrolled the list.
        var scrolled = scroll.Offset.Y - before;
        window.UpdateLayout();
        Assert.Null(container.RenderTransform);
        return (finger.Y - start.Y, scrolled, song);
    }

    [AvaloniaFact]
    public void DraggedToTheBottomEdge_TheListScrolls_AndTheRowDropsUnderTheFinger()
    {
        var (rig, window, page) = OpenQueue(history: false, count: 80);
        using var _rig = rig;
        var count = rig.Shell.Player.UpNext.Count;

        var (travel, scrolled, song) = DragToBottomAndScroll(rig, window, page, frames: 30);

        Assert.True(scrolled > Row * 15, $"scrolled {scrolled}");
        var expected = QueueGesture.TargetIndex(0, travel + scrolled, Row, count);
        Assert.InRange(expected, 20, count - 2);       // well past where the finger alone could reach
        Assert.Equal(expected, rig.Shell.Player.UpNext.IndexOf(song));
        window.Close();
    }

    [AvaloniaFact]
    public void PlayedSongsOn_AfterAnAutoScroll_TheRowDropsUnderTheFinger()
    {
        var (rig, window, page) = OpenQueue(history: true, count: 80);
        using var _rig = rig;
        var count = rig.Shell.Player.UpNext.Count;

        var (travel, scrolled, song) = DragToBottomAndScroll(rig, window, page, frames: 30);

        // In the one list the row goes past every row whose centre its own passed: Up Next row j's
        // centre is j rows below the dragged one's start.
        var d = travel + scrolled;
        var expected = Enumerable.Range(1, count - 1).Count(j => j * Row < d);
        Assert.InRange(expected, 20, count - 2);
        Assert.Equal(expected, rig.Shell.Player.UpNext.IndexOf(song));
        window.Close();
    }

    [AvaloniaFact]
    public void AtTheListsEnd_TheAutoScrollStops()
    {
        var (rig, window, page) = OpenQueue(history: false, count: 30);
        using var _rig = rig;
        var scroll = Scroll(page);
        var container = List(page).ContainerFromIndex(0)!;
        var start = HandleCentre(container, window);
        var finger = new Point(start.X, ListEdge(page, window, bottom: true) - 4);

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(finger, RawInputModifiers.LeftMouseButton);
        var frames = 0;
        while (page.StepAutoScroll(0.05) && frames < 500)
        {
            window.UpdateLayout();
            frames++;
        }
        Assert.InRange(frames, 2, 499);
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
        Assert.False(page.StepAutoScroll(0.05));
        window.MouseUp(finger, MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggedToTheTopEdge_TheListScrollsUp_ToItsTop_AndTheMiddleNeverScrolls()
    {
        var (rig, window, page) = OpenQueue(history: false, count: 60);
        using var _rig = rig;
        var scroll = Scroll(page);
        var list = List(page);
        scroll.Offset = new Vector(0, Row * 20);
        window.UpdateLayout();
        var container = list.GetRealizedContainers().OrderBy(list.IndexFromContainer)
            .First(c => Top(c, window) > ListEdge(page, window, bottom: false) + Row * 3);
        var start = HandleCentre(container, window);

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        // Held in the middle of the list: nothing scrolls.
        window.MouseMove(new Point(start.X, start.Y + 10), RawInputModifiers.LeftMouseButton);
        Assert.False(page.StepAutoScroll(0.05));
        Assert.Equal(Row * 20, scroll.Offset.Y);

        window.MouseMove(new Point(start.X, ListEdge(page, window, bottom: false) + 4), RawInputModifiers.LeftMouseButton);
        Assert.True(page.StepAutoScroll(0.05));
        Assert.True(scroll.Offset.Y < Row * 20);
        for (var i = 0; i < 200 && page.StepAutoScroll(0.05); i++) window.UpdateLayout();
        Assert.Equal(0, scroll.Offset.Y);
        window.MouseUp(new Point(start.X, ListEdge(page, window, bottom: false) + 4), MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void TheListScrolledByAnotherFinger_TheRowStaysUnderItsFinger_AndDropsByTheScroll()
    {
        var (rig, window, page) = OpenQueue(history: false, count: 40);
        using var _rig = rig;
        var scroll = Scroll(page);
        var list = List(page);
        var container = list.ContainerFromIndex(0)!;
        var song = rig.Shell.Player.UpNext[0];
        var start = HandleCentre(container, window);
        var finger = new Point(start.X, start.Y + 30);

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(finger, RawInputModifiers.LeftMouseButton);
        var rowAtFinger = Top(container, window);
        scroll.Offset = new Vector(0, Row * 5);     // the other hand scrolls; the drag finger stays put
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(Top(container, window), rowAtFinger - 2, rowAtFinger + 2);   // within layout rounding

        window.MouseUp(finger, MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(QueueGesture.TargetIndex(0, 30 + Row * 5, Row, rig.Shell.Player.UpNext.Count), rig.Shell.Player.UpNext.IndexOf(song));
        Assert.Equal(5, rig.Shell.Player.UpNext.IndexOf(song));
        window.Close();
    }
}
