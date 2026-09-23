using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Queue page gestures, driven through the headless input pipeline.</summary>
public class MobileQueueGestureTests
{
    private static (MobileFixtures.Rig Rig, Window Window, ShellView View, Track[] Songs) OpenQueue(Track[]? queue = null)
    {
        var songs = queue ?? Enumerable.Range(0, 5).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        var rig = MobileFixtures.MakeRig(songs.Distinct().ToArray());
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        return (rig, window, view, songs);
    }

    private static Control Row(ShellView view, int index) =>
        MobileFixtures.Named<ItemsControl>(view, "QueueList").ContainerFromIndex(index)!;

    [Fact]
    public void QueueGesture_Thresholds()
    {
        Assert.False(QueueGesture.ShouldRemove(-90, 200));     // under the 96 dip floor
        Assert.True(QueueGesture.ShouldRemove(-100, 200));
        Assert.False(QueueGesture.ShouldRemove(-120, 400));    // under 35 % of a wide row
        Assert.True(QueueGesture.ShouldRemove(-141, 400));
        Assert.False(QueueGesture.ShouldRemove(300, 400));     // right swipes never remove

        Assert.Equal(2, QueueGesture.TargetIndex(0, 125, 60, 4));
        Assert.Equal(3, QueueGesture.TargetIndex(0, 900, 60, 4));    // clamped to the end
        Assert.Equal(0, QueueGesture.TargetIndex(2, -500, 60, 4));
        Assert.Equal(1, QueueGesture.TargetIndex(1, 20, 60, 4));     // under half a row stays put
    }

    [AvaloniaFact]
    public void DragHandle_MovesTheRow_ByWholeRows()
    {
        var (rig, window, view, _) = OpenQueue();
        using var _rig = rig;
        Assert.Equal(new[] { "S1", "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        var row = Row(view, 0);
        var handle = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        var rowHeight = row.Bounds.Height;

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(start.X, start.Y + rowHeight), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(start.X, start.Y + rowHeight * 2 + 5), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(start.X, start.Y + rowHeight * 2 + 5), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { "S2", "S3", "S1", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void SwipeLeft_PastTheThreshold_RemovesTheRow_AndAShortSwipeSnapsBack()
    {
        var (rig, window, view, _) = OpenQueue();
        using var _rig = rig;
        var row = Row(view, 1);
        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;

        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(230, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(200, y), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(4, rig.Shell.Player.UpNext.Count);             // 50 dip: snaps back
        Assert.Null(Row(view, 1).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("queue-row")).RenderTransform);

        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(30, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, y), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { "S1", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void DuplicateTrack_TheSwipedCopyIsTheOneRemoved()
    {
        var now = MobileFixtures.Song("Now");
        var x = MobileFixtures.Song("X");
        var y = MobileFixtures.Song("Y");
        var (rig, window, view, _) = OpenQueue(new[] { now, x, y, x });
        using var _rig = rig;

        MobileFixtures.Find<QueuePage>(view).CommitSwipe(2, x, -300, 380);   // the second X

        Assert.Equal(new[] { "X", "Y" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void ClearUpNext_EmptiesTheQueue_KeepsTheCurrentTrack_AndEachChangeIsOneReset()
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs, 0);
        var events = 0;
        rig.Shell.Player.UpNext.CollectionChanged += (_, e) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
            events++;
        };

        rig.Shell.Player.AddToQueue(songs[1]);
        Assert.Equal(1, events);

        rig.Shell.Player.ClearUpNextCommand.Execute(null);
        Assert.Empty(rig.Shell.Player.UpNext);
        Assert.False(rig.Shell.Player.HasUpNext);
        Assert.Same(songs[0], rig.Shell.Player.CurrentTrack);
    }
    [AvaloniaFact]
    public void UpNextRebuiltMidSwipe_TheReleaseDoesNotRemoveTheNeighbour()
    {
        var (rig, window, view, _) = OpenQueue();
        using var _rig = rig;
        var row = Row(view, 1);                                     // S2
        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;

        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(30, y), RawInputModifiers.LeftMouseButton);
        rig.Shell.Player.NextCommand.Execute(null);                 // lock-screen Next mid-gesture
        window.UpdateLayout();
        Assert.Equal(new[] { "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.MouseUp(new Point(30, y), MouseButton.Left, RawInputModifiers.None);

        // Index 1 is now S3: acting on the index captured at press would remove it.
        Assert.Equal(new[] { "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void UpNextRebuiltMidDrag_TheReleaseDoesNotMoveTheNeighbour()
    {
        var (rig, window, view, _) = OpenQueue();
        using var _rig = rig;
        var row = Row(view, 0);                                     // S1
        var handle = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        var rowHeight = row.Bounds.Height;

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(start.X, start.Y + rowHeight * 2 + 5), RawInputModifiers.LeftMouseButton);
        rig.Shell.Player.NextCommand.Execute(null);                 // the track ended mid-gesture
        window.UpdateLayout();
        window.MouseUp(new Point(start.X, start.Y + rowHeight * 2 + 5), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }
    [AvaloniaFact]
    public void UpNextRebuiltBetweenPressAndSwipe_TheReleaseDoesNotRemoveTheNeighbour()
    {
        var (rig, window, view, _) = OpenQueue();
        using var _rig = rig;
        var row = Row(view, 1);                                     // S2
        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;

        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        rig.Shell.Player.NextCommand.Execute(null);
        window.UpdateLayout();
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(30, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, y), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }
    [AvaloniaFact]
    public void Commit_AfterUpNextWasRebuilt_ActsOnlyIfThePressedTrackIsStillAtItsIndex()
    {
        var (rig, window, view, songs) = OpenQueue();
        using var _rig = rig;
        var page = MobileFixtures.Find<QueuePage>(view);
        rig.Shell.Player.NextCommand.Execute(null);                 // S1 now playing; Up Next S2, S3, S4

        page.CommitSwipe(1, songs[2], -300, 380);                   // pressed S2 at index 1: now S3 there
        page.CommitDrag(0, songs[1], 200, 60);                      // pressed S1 at index 0: now S2 there
        Assert.Equal(new[] { "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));

        page.CommitSwipe(1, songs[3], -300, 380);                   // S3 really is at index 1
        Assert.Equal(new[] { "S2", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }
}
