using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Disc scratching (Discord, Mistery "Scratching"; Settings → Player → Disc Scratching, off by
/// default): the CD / record is grabbed and turned to move through the song, one record turn
/// (33⅓ rpm) = 1.8 s. The engine is held silent while a playing track is scratched and gets ONE
/// seek on release; a paused track stays paused; the turn is clamped to the track; a plain click
/// is not a scratch; with the setting off the disc does nothing at all.
/// </summary>
public class DiscScratchTests
{
    // ── Mapping ──

    [Fact]
    public void OneTurn_IsOneRevolutionOfA33Record()
    {
        Assert.Equal(1.8, DiscScratch.OffsetFor(360).TotalSeconds, 9);
        Assert.Equal(-0.9, DiscScratch.OffsetFor(-180).TotalSeconds, 9);
        Assert.Equal(0.45, DiscScratch.OffsetFor(90).TotalSeconds, 9);
        Assert.Equal(720, DiscScratch.DegreesFor(TimeSpan.FromSeconds(3.6)), 6);
    }

    [Fact]
    public void PointerAngle_GrowsClockwise_LikeTheDiscSpin()
    {
        var c = new Point(100, 100);
        Assert.Equal(0, DiscScratch.AngleOf(new Point(150, 100), c), 9);   // right
        Assert.Equal(90, DiscScratch.AngleOf(new Point(100, 150), c), 9);  // below = a quarter turn clockwise
        Assert.Equal(-90, DiscScratch.AngleOf(new Point(100, 50), c), 9);  // above
    }

    [Fact]
    public void TurnBetween_TakesTheShortWayAcrossTheWrap()
    {
        Assert.Equal(20, DiscScratch.TurnBetween(170, -170), 9);
        Assert.Equal(-20, DiscScratch.TurnBetween(-170, 170), 9);
        Assert.Equal(30, DiscScratch.TurnBetween(0, 30), 9);
        Assert.Equal(-30, DiscScratch.TurnBetween(30, 0), 9);
        Assert.Equal(180, DiscScratch.TurnBetween(0, 180), 9);
    }

    [Fact]
    public void Target_IsClampedToTheTrack_AndNeverLandsOnTheEnd()
    {
        var duration = TimeSpan.FromMinutes(3);
        Assert.Equal(TimeSpan.Zero, DiscScratch.TargetFor(TimeSpan.FromSeconds(1), -3600, duration));
        Assert.Equal(duration - DiscScratch.EndGuard, DiscScratch.TargetFor(TimeSpan.FromSeconds(179), 3600, duration));
        Assert.Equal(61.8, DiscScratch.TargetFor(TimeSpan.FromSeconds(60), 360, duration).TotalSeconds, 6);
        // A track shorter than the guard pins at 0 rather than going negative.
        Assert.Equal(TimeSpan.Zero, DiscScratch.Clamp(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void DragThreshold_KeepsASmallWobbleAClick()
    {
        var press = new Point(50, 50);
        Assert.False(DiscScratch.PastThreshold(press, new Point(53, 52)));
        Assert.True(DiscScratch.PastThreshold(press, new Point(54, 50)));
        Assert.True(DiscScratch.PastThreshold(press, new Point(50, 46)));
    }

    // ── SpinClock hold ──

    [Fact]
    public void HeldClock_StandsStill_ThenSpinsUpFromRestOnRelease()
    {
        var clock = new SpinClock { IsRunning = true };
        for (var i = 0; i < 120; i++) clock.Advance(1.0 / 60);
        Assert.True(clock.Velocity > 0);

        clock.Hold(1234);
        for (var i = 0; i < 60; i++) clock.Advance(1.0 / 60);
        Assert.Equal(1234, clock.TotalDegrees);
        Assert.Equal(0, clock.Velocity);

        clock.Release();
        clock.Advance(1.0 / 60);
        Assert.True(clock.TotalDegrees > 1234);
        Assert.True(clock.Velocity < SpinClock.DefaultDegreesPerSecond / 2, "spins up from rest, not at full speed");
    }

    // ── Player ──

    private static (PlayerViewModel vm, FakeAudioPlayer audio, Track[] tracks) Playing()
    {
        var tracks = new[]
        {
            new Track { Id = Guid.NewGuid(), Title = "a", Artist = "A", FilePath = Path.Combine("C:", "Music", "a.mp3"), Duration = TimeSpan.FromMinutes(3) },
            new Track { Id = Guid.NewGuid(), Title = "b", Artist = "A", FilePath = Path.Combine("C:", "Music", "b.mp3"), Duration = TimeSpan.FromMinutes(3) },
        };
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(tracks, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlaybackState.Playing, vm.State);
        vm.SeekTo(TimeSpan.FromSeconds(60));
        audio.Seeks.Clear();
        return (vm, audio, tracks);
    }

    [AvaloniaFact]
    public void PlayingScratch_HoldsTheEngineSilent_MovesThePosition_ThenOneSeekAndResume()
    {
        var (vm, audio, _) = Playing();

        Assert.True(vm.BeginScratch());
        Assert.True(vm.IsScratching);
        Assert.True(vm.IsSeeking); // engine position ticks are ignored, lyrics follow Position verbatim
        Assert.Equal(PlaybackState.Paused, audio.State); // held record = silent, cannot reach the end
        Assert.Equal(PlaybackState.Playing, vm.State);   // the UI keeps "playing": no button flicker

        Assert.Equal(360, vm.ScratchTo(360), 6);
        Assert.Equal(61.8, vm.Position.TotalSeconds, 3);
        vm.ScratchTo(-180);
        Assert.Equal(59.1, vm.Position.TotalSeconds, 3);
        Assert.Empty(audio.Seeks); // nothing hammers the engine mid-drag

        vm.EndScratch();
        Assert.False(vm.IsScratching);
        Assert.False(vm.IsSeeking);
        var seek = Assert.Single(audio.Seeks);
        Assert.Equal(59.1, seek.TotalSeconds, 3);
        Assert.Equal(PlaybackState.Playing, audio.State);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public void PausedScratch_MovesThePosition_AndStaysPaused()
    {
        var (vm, audio, _) = Playing();
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(PlaybackState.Paused, vm.State);

        Assert.True(vm.BeginScratch());
        vm.ScratchTo(720);
        vm.EndScratch();

        Assert.Equal(63.6, Assert.Single(audio.Seeks).TotalSeconds, 3);
        Assert.Equal(PlaybackState.Paused, vm.State);
        Assert.Equal(PlaybackState.Paused, audio.State);
    }

    [AvaloniaFact]
    public void Scratch_IsClampedToTheTrack_AndReportsTheTurnItApplied()
    {
        var (vm, audio, tracks) = Playing();
        vm.BeginScratch();

        // 60 s back is 12000°; anything past it is absorbed at the start of the song.
        Assert.Equal(-12000, vm.ScratchTo(-50000), 3);
        Assert.Equal(TimeSpan.Zero, vm.Position);

        var applied = vm.ScratchTo(1e7);
        Assert.Equal(vm.Duration - DiscScratch.EndGuard, vm.Position);
        Assert.Equal(DiscScratch.DegreesFor(vm.Duration - DiscScratch.EndGuard - TimeSpan.FromSeconds(60)), applied, 3);

        vm.EndScratch();
        Assert.Equal(tracks[0], vm.CurrentTrack); // never skipped to the next song
        Assert.True(Assert.Single(audio.Seeks) < vm.Duration);
    }

    [AvaloniaFact]
    public void ScratchWithoutAMove_SeeksNothing_AndResumes()
    {
        var (vm, audio, _) = Playing();
        vm.BeginScratch();
        vm.EndScratch();

        Assert.Empty(audio.Seeks);
        Assert.Equal(PlaybackState.Playing, audio.State);
        Assert.Equal(60, vm.Position.TotalSeconds, 3);
    }

    [AvaloniaFact]
    public void SongChangeMidScratch_DropsTheScratch_WithoutItsSeek()
    {
        var (vm, audio, tracks) = Playing();
        vm.BeginScratch();
        vm.ScratchTo(360);

        vm.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(tracks[1], vm.CurrentTrack);
        Assert.False(vm.IsScratching);
        Assert.False(vm.IsSeeking);

        vm.EndScratch(); // the disc's release arrives afterwards: a no-op
        Assert.Empty(audio.Seeks);
    }

    [AvaloniaFact]
    public void NoTrack_CannotBeScratched()
    {
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        Assert.False(vm.BeginScratch());
        Assert.False(vm.IsSeeking);
        Assert.Equal(0, vm.ScratchTo(360));
    }

    // ── The disc ──

    private sealed class FakeTarget : IDiscScratchTarget
    {
        public int Begins, Ends;
        public List<double> Turns { get; } = new();
        public bool BeginScratch() { Begins++; return true; }
        public double ScratchTo(double degrees) { Turns.Add(degrees); return degrees; }
        public void EndScratch() => Ends++;
    }

    private static (Window win, MediaArtwork art) Mount(bool enabled, ArtworkMedium medium, FakeTarget target)
    {
        var art = new MediaArtwork { Width = 400, Height = 400, Medium = medium, ScratchEnabled = enabled, ScratchTarget = target };
        var win = new Window { Width = 400, Height = 400, Content = art };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (win, art);
    }

    // The CD fills the 400 px control: centre (200,200), radius 200, hole radius ~27 px.
    private static readonly Point OnDiscRight = new(320, 200);

    private static void QuarterTurnClockwise(Window win)
    {
        win.MouseDown(OnDiscRight, MouseButton.Left);
        foreach (var deg in new[] { 10.0, 30, 50, 70, 90 })
        {
            var r = deg * Math.PI / 180;
            win.MouseMove(new Point(200 + 120 * Math.Cos(r), 200 + 120 * Math.Sin(r)), RawInputModifiers.LeftMouseButton);
        }
        win.MouseUp(new Point(200, 320), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DraggingTheDisc_ScratchesForward_AndTurnsTheDisc()
    {
        var target = new FakeTarget();
        var (win, art) = Mount(true, ArtworkMedium.CompactDisc, target);
        var before = art.DiscTotalDegrees;

        QuarterTurnClockwise(win);

        Assert.Equal(1, target.Begins);
        Assert.Equal(1, target.Ends);
        Assert.Equal(90, target.Turns[^1], 1);
        Assert.Equal(before + 90, art.DiscTotalDegrees, 1);
        Assert.False(art.IsScratching);
        Assert.False(art.IsScratchArmed);
        win.Close();
    }

    [AvaloniaFact]
    public void PlainClickOnTheDisc_IsNotAScratch()
    {
        var target = new FakeTarget();
        var (win, art) = Mount(true, ArtworkMedium.CompactDisc, target);

        win.MouseDown(OnDiscRight, MouseButton.Left);
        win.MouseMove(OnDiscRight + new Vector(2, 1), RawInputModifiers.LeftMouseButton); // under the threshold
        win.MouseUp(OnDiscRight + new Vector(2, 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, target.Begins);
        Assert.Equal(0, target.Ends);
        Assert.Empty(target.Turns);
        Assert.False(art.IsScratchArmed);
        win.Close();
    }

    [AvaloniaFact]
    public void SettingOff_TheDiscDoesNothing()
    {
        var target = new FakeTarget();
        var (win, art) = Mount(false, ArtworkMedium.CompactDisc, target);
        var handled = false;
        art.AddHandler(InputElement.PointerPressedEvent, (_, e) => handled = e.Handled,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        QuarterTurnClockwise(win);

        Assert.Equal(0, target.Begins);
        Assert.Empty(target.Turns);
        Assert.False(handled); // the press goes on to whoever handled it before
        Assert.False(art.IsScratchArmed);
        Assert.Null(art.FindControl<Control>("DiscSpin")!.Cursor);
        win.Close();
    }

    [AvaloniaFact]
    public void PlainCover_HasNoDiscToScratch()
    {
        var target = new FakeTarget();
        var (win, art) = Mount(true, ArtworkMedium.Cover, target);

        QuarterTurnClockwise(win);

        Assert.Null(art.ScratchDisc);
        Assert.Equal(0, target.Begins);
        win.Close();
    }

    [AvaloniaFact]
    public void TurningTheSettingOffMidScratch_EndsIt()
    {
        var target = new FakeTarget();
        var (win, art) = Mount(true, ArtworkMedium.CompactDisc, target);

        win.MouseDown(OnDiscRight, MouseButton.Left);
        win.MouseMove(new Point(310, 240), RawInputModifiers.LeftMouseButton);
        Assert.True(art.IsScratching);

        art.ScratchEnabled = false;
        Assert.False(art.IsScratching);
        Assert.Equal(1, target.Ends);

        win.MouseUp(new Point(310, 240), MouseButton.Left);
        Assert.Equal(1, target.Ends); // no second end on the release
        win.Close();
    }
}
