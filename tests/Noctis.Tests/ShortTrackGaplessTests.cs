using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A track shorter than the 2 s start guard (an interlude, a skit) got neither a staged
/// successor nor an early advance: the next file opened cold at its end (harness 11-19:
/// 410-450 ms of silence after a 0.5-0.9 s track, 110-140 ms after a 1.5-2 s one).
/// </summary>
public class ShortTrackGaplessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-short-" + Guid.NewGuid().ToString("N"));

    public ShortTrackGaplessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    private Track NewTrack(int number, double seconds)
    {
        var path = Path.Combine(_dir, $"{number:00}.wav");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = "Part " + number,
            Artist = "A",
            Album = "Interludes",
            AlbumId = Guid.Empty,
            TrackNumber = number,
            FilePath = path,
            Duration = TimeSpan.FromSeconds(seconds),
        };
    }

    // Starts the queue and leaves the start guard armed, as on a real track change.
    private static (FakeAudioPlayer Player, PlayerViewModel Vm) Start(params Track[] queue)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.AutoMixTransitionMode = AutoMixTransitionMode.Off;
        vm.ReplaceQueueAndPlay(queue, 0);
        Dispatcher.UIThread.RunJobs();
        return (player, vm);
    }

    [AvaloniaFact]
    public void HalfSecondTrack_StagesItsSuccessorThenHandsOff_InsideTheStartGuard()
    {
        var a = NewTrack(1, 0.5);
        var b = NewTrack(2, 10);
        var (player, vm) = Start(a, b);
        var duration = TimeSpan.FromSeconds(0.5);

        // Already inside the handoff lead on its first tick: stage now, hand off on a later one.
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, duration));
        Assert.Contains(b.FilePath, player.PreparedPaths);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);

        Assert.True(vm.TryAdvanceForGapless(TimeSpan.Zero, duration));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void InsideTheStartGuard_TheAdvanceWaitsForTheStagedSuccessor()
    {
        var a = NewTrack(1, 0.5);
        var b = NewTrack(2, 10);
        var (player, vm) = Start(a, b);
        player.StagesImmediately = false;
        var duration = TimeSpan.FromSeconds(0.5);

        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, duration));
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, duration));

        Assert.Equal(a.Id, vm.CurrentTrack?.Id); // a cold open would cut a before it is heard
    }

    [AvaloniaFact]
    public void OutgoingSongsLateTick_InsideTheStartGuard_DoesNothing()
    {
        var a = NewTrack(1, 0.9);
        var b = NewTrack(2, 10);
        var (player, vm) = Start(a, b);

        // 0.85 s into a song that started a moment ago: the previous song's position.
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.FromSeconds(0.85), TimeSpan.FromSeconds(0.9)));

        Assert.Empty(player.PreparedPaths);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void RunOfVeryShortTracks_StageAndHandOffInsideTheSettleWindow()
    {
        // 0.3 s songs: the settle window after each start (300 ms) was the whole song, so the
        // chain fell behind until a seam opened cold (harness 11-19: 10-120 ms of silence).
        var a = NewTrack(1, 0.3);
        var b = NewTrack(2, 0.3);
        var c = NewTrack(3, 10);
        var (player, vm) = Start(a, b, c);
        var d = TimeSpan.FromSeconds(0.3);

        // The tick's position is the outgoing song's: not used.
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.FromSeconds(9.7), d, settling: true));
        Assert.Contains(b.FilePath, player.PreparedPaths);
        Assert.True(vm.TryAdvanceForGapless(TimeSpan.FromSeconds(9.7), d, settling: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);

        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, d, settling: true));
        Assert.Contains(c.FilePath, player.PreparedPaths);
    }

    [AvaloniaFact]
    public void SettleWindow_WaitsForTheStagedSuccessor_AndLeavesLongerSongsToTheirTicks()
    {
        var a = NewTrack(1, 0.9);
        var b = NewTrack(2, 10);
        var (player, vm) = Start(a, b);
        player.StagesImmediately = false;

        // 0.9 s: staged at once, but the handoff needs its own position.
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(0.9), settling: true));
        Assert.Contains(b.FilePath, player.PreparedPaths);
        player.StagesImmediately = true;
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(0.9), settling: true));
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);

        var (player2, vm2) = Start(NewTrack(3, 9), NewTrack(4, 10));
        Assert.False(vm2.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(9), settling: true));
        Assert.Empty(player2.PreparedPaths);
    }

    [AvaloniaFact]
    public void SettleWindow_RepeatOneAndLastTrack_DoNothing()
    {
        var (player, vm) = Start(NewTrack(1, 0.3), NewTrack(2, 10));
        vm.RepeatMode = RepeatMode.One;
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(0.3), settling: true));
        Assert.Empty(player.PreparedPaths);

        var (player2, vm2) = Start(NewTrack(3, 0.3));
        Assert.False(vm2.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(0.3), settling: true));
        Assert.Empty(player2.PreparedPaths);
    }

    [AvaloniaFact]
    public void SettleWindow_WithSongTransitionsOn_DoesNothing()
    {
        var a = NewTrack(1, 0.3);
        var b = NewTrack(2, 10);
        var (player, vm) = Start(a, b);
        vm.AutoMixTransitionMode = AutoMixTransitionMode.Crossfade;

        Assert.False(vm.TryAdvanceForGapless(TimeSpan.Zero, TimeSpan.FromSeconds(0.3), settling: true));
        Assert.Empty(player.PreparedPaths);
    }

    [Theory]
    [InlineData(0, 0, 0, 1.0, true)]
    [InlineData(400, 0, 300, 1.0, true)]      // 300 ms in, 150 ms slack
    [InlineData(500, 0, 300, 1.0, false)]
    [InlineData(850, 0, 10, 1.0, false)]      // the outgoing song's tick
    [InlineData(30_100, 30_000, 50, 1.0, true)] // started at a saved position
    [InlineData(700, 0, 300, 2.0, true)]      // 2x playback
    [InlineData(700, 0, 300, 1.0, false)]
    [InlineData(0, 0, -5, 1.0, false)]
    public void OwnTickAtTrackStart(double posMs, double startMs, double msSince, double rate, bool expected) =>
        Assert.Equal(expected, PlayerViewModel.IsOwnTickAtTrackStart(
            TimeSpan.FromMilliseconds(posMs), TimeSpan.FromMilliseconds(startMs), msSince, rate));
}
