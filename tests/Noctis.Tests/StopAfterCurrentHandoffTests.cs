using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Stop after current track" with songs queued and gapless (the default) or AutoMix
/// on: the early handoff near the end ran AdvanceQueue, whose stop branch only set
/// State=Stopped while the track kept playing — its real TrackEnded then advanced the
/// queue and the next song started anyway. These drive the position-tick handoff and
/// then the natural end, and require the player to stay on the finished track.
/// </summary>
public class StopAfterCurrentHandoffTests : IDisposable
{
    private readonly string _dir;

    public StopAfterCurrentHandoffTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"noctis-stopafter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // The handoff paths require the next track's file to exist on disk.
    private Track Trk(string name)
    {
        var path = Path.Combine(_dir, $"{name}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new()
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "A",
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3)
        };
    }

    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    [AvaloniaFact]
    public void Gapless_StopAfterCurrent_DoesNotPlayQueuedNext()
    {
        var (vm, player) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        Assert.True(vm.GaplessEnabled); // the default this bug hides behind
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        Dispatcher.UIThread.RunJobs();
        ClearTrackStartGuards(vm);
        vm.StopAfterCurrentTrack = true;

        Tick(vm, player, TimeSpan.FromSeconds(175));   // gapless prepare window
        Tick(vm, player, TimeSpan.FromSeconds(179.6)); // inside the 0.5s handoff lead

        Assert.Equal(PlaybackState.Playing, vm.State); // still playing its tail
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
        Assert.Empty(player.PreparedPaths);

        EndTrackNaturally(player);

        AssertStoppedOn(vm, player, a, b);
    }

    [AvaloniaFact]
    public void Gapless_StopArmedAfterPreRoll_CancelsPreparedNext()
    {
        var (vm, player) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        Dispatcher.UIThread.RunJobs();
        ClearTrackStartGuards(vm);

        Tick(vm, player, TimeSpan.FromSeconds(175));
        Assert.Contains(b.FilePath, player.PreparedPaths);
        var cancelledBefore = player.CancelledCount;

        // Armed inside the prepare window: the engine would splice into the staged
        // next track at the seam unless it is dropped now.
        vm.StopAfterCurrentTrack = true;
        Assert.True(player.CancelledCount > cancelledBefore);

        Tick(vm, player, TimeSpan.FromSeconds(179.6));
        Assert.Single(player.PreparedPaths); // not re-prepared while armed
        EndTrackNaturally(player);

        AssertStoppedOn(vm, player, a, b);
    }

    [AvaloniaFact]
    public void AutoMix_StopAfterCurrent_DoesNotCommitTransition()
    {
        var (vm, player) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        vm.AutoMixTransitionMode = AutoMixTransitionMode.AutoMix;
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        Dispatcher.UIThread.RunJobs();
        ClearTrackStartGuards(vm);
        vm.StopAfterCurrentTrack = true;

        // Same in-window position the AutoMix commit test uses (prepare, then commit).
        var position = TimeSpan.FromSeconds(176.5);
        var duration = TimeSpan.FromMinutes(3);
        Assert.False(vm.TryAdvanceForAutoMix(position, duration));
        Assert.False(vm.TryAdvanceForAutoMix(position, duration));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(player.PreparedPaths);

        EndTrackNaturally(player);

        AssertStoppedOn(vm, player, a, b);
    }

    private static void AssertStoppedOn(PlayerViewModel vm, FakeAudioPlayer player, Track current, Track next)
    {
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(current.Id, vm.CurrentTrack?.Id);
        Assert.Equal(new[] { next.Id }, vm.UpNext.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { current.FilePath }, player.PlayedPaths);
        Assert.False(vm.StopAfterCurrentTrack); // one-shot flag consumed
    }

    /// <summary>One position report through the real tick (coalesced onto the dispatcher).</summary>
    private static void Tick(PlayerViewModel vm, FakeAudioPlayer player, TimeSpan position)
    {
        player.RaisePositionChanged(position);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The natural end exactly as VLC's TrackEnded delivers it.</summary>
    private static void EndTrackNaturally(FakeAudioPlayer player)
    {
        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
    }

    // PlayTrack arms a stale-position guard (positions far past the start are dropped
    // for 9s) and a 2s AutoMix commit guard; clear both so the test doesn't sleep.
    private static void ClearTrackStartGuards(PlayerViewModel vm)
    {
        var t = typeof(PlayerViewModel);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        t.GetField("_lastSeekTime", flags)!.SetValue(vm, DateTime.MinValue);
        t.GetField("_autoMixCommitGuardUntilUtc", flags)!.SetValue(vm, DateTime.MinValue);
    }
}
