using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The 9s stale-position guard armed by every track start and seek allowed only
/// wall-clock time since the seek, so at 2× speed real ticks (target + 2t) were
/// dropped from ~4s to 9s: the timeline and lyrics froze, then jumped. The allowance
/// now scales with the playback rate; outgoing-track positions are still rejected.
/// </summary>
public class PlaybackRateStaleGuardTests : IDisposable
{
    private readonly string _dir;

    public PlaybackRateStaleGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"noctis-rateguard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private static (PlayerViewModel vm, FakeAudioPlayer player) StartAt(int ratePercent, Track track)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.PlaybackRatePercent = ratePercent;
        vm.ReplaceQueueAndPlay(new[] { track }, 0);
        Dispatcher.UIThread.RunJobs();
        return (vm, player);
    }

    // Pretend the track started this long ago (still inside the 9s guard window).
    private static void BackdateTrackStart(PlayerViewModel vm, TimeSpan ago) =>
        typeof(PlayerViewModel)
            .GetField("_lastSeekTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.UtcNow - ago);

    private static void Tick(FakeAudioPlayer player, TimeSpan position)
    {
        player.RaisePositionChanged(position);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DoubleSpeed_TickSixSecondsAfterStart_IsApplied()
    {
        var (vm, player) = StartAt(200, Trk("a"));
        BackdateTrackStart(vm, TimeSpan.FromSeconds(6));

        Tick(player, TimeSpan.FromSeconds(12)); // 6s of wall clock at 2×

        Assert.Equal(TimeSpan.FromSeconds(12), vm.Position);
    }

    [AvaloniaFact]
    public void DoubleSpeed_OutgoingTrackPosition_StillRejected()
    {
        var (vm, player) = StartAt(200, Trk("a"));
        BackdateTrackStart(vm, TimeSpan.FromSeconds(6));

        Tick(player, TimeSpan.FromSeconds(170)); // previous song's near-end tick

        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public void NormalSpeed_ImplausibleJump_StillRejected()
    {
        var (vm, player) = StartAt(100, Trk("a"));
        BackdateTrackStart(vm, TimeSpan.FromSeconds(6));

        Tick(player, TimeSpan.FromSeconds(12)); // 2× faster than the clock at 1×

        Assert.Equal(TimeSpan.Zero, vm.Position);
    }
}
