using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09: only Next marked a play skipped, so with "Count a play after: Immediately"
/// picking another song (double-click, Play in a menu) left the first one a full play. On the
/// owner's main profile 2,655 of 4,800 logged plays were cut off within 30 s. A song the user
/// leaves in its first half is now a skip however they left it; a song that ends by itself,
/// or one that was stopped, is not.
/// </summary>
public class SkipOnReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-skipreplace-{Guid.NewGuid():N}");

    public SkipOnReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class RecordingPlayHistory : IPlayHistoryService
    {
        public List<PlayHistoryEvent> Log { get; } = new();
        public IReadOnlyList<PlayHistoryEvent> Events => Log;
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) =>
            Log.Add(new PlayHistoryEvent { TrackId = track.Id, Title = track.Title, PlayedAtUtc = DateTime.UtcNow });
        public void RecordSkip(Track track)
        {
            var newest = Log.LastOrDefault(e => e.TrackId == track.Id);
            if (newest != null) newest.Skipped = true;
        }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private Track Trk(string name)
    {
        var path = Path.Combine(_dir, $"{name}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track { Id = Guid.NewGuid(), Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private static (PlayerViewModel Player, FakeAudioPlayer Audio, RecordingPlayHistory Log) Create()
    {
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var log = new RecordingPlayHistory();
        player.SetPlayHistory(log); // no settings: "Immediately", every start is logged
        return (player, audio, log);
    }

    private static void SettleSeekGuards(PlayerViewModel vm) =>
        typeof(PlayerViewModel)
            .GetField("_lastSeekTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.UtcNow - TimeSpan.FromMinutes(10));

    private static void PlayAt(PlayerViewModel player, FakeAudioPlayer audio, double seconds)
    {
        Dispatcher.UIThread.RunJobs();
        SettleSeekGuards(player);
        audio.RaisePositionChanged(TimeSpan.FromSeconds(seconds));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void PickingAnotherSong_InTheFirstHalf_MarksTheFirstSkipped()
    {
        var (player, audio, log) = Create();
        var a = Trk("a");
        var b = Trk("b");
        player.ReplaceQueueAndPlay(new[] { a }, 0);
        PlayAt(player, audio, 2); // 2 s of a 3-min song

        player.ReplaceQueueAndPlay(new[] { b }, 0); // a double-click on another song
        Dispatcher.UIThread.RunJobs();

        Assert.True(log.Log.Single(e => e.TrackId == a.Id).Skipped);
        Assert.False(log.Log.Single(e => e.TrackId == b.Id).Skipped);
    }

    [AvaloniaFact]
    public void PickingAnotherSong_PastHalfway_IsAFullPlay()
    {
        var (player, audio, log) = Create();
        var a = Trk("a");
        player.ReplaceQueueAndPlay(new[] { a }, 0);
        PlayAt(player, audio, 120); // 2 of 3 minutes

        player.ReplaceQueueAndPlay(new[] { Trk("b") }, 0);
        Dispatcher.UIThread.RunJobs();

        Assert.False(log.Log.Single(e => e.TrackId == a.Id).Skipped);
    }

    [AvaloniaFact]
    public void ASongThatEndsByItself_IsNotASkip_EvenWhenItsReportedPositionIsEarly()
    {
        var (player, audio, log) = Create();
        var a = Trk("a");
        var b = Trk("b");
        player.ReplaceQueueAndPlay(new[] { a, b }, 0);
        PlayAt(player, audio, 5); // the engine can report the end before the last ticks land

        audio.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(b.Id, player.CurrentTrack?.Id);
        Assert.False(log.Log.Single(e => e.TrackId == a.Id).Skipped);
    }

    [AvaloniaFact]
    public void PlayAfterAStop_ReplaysWithoutMarkingTheStoppedPlay()
    {
        var (player, audio, log) = Create();
        var a = Trk("a");
        player.ReplaceQueueAndPlay(new[] { a, Trk("b") }, 0);
        PlayAt(player, audio, 3);
        // "Stop after current track": the song ends, the player stops with it loaded.
        player.StopAfterCurrentTrack = true;
        audio.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(a.Id, player.CurrentTrack?.Id);

        player.PlayPauseCommand.Execute(null); // replays the loaded song
        Dispatcher.UIThread.RunJobs();

        Assert.All(log.Log.Where(e => e.TrackId == a.Id), e => Assert.False(e.Skipped));
    }
}
