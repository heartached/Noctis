using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Swipe sideways on the mini player or the Now Playing cover to change the song (beta tester request).</summary>
public class MobileTrackSwipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- The maths ---------------------------------------------------------------------------

    [Fact]
    public void Direction_LeftIsNext_RightIsPrevious()
    {
        Assert.Equal(SwipeOutcome.Next, TrackSwipe.Direction(-50));
        Assert.Equal(SwipeOutcome.Previous, TrackSwipe.Direction(50));
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Direction(0));
    }

    [Fact]
    public void Distance_CommitsPastAThirdOfTheWidth_ElseSnapsBack()
    {
        // 300 wide: the line is 105.
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-100, 0, 300, true, true));
        Assert.Equal(SwipeOutcome.Next, TrackSwipe.Decide(-106, 0, 300, true, true));
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(100, 0, 300, true, true));
        Assert.Equal(SwipeOutcome.Previous, TrackSwipe.Decide(106, 0, 300, true, true));
        // Far enough, but flicked back the other way at the end: settles back.
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-150, 0.8, 300, true, true));
    }

    [Fact]
    public void Velocity_AFlickCommitsAShortDrag_ButNotATap()
    {
        Assert.Equal(SwipeOutcome.Next, TrackSwipe.Decide(-40, -0.6, 300, true, true));
        Assert.Equal(SwipeOutcome.Previous, TrackSwipe.Decide(40, 0.6, 300, true, true));
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-40, -0.3, 300, true, true));   // too slow
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-15, -2.0, 300, true, true));   // under the fling floor
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-40, 0.6, 300, true, true));    // flick against the drag
    }

    [Fact]
    public void NoSongThatWay_RubberBands_AndNeverCommits()
    {
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(-250, -2, 300, canNext: false, canPrevious: true));
        Assert.Equal(SwipeOutcome.None, TrackSwipe.Decide(250, 2, 300, canNext: true, canPrevious: false));
        Assert.Equal(-100 * TrackSwipe.RubberBand, TrackSwipe.Offset(-100, canNext: false, canPrevious: true), 6);
        Assert.Equal(-100, TrackSwipe.Offset(-100, canNext: true, canPrevious: false), 6);
        Assert.Equal(100 * TrackSwipe.RubberBand, TrackSwipe.Offset(100, canNext: true, canPrevious: false), 6);
    }

    [Fact]
    public void AxisLock_DownwardDragStaysTheSheets_SidewaysIsTheSwipes()
    {
        Assert.Equal(SwipeAxis.Undecided, TrackSwipe.Lock(6, 8));
        Assert.Equal(SwipeAxis.Vertical, TrackSwipe.Lock(4, 20));    // swipe-down to close
        Assert.Equal(SwipeAxis.Vertical, TrackSwipe.Lock(3, -20));   // a scroll up
        Assert.Equal(SwipeAxis.Horizontal, TrackSwipe.Lock(-14, 6));
        // The same rule the shell's swipe-down uses (sideways past the slop first): never both.
        Assert.Equal(SwipeAxis.Horizontal, TrackSwipe.Lock(11, 11));
    }

    // ---- What a swipe would land on ----------------------------------------------------------

    private static Track[] Tracks(int n) => Enumerable.Range(0, n)
        .Select(i => new Track { Id = Guid.NewGuid(), Title = $"t{i}", FilePath = $"content://x/t{i}", Duration = TimeSpan.FromSeconds(100 + i) })
        .ToArray();

    private (NowPlayingViewModel Vm, FakeAudioPlayer Player) Make(Track[] tracks)
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        return (new NowPlayingViewModel(player, library, new PersistenceService(_root), marshal: a => a()), player);
    }

    [Fact]
    public void Peek_AtTheQueueEnd_NothingNext_UnlessRepeatAllWraps()
    {
        var t = Tracks(3);
        var (vm, _) = Make(t);
        vm.PlayTracks(t, 0);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Null(vm.PeekNext());                    // Repeat off: Next would stop
        Assert.Same(t[2], vm.CurrentTrack);            // and peeking moved nothing
        Assert.Same(t[1], vm.PeekPrevious());

        vm.CycleRepeatCommand.Execute(null);           // Off → All
        Assert.Same(t[0], vm.PeekNext());              // wraps round
        Assert.Same(t[2], vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    [Fact]
    public void Peek_FirstSong_NothingBefore_AndSkipToPreviousNeverRestarts()
    {
        var t = Tracks(3);
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        Assert.Same(t[1], vm.PeekNext());
        Assert.Null(vm.PeekPrevious());
        vm.SkipToPrevious();                           // a no-op, not a restart
        Assert.Single(player.PlayedPaths);

        vm.NextCommand.Execute(null);
        Assert.Same(t[0], vm.PeekPrevious());
        vm.SkipToPrevious();
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(new[] { t[1], t[2] }, vm.UpNext);
    }

    // ---- Through the real input pipeline -----------------------------------------------------

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static void DragX(Window window, Point from, double by)
    {
        Pump(window);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        var step = Math.Sign(by) * 10.0;
        for (var dx = step; Math.Abs(dx) <= Math.Abs(by); dx += step)
        {
            window.MouseMove(from + new Point(dx, 0), RawInputModifiers.LeftMouseButton);
            window.UpdateLayout();
            System.Threading.Thread.Sleep(40);   // 0.25 dp/ms: decided by distance, not a flick
        }
        window.MouseUp(from + new Point(by, 0), MouseButton.Left, RawInputModifiers.None);
        MobileFixtures.Land(window);
    }

    private static Point CentreOf(Control c, Window window) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public void MiniPlayer_SwipeLeftSkips_SwipeRightGoesBack_AShortOneDoesNothing_AndNoTapOpens()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        MobileFixtures.Land(window);
        var row = view.FindControl<Button>("MiniRow")!;
        var at = CentreOf(row, window);

        DragX(window, at, -40);                        // short and slow: springs back
        Assert.Equal("S0", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Null(row.RenderTransform);
        Assert.False(view.FindControl<Grid>("MiniGhost")!.IsVisible);

        DragX(window, at, -200);
        Assert.Equal("S1", rig.Shell.Player.CurrentTrack!.Title);
        Assert.False(rig.Shell.IsNowPlayingOpen);      // the row's tap did not fire
        Assert.Null(row.RenderTransform);

        DragX(window, at, 200);
        Assert.Equal("S0", rig.Shell.Player.CurrentTrack!.Title);

        DragX(window, at, 200);                        // nothing before the first: rubber band only
        Assert.Equal("S0", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Null(row.RenderTransform);
        window.Close();
    }

    [AvaloniaFact]
    public void MiniPlayer_AtTheQueueEnd_SwipeLeftDoesNothing()
    {
        var songs = Enumerable.Range(0, 2).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 1);
        MobileFixtures.Land(window);
        var at = CentreOf(view.FindControl<Button>("MiniRow")!, window);

        DragX(window, at, -250);
        Assert.Equal("S1", rig.Shell.Player.CurrentTrack!.Title);
        Assert.True(rig.Shell.Player.IsPlaying);
        window.Close();
    }

    [AvaloniaFact]
    public void NowPlayingCover_SwipeLeftSkips_AndASwipeDownStillCloses()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var cover = MobileFixtures.Find<NowPlayingPage>(view).FindControl<Viewbox>("ArtworkBox")!;
        var at = CentreOf(cover, window);

        DragX(window, at, -220);
        Assert.Equal("S1", rig.Shell.Player.CurrentTrack!.Title);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Assert.Null(cover.RenderTransform);

        // Straight down on the cover: the sheet's, not the swipe's.
        Pump(window);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        for (var dy = 10.0; dy <= 300; dy += 10)
        {
            window.MouseMove(at + new Point(0, dy), RawInputModifiers.LeftMouseButton);
            window.UpdateLayout();
        }
        window.MouseUp(at + new Point(0, 300), MouseButton.Left, RawInputModifiers.None);
        MobileFixtures.Land(window);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Assert.Equal("S1", rig.Shell.Player.CurrentTrack!.Title);
        window.Close();
    }
}
