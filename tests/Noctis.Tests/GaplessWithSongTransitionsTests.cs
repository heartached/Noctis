using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #254 ("Gapless playback not working"). With Song Transitions on, the planner leaves
/// some pairs alone on purpose: the next song of the same album, short tracks, classical and
/// live recordings. The gapless advance gave up whenever the transition mode was not Off, so
/// exactly those pairs got neither a transition nor a staged next track: the file opened cold
/// at the seam (measured on the engine's output: 478 ms of silence between two parts of one
/// continuous recording, 0 ms with Song Transitions off).
/// </summary>
public class GaplessWithSongTransitionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-gapless-" + Guid.NewGuid().ToString("N"));

    public GaplessWithSongTransitionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    private Track AlbumTrack(Guid albumId, int number, string genre = "")
    {
        var path = Path.Combine(_dir, $"{number:00}.flac");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = "Part " + number,
            Artist = "A",
            Album = "One Recording",
            AlbumId = albumId,
            DiscNumber = 1,
            TrackNumber = number,
            Genre = genre,
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3)
        };
    }

    private static (FakeAudioPlayer Player, PlayerViewModel Vm) Start(AutoMixTransitionMode mode, params Track[] queue)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.AutoMixTransitionMode = mode;
        vm.ReplaceQueueAndPlay(queue, 0);
        Dispatcher.UIThread.RunJobs();
        typeof(PlayerViewModel)
            .GetField("_autoMixCommitGuardUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.MinValue);
        return (player, vm);
    }

    // One position tick, in the order the position timer runs the two advances.
    private static bool Tick(PlayerViewModel vm, TimeSpan position, TimeSpan duration) =>
        vm.TryAdvanceForAutoMix(position, duration) || vm.TryAdvanceForGapless(position, duration);

    [AvaloniaTheory]
    [InlineData(AutoMixTransitionMode.AutoMix)]
    [InlineData(AutoMixTransitionMode.Crossfade)]
    public void NextSongOfTheSameAlbum_IsStagedAndHandedOffGapless_WithSongTransitionsOn(AutoMixTransitionMode mode)
    {
        var album = Guid.NewGuid();
        var a = AlbumTrack(album, 1);
        var b = AlbumTrack(album, 2);
        var (player, vm) = Start(mode, a, b);
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(Tick(vm, duration - TimeSpan.FromSeconds(6), duration));
        Assert.Contains(b.FilePath, player.PreparedPaths);

        var advanced = Tick(vm, duration - TimeSpan.FromSeconds(0.3), duration);
        Dispatcher.UIThread.RunJobs();

        Assert.True(advanced);
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void PairThePlannerExcludesByGenre_IsStagedGapless_WithSongTransitionsOn()
    {
        // Different albums, so only the genre rule keeps the planner off this pair.
        var a = AlbumTrack(Guid.NewGuid(), 1, genre: "Classical");
        var b = AlbumTrack(Guid.NewGuid(), 5, genre: "Classical");
        var (player, vm) = Start(AutoMixTransitionMode.AutoMix, a, b);
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(Tick(vm, duration - TimeSpan.FromSeconds(6), duration));

        Assert.Contains(b.FilePath, player.PreparedPaths);
    }

    [AvaloniaFact]
    public void PairThePlannerTransitions_IsNotTakenByTheGaplessAdvance()
    {
        // Two albums: the planner mixes this pair, and the gapless advance stays out of it.
        var a = AlbumTrack(Guid.NewGuid(), 1);
        var b = AlbumTrack(Guid.NewGuid(), 5);
        var (_, vm) = Start(AutoMixTransitionMode.AutoMix, a, b);
        var duration = TimeSpan.FromMinutes(3);

        vm.TryAdvanceForAutoMix(TimeSpan.FromSeconds(100), duration);

        Assert.False(vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(0.3), duration));
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void GaplessOff_StillLeavesTheSameAlbumPairAlone_WithSongTransitionsOn()
    {
        var album = Guid.NewGuid();
        var a = AlbumTrack(album, 1);
        var b = AlbumTrack(album, 2);
        var (player, vm) = Start(AutoMixTransitionMode.AutoMix, a, b);
        vm.GaplessEnabled = false;
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(Tick(vm, duration - TimeSpan.FromSeconds(6), duration));
        Assert.False(Tick(vm, duration - TimeSpan.FromSeconds(0.3), duration));

        Assert.DoesNotContain(b.FilePath, player.PreparedPaths);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }
}
