using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A seek that lands directly inside the AutoMix fade window used to skip the
/// approach-only preload branch, so the prepared-transition validator failed with
/// "no prepared track" on every tick and the track ended with no transition —
/// exactly the "drag near the end to hear AutoMix" test every user tries first.
/// </summary>
public class AutoMixSeekIntoWindowTests : IDisposable
{
    private readonly string _dir;

    public AutoMixSeekIntoWindowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"noctis-automix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // The commit path requires the next track's file to exist on disk.
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

    [AvaloniaFact]
    public void SeekIntoFadeWindow_PreparesThenCommitsTransition()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        var b = Trk("b");
        vm.AutoMixTransitionMode = AutoMixTransitionMode.AutoMix;
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        Dispatcher.UIThread.RunJobs();
        ClearCommitGuard(vm);

        // 3-minute tracks without BPM plan a 5s fallback crossfade, so the fade
        // window opens at 175s. Land inside it, as an end-of-track seek does.
        var position = TimeSpan.FromSeconds(176.5);
        var duration = TimeSpan.FromMinutes(3);

        var firstTick = vm.TryAdvanceForAutoMix(position, duration);
        Assert.False(firstTick); // the fresh async prepare gets a tick of head start
        Assert.Contains(b.FilePath, player.PreparedPaths);

        var secondTick = vm.TryAdvanceForAutoMix(position, duration);
        Dispatcher.UIThread.RunJobs();

        Assert.True(secondTick);
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
    }

    // PlayTrack arms a 2s wall-clock commit guard against stale positions from the
    // outgoing song; clear it so the test doesn't have to sleep through it.
    private static void ClearCommitGuard(PlayerViewModel vm) =>
        typeof(PlayerViewModel)
            .GetField("_autoMixCommitGuardUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.MinValue);
}
