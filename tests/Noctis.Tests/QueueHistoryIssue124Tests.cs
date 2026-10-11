using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #124: "Skip back never working". Previous walked back through played songs, except
/// in the two places the played list was wiped: the Repeat All wrap (History.Clear, so
/// Previous on the first song of a new pass did nothing) and the end of the queue
/// (StopAndClear, so nothing was left to go back to once the last song ended).
/// </summary>
public class QueueHistoryIssue124Tests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    private static void End(FakeAudioPlayer player, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            player.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Previous(PlayerViewModel vm)
    {
        vm.PreviousCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Titles(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Title));

    [AvaloniaFact]
    public void RepeatAll_AfterTheWrap_PreviousGoesBackIntoTheLastPass()
    {
        var (vm, player) = CreateVm();
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 3); // a, b, c played → wraps to a
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
        Assert.Equal("c,b,a", Titles(vm.History));

        Previous(vm);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Previous(vm);
        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal("c,a,b,c", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void RepeatAll_WrapsAgainAndAgain_WithTheSameCycle()
    {
        var (vm, player) = CreateVm();
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 6); // two full passes
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
    }

    /// <summary>No recorded cycle (songs queued onto the emptied player, GitHub #92): the wrap
    /// replays what this queue played — never the queue that ended before it — and keeping
    /// History across the wrap must not double the next pass.</summary>
    [AvaloniaFact]
    public void RepeatAll_WithoutARecordedCycle_ReplaysOnlyThisQueue_WithoutDoubling()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2); // queue ended, player emptied
        Assert.Null(vm.CurrentTrack);

        vm.AddToQueue(Trk("x"), announce: false);
        vm.AddToQueue(Trk("y"), announce: false);
        vm.PlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("x", vm.CurrentTrack?.Title);
        vm.RepeatMode = RepeatMode.All;

        End(player, 2);
        Assert.Equal("x", vm.CurrentTrack?.Title);
        Assert.Equal("y", Titles(vm.UpNext));

        End(player, 2);
        Assert.Equal("x", vm.CurrentTrack?.Title);
        Assert.Equal("y", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void QueueEnd_KeepsWhatPlayed_SoPreviousPlaysTheLastSongsAgain()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 3);
        Assert.Null(vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Empty(vm.UpNext);
        Assert.Equal("c,b,a", Titles(vm.History));

        Previous(vm);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Previous(vm);
        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal("c", Titles(vm.UpNext));
    }

    /// <summary>The kept songs belong to the queue that ended: turning Repeat All on afterwards
    /// must not make Next "wrap" an empty player (that path wiped everything).</summary>
    [AvaloniaFact]
    public void QueueEnd_ThenRepeatAll_NextStaysUnavailable()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2);

        vm.RepeatMode = RepeatMode.All;

        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.Equal("b,a", Titles(vm.History));
    }

    // ── Keep Played Songs (Settings ▸ Playback) ──

    [AvaloniaFact]
    public void KeepPlayed_QueueEnd_KeepsTheLastSongLoaded_SoTheBarAndPreviousStay()
    {
        var (vm, player) = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 3);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(TimeSpan.Zero, vm.Position);
        Assert.True(vm.HasContent); // the island stays mounted
        Assert.Empty(vm.UpNext);
        Assert.Equal("b,a", Titles(vm.History)); // c is current again, not played twice

        Previous(vm);
        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal("c", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void KeepPlayed_QueueEnd_PlayReplaysTheLastSong()
    {
        var (vm, player) = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2);

        vm.PlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal("a", Titles(vm.History));
    }

    [AvaloniaFact]
    public void KeepPlayed_QueueEnd_ThenRepeatAll_NextReplaysTheQueue()
    {
        var (vm, player) = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);
        End(player, 3);

        vm.RepeatMode = RepeatMode.All;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void KeepPlayed_Off_QueueEnd_StillEmptiesThePlayer()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2);

        Assert.Null(vm.CurrentTrack);
        Assert.False(vm.HasContent);
    }

    [AvaloniaFact]
    public void PlayedTracks_MirrorHistoryOldestFirst_AndShowOnlyWithTheSetting()
    {
        var (vm, player) = CreateVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);
        End(player, 2);

        Assert.Equal("a,b", Titles(vm.PlayedTracks));
        Assert.False(vm.ShowPlayedInQueue); // setting off

        changed.Clear();
        vm.KeepPlayedInQueue = true;
        Assert.True(vm.ShowPlayedInQueue);
        Assert.Contains(nameof(PlayerViewModel.ShowPlayedInQueue), changed);

        Previous(vm);
        Previous(vm);
        Assert.Empty(vm.PlayedTracks);
        Assert.False(vm.ShowPlayedInQueue);
    }

    [AvaloniaFact]
    public void PlayPlayedAt_PlaysThatSong_AndPutsTheRestBackInFrontOfUpNext()
    {
        var (vm, player) = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c"), Trk("d"), Trk("e") }, 0);
        End(player, 3); // a, b, c played; d playing

        vm.PlayPlayedAt(0); // "a", the oldest

        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c,d,e", Titles(vm.UpNext));
        Assert.Empty(vm.PlayedTracks);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class SettingsStore : TestPersistenceService
    {
        public AppSettings Stored { get; } = new();
        public AppSettings? Saved { get; private set; }
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Stored);
        public override Task SaveSettingsAsync(AppSettings settings)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Setting_LoadsIntoThePlayer_AndTogglingItAppliesLiveAndSaves()
    {
        var store = new SettingsStore();
        store.Stored.KeepPlayedInQueue = true;
        var lib = new FakeLibraryService();
        var settings = new SettingsViewModel(store, lib, new NoOpPlayHistory());
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, store, new FakeAnimatedCoverService());
        settings.SetPlayer(player);
        await settings.LoadAsync();

        Assert.True(settings.KeepPlayedInQueue);
        Assert.True(player.KeepPlayedInQueue);

        settings.KeepPlayedInQueue = false;
        Assert.False(player.KeepPlayedInQueue);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (store.Saved?.KeepPlayedInQueue != false && sw.ElapsedMilliseconds < 3000)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.False(store.Saved?.KeepPlayedInQueue ?? true);
    }

    /// <summary>Panel wiring (MainWindow is not mountable headlessly): the played list sits
    /// above Now Playing, outside the Up Next ListBox (its indices and drag slots unchanged),
    /// shows only with the setting, and plays the clicked row by container index.</summary>
    [Fact]
    public void QueuePanel_PlayedSection_SitsAboveNowPlaying_AndIsWired()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var xaml = File.ReadAllText(Path.Combine(root!.FullName, "src", "Noctis", "Views", "MainWindow.axaml"));
        var code = File.ReadAllText(Path.Combine(root.FullName, "src", "Noctis", "Views", "MainWindow.axaml.cs"));

        var played = xaml.IndexOf("x:Name=\"QueuePlayed\"", StringComparison.Ordinal);
        var nowPlaying = xaml.IndexOf("x:Name=\"QueueNowPlaying\"", StringComparison.Ordinal);
        var list = xaml.IndexOf("x:Name=\"QueuePopupListBox\"", StringComparison.Ordinal);
        Assert.True(played > 0 && played < nowPlaying && nowPlaying < list);

        var section = xaml.Substring(played, nowPlaying - played);
        Assert.Contains("IsVisible=\"{Binding Player.ShowPlayedInQueue}\"", section);
        Assert.Contains("ItemsSource=\"{Binding Player.PlayedTracks}\"", section);
        Assert.Contains("{loc:T Queue.Played}", section);
        Assert.Contains("DoubleTapped=\"OnQueuePlayedDoubleTapped\"", section);
        Assert.Contains("MaxHeight=", section); // never crowds Up Next out
        Assert.Contains("QueuePlayedList.IndexFromContainer", code);
        Assert.Contains("vm.Player.PlayPlayedAt(index)", code);
    }

    [AvaloniaFact]
    public void PlayPlayedAt_ASongThatPlayedTwice_PlaysTheClickedCopy()
    {
        var (vm, player) = CreateVm();
        vm.KeepPlayedInQueue = true;
        var a = Trk("a");
        vm.ReplaceQueueAndPlay(new[] { a, Trk("b"), a, Trk("c") }, 0);
        End(player, 3); // a, b, a played; c playing

        vm.PlayPlayedAt(2); // the second "a"

        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("c", Titles(vm.UpNext));
        Assert.Equal("a,b", Titles(vm.PlayedTracks));
    }
}
