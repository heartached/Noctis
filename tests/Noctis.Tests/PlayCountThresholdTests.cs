using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #101: just clicking a song counted it as played. Settings → Playback → "Count a
/// play after" (Immediately / 25 / 50 / 75 / 90 %) now holds the play count, Last Played and
/// the play log back until that share of the song has actually been heard — seeks don't
/// count as listening. Immediately (the default) keeps counting at the start.
/// </summary>
public class PlayCountThresholdTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public PlayCountThresholdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"noctis-playcount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Play log with PlayHistoryService's skip rule: the newest event of the track is marked.</summary>
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

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed record Rig(PlayerViewModel Player, FakeAudioPlayer Audio, RecordingPlayHistory Log,
        SettingsViewModel Settings, FakeLibraryService Library, TestPersistenceService Persistence);

    private Track Trk(string name, TimeSpan? duration = null)
    {
        var path = Path.Combine(_dir, $"{name}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new()
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "A",
            FilePath = path,
            Duration = duration ?? TimeSpan.FromMinutes(3)
        };
    }

    private static Rig Create(int percent)
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(audio, library, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService())
        {
            PlayCountThresholdPercent = percent
        };
        player.SetSettingsViewModel(settings);
        var log = new RecordingPlayHistory();
        player.SetPlayHistory(log);
        return new Rig(player, audio, log, settings, library, persistence);
    }

    private static void Start(Rig rig, params Track[] queue)
    {
        rig.Player.ReplaceQueueAndPlay(queue, 0);
        Dispatcher.UIThread.RunJobs();
        SettleSeekGuards(rig.Player);
    }

    // Pretend the last start/seek happened long ago so the seek-settle guards pass the ticks
    // (same trick as PlaybackRateStaleGuardTests).
    private static void SettleSeekGuards(PlayerViewModel vm) =>
        typeof(PlayerViewModel)
            .GetField("_lastSeekTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.UtcNow - TimeSpan.FromMinutes(10));

    private static void Tick(Rig rig, double seconds)
    {
        rig.Audio.RaisePositionChanged(TimeSpan.FromSeconds(seconds));
        Dispatcher.UIThread.RunJobs();
    }

    // One tick per second of playback, from just after `from` through `to`.
    private static void Listen(Rig rig, int from, int to)
    {
        for (var s = from + 1; s <= to; s++) Tick(rig, s);
    }

    [AvaloniaFact]
    public void Immediately_TheDefault_CountsAtTheStart()
    {
        Assert.Equal(0, new AppSettings().PlayCountThresholdPercent);
        var rig = Create(0);
        var counted = new List<Track>();
        rig.Player.PlayCounted += (_, t) => counted.Add(t);
        var a = Trk("a");

        Start(rig, a);

        Assert.Equal(1, a.PlayCount);
        Assert.NotNull(a.LastPlayed);
        Assert.Single(rig.Log.Log, e => e.TrackId == a.Id);
        Assert.Equal(new[] { a }, counted);
    }

    [AvaloniaFact]
    public void Half_ShortListenThenSkip_RecordsNothing()
    {
        var rig = Create(50);
        var a = Trk("a");
        var b = Trk("b");

        Start(rig, a, b);
        Listen(rig, 0, 10);
        rig.Player.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, a.PlayCount);
        Assert.Null(a.LastPlayed);
        Assert.DoesNotContain(rig.Log.Log, e => e.TrackId == a.Id);
    }

    [AvaloniaFact]
    public void Half_CountsOnce_WhenHalfTheSongHasBeenHeard()
    {
        var rig = Create(50);
        var counted = 0;
        rig.Player.PlayCounted += (_, _) => counted++;
        var a = Trk("a"); // 3:00 → 90 s

        Start(rig, a);
        Listen(rig, 0, 89);
        Assert.Equal(0, a.PlayCount);
        Assert.Empty(rig.Log.Log);

        Tick(rig, 90);
        Assert.Equal(1, a.PlayCount);
        Assert.NotNull(a.LastPlayed);

        Listen(rig, 90, 150);
        Assert.Equal(1, a.PlayCount);
        Assert.Single(rig.Log.Log);
        Assert.Equal(1, counted);
    }

    [AvaloniaFact]
    public void SeekingToTheEnd_IsNotListening()
    {
        var rig = Create(50);
        var a = Trk("a");

        Start(rig, a);
        Listen(rig, 0, 5);
        rig.Player.SeekTo(TimeSpan.FromSeconds(165));
        SettleSeekGuards(rig.Player);
        Listen(rig, 165, 175);
        // A jump the player never announced as a seek adds nothing either.
        Tick(rig, 20);
        Tick(rig, 110);

        Assert.Equal(0, a.PlayCount);
        Assert.Empty(rig.Log.Log);
    }

    [AvaloniaFact]
    public void MidSongStart_CountsOnlyWhatIsHeard()
    {
        var rig = Create(50);
        var a = Trk("a");
        a.StartTimeMs = 100_000; // starts past the halfway point of 3:00

        Start(rig, a);
        Listen(rig, 100, 179); // 79 s heard, short of 90

        Assert.Equal(0, a.PlayCount);
        Assert.Empty(rig.Log.Log);
    }

    [AvaloniaFact]
    public void UnknownDuration_CountsAtTheStart()
    {
        var rig = Create(90);
        var stream = Trk("stream", TimeSpan.Zero);

        Start(rig, stream);

        Assert.Equal(1, stream.PlayCount);
        Assert.Single(rig.Log.Log);
    }

    [AvaloniaFact]
    public void ChangingTheSettingMidSong_AppliesToTheRestOfThePlay()
    {
        var rig = Create(50);
        var a = Trk("a");

        Start(rig, a);
        Listen(rig, 0, 30);
        rig.Settings.PlayCountThresholdPercent = 25; // 45 s of 3:00
        Listen(rig, 30, 44);
        Assert.Equal(0, a.PlayCount);

        Tick(rig, 45);
        Assert.Equal(1, a.PlayCount);
    }

    [AvaloniaFact]
    public void SkippingAnUncountedPlay_LeavesTheEarlierPlayOfThatSongAlone()
    {
        var rig = Create(0);
        var a = Trk("a");
        var b = Trk("b");
        Start(rig, a); // counted at the start: the earlier, full play of `a`
        // Heard past halfway, so it is a full play: leaving a song in its first half is a
        // skip however it is left (SkipOnReplaceTests), and at 0:00 this one would be.
        Listen(rig, 0, 100);

        rig.Settings.PlayCountThresholdPercent = 50;
        Start(rig, a, b);
        Listen(rig, 0, 10);
        rig.Player.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var only = Assert.Single(rig.Log.Log, e => e.TrackId == a.Id);
        Assert.False(only.Skipped);
        Assert.Equal(1, a.PlayCount);
    }

    [AvaloniaFact]
    public void SkippingACountedPlayEarly_StillMarksItSkipped()
    {
        var rig = Create(25);
        var a = Trk("a");
        var b = Trk("b");

        Start(rig, a, b);
        Listen(rig, 0, 50); // counted at 45 s, skipped before the halfway point
        rig.Player.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var only = Assert.Single(rig.Log.Log, e => e.TrackId == a.Id);
        Assert.True(only.Skipped);
    }

    [AvaloniaFact]
    public void HomeLastPlayed_WaitsForTheCountedPlay()
    {
        var rig = Create(50);
        var a = Trk("a");
        var home = new HomeViewModel(rig.Player, rig.Library, new SidebarViewModel(rig.Persistence, rig.Library),
            playHistory: rig.Log);

        Start(rig, a);
        Listen(rig, 0, 10);
        Assert.DoesNotContain(a, home.LastPlayed);

        Listen(rig, 10, 90);
        Assert.Contains(a, home.LastPlayed);
    }

    [AvaloniaFact]
    public async Task Setting_SurvivesSaveAndReload_AndResetPutsItBack()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.Equal(0, vm.PlayCountThresholdPercent);
        Assert.Equal(new[] { 0, 25, 50, 75, 90 }, vm.PlayCountThresholdOptions.Select(o => o.Percent));

        vm.SelectedPlayCountThresholdOption = vm.PlayCountThresholdOptions.Single(o => o.Percent == 75);
        Assert.Equal(75, vm.PlayCountThresholdPercent);
        await vm.SaveAsync();

        var reloaded = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await reloaded.LoadAsync();
        Assert.Equal(75, reloaded.PlayCountThresholdPercent);
        Assert.Equal(75, reloaded.SelectedPlayCountThresholdOption?.Percent);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal(0, reloaded.PlayCountThresholdPercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 50)]
    [InlineData(100, 90)]
    [InlineData(-5, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 90)]
    public void HandEditedValue_SnapsToTheNearestChoice(int stored, int expected)
    {
        var settings = new AppSettings { PlayCountThresholdPercent = stored };
        settings.ClampToValidRanges();
        Assert.Equal(expected, settings.PlayCountThresholdPercent);
    }
}
