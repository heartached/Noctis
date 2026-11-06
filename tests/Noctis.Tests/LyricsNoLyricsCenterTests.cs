using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord request: the lyrics page centres the player when the song has no lyrics
/// (Settings ▸ "Center the player when a song has no lyrics", on by default). Pins
/// <see cref="LyricsViewModel.IsNoLyricsCentered"/>: on only once it is KNOWN there are none
/// (nothing found, instrumental, marked as none), held while lyrics are loading so a load
/// never bounces the page between layouts, off when lines arrive or the setting is off.
/// </summary>
public class LyricsNoLyricsCenterTests : IDisposable
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => GetImpl();

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    private const string Lrc = "[00:01.00]line timed";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "nolyrcenter-" + Guid.NewGuid().ToString("N"));

    public LyricsNoLyricsCenterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Rig(LyricsViewModel Vm, PlayerViewModel Player, StubLrcLib LrcLib, SettingsPersistence Persistence, List<bool> History);

    private Rig Mount(StubLrcLib? lrcLib = null, bool autoDownload = false, bool center = true)
    {
        lrcLib ??= new StubLrcLib();
        var persistence = new SettingsPersistence();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            LyricsAutoDownload = autoDownload,
            LyricsCenterWhenNoLyrics = center,
        };
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new MetadataService(), persistence, new FakeLibraryService())
        {
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        var history = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LyricsViewModel.IsNoLyricsCentered))
                history.Add(vm.IsNoLyricsCentered);
        };
        vm.SetLyricsSurfaceVisible(true);
        return new Rig(vm, player, lrcLib, persistence, history);
    }

    private Track NewTrack(string? syncedLyrics = null) => new()
    {
        Title = "Test Song",
        Artist = "Test Artist",
        Duration = TimeSpan.FromSeconds(200),
        FilePath = Path.Combine(_dir, $"nofile-{Guid.NewGuid():N}.mp3"),
        SourceType = SourceType.Local,
        SyncedLyrics = syncedLyrics ?? string.Empty,
    };

    private static LrcLibResult Result(string? synced = null, bool instrumental = false) => new()
    {
        TrackName = "Test Song",
        ArtistName = "Test Artist",
        Duration = 200,
        SyncedLyrics = synced,
        PlainLyrics = synced == null ? null : "line timed",
        Instrumental = instrumental,
    };

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SettleAsync(LyricsViewModel vm)
    {
        await PumpUntilAsync(() => false, 150);
        await PumpUntilAsync(() => !vm.IsSearching);
        for (var i = 0; i < 3; i++)
        {
            await LyricsViewModel.EnqueueLyricsFileWork(() => { });
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task OpenAsync(Rig rig, Track track)
    {
        rig.Player.CurrentTrack = track;
        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);
    }

    [AvaloniaFact]
    public async Task NoLyricsFound_Centers()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack());

        Assert.True(rig.Vm.ShowSearchButton);
        Assert.True(rig.Vm.IsNoLyricsCentered);
    }

    [AvaloniaFact]
    public async Task LyricsFound_StaysSplit()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack(Lrc));

        Assert.NotEmpty(rig.Vm.LyricLines);
        Assert.False(rig.Vm.IsNoLyricsCentered);
        Assert.Empty(rig.History);
    }

    [AvaloniaFact]
    public async Task ToggleOff_NeverCenters_AndFlippingItMovesThePageLive()
    {
        var rig = Mount(center: false);
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.ShowSearchButton);
        Assert.False(rig.Vm.IsNoLyricsCentered);

        rig.Player.LyricsCenterWhenNoLyrics = true;
        Assert.True(rig.Vm.IsNoLyricsCentered);

        rig.Player.LyricsCenterWhenNoLyrics = false;
        Assert.False(rig.Vm.IsNoLyricsCentered);
    }

    [AvaloniaFact]
    public async Task Instrumental_Centers()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(instrumental: true)) },
            autoDownload: true);
        await OpenAsync(rig, NewTrack());

        Assert.Equal("This track is instrumental.", rig.Vm.SearchFailedMessage);
        Assert.True(rig.Vm.IsNoLyricsCentered);
        // The island's Search Lyrics says why there are none (the old row's caption).
        Assert.Equal("This track is instrumental.\nSearch Lyrics", rig.Vm.NoLyricsSearchTip);
        // Probe "none" → automatic search → instrumental: one switch, at the end.
        Assert.Equal(new[] { true }, rig.History);
    }

    [AvaloniaFact]
    public async Task MarkedNoLyrics_Centers()
    {
        var rig = Mount();
        var track = NewTrack();
        track.NoLyrics = true;
        await OpenAsync(rig, track);

        Assert.True(rig.Vm.IsMarkedNoLyrics);
        Assert.True(rig.Vm.IsNoLyricsCentered);
        Assert.Equal("Marked as no lyrics\nSearch Lyrics", rig.Vm.NoLyricsSearchTip);
    }

    /// <summary>Bug: ShareAvailable only looked at the search states, so a song marked as having
    /// no lyrics kept the player bar's ⋯ ▸ Share Lyrics enabled — a row that did nothing
    /// (ShareLyrics returns on zero lines).</summary>
    [AvaloniaFact]
    public async Task MarkedNoLyrics_IsNotShareable()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack(Lrc));
        Assert.True(rig.Vm.ShareAvailable);

        var changed = false;
        rig.Vm.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(LyricsViewModel.ShareAvailable);
        await rig.Vm.MarkNoLyricsCommand.ExecuteAsync(null);
        await SettleAsync(rig.Vm);

        Assert.True(rig.Vm.IsMarkedNoLyrics);
        Assert.Empty(rig.Vm.LyricLines);
        Assert.False(rig.Vm.ShareAvailable);
        Assert.True(changed);
        Assert.True(rig.Vm.IsNoLyricsCentered);
    }

    [AvaloniaFact]
    public async Task WhileLoading_DoesNotCenter()
    {
        var gate = new TaskCompletionSource<LrcLibResult?>();
        var rig = Mount(new StubLrcLib { GetImpl = () => gate.Task }, autoDownload: true);
        rig.Player.CurrentTrack = NewTrack();
        rig.Vm.EnsureLyricsForCurrentTrack();
        // Local probe found nothing, the automatic search is in flight: not known yet.
        await PumpUntilAsync(() => rig.Vm.IsSearching);
        Assert.True(rig.Vm.IsSearching);
        Assert.False(rig.Vm.IsNoLyricsCentered);

        gate.SetResult(Result(synced: Lrc));
        await SettleAsync(rig.Vm);

        Assert.NotEmpty(rig.Vm.LyricLines);
        Assert.False(rig.Vm.IsNoLyricsCentered);
        // Never centred on the way: no flash of the centred layout before the lyrics landed.
        Assert.Empty(rig.History);
    }

    [AvaloniaFact]
    public async Task LyricsFoundLater_Uncenters_AndHoldsWhileSearching()
    {
        var gate = new TaskCompletionSource<LrcLibResult?>();
        var rig = Mount(new StubLrcLib { GetImpl = () => gate.Task });
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.IsNoLyricsCentered);

        rig.Vm.SearchLyricsCommand.Execute(null);
        Assert.True(rig.Vm.IsSearching);
        Assert.True(rig.Vm.IsNoLyricsCentered); // held while the search runs

        gate.SetResult(Result(synced: Lrc));
        await SettleAsync(rig.Vm);

        Assert.NotEmpty(rig.Vm.LyricLines);
        Assert.False(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(new[] { true, false }, rig.History);
    }

    [AvaloniaFact]
    public async Task TrackChange_HoldsUntilTheNewSongIsKnown()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.IsNoLyricsCentered);

        // Another song with no lyrics: stays centred the whole way, no bounce.
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(new[] { true }, rig.History);

        // A song with lyrics: back to split once they are in.
        await OpenAsync(rig, NewTrack(Lrc));
        Assert.False(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(new[] { true, false }, rig.History);

        // And from lyrics to none: split while loading, centred once known.
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(new[] { true, false, true }, rig.History);
    }

    [AvaloniaFact]
    public async Task RemoveLyrics_Centers()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack(Lrc));
        Assert.False(rig.Vm.IsNoLyricsCentered);

        await rig.Vm.RemoveLyricsCommand.ExecuteAsync(null);
        await SettleAsync(rig.Vm);

        Assert.True(rig.Vm.IsNoLyricsCentered);
    }

    [AvaloniaFact]
    public async Task QueueEnd_Uncenters()
    {
        var rig = Mount();
        await OpenAsync(rig, NewTrack());
        Assert.True(rig.Vm.IsNoLyricsCentered);

        rig.Player.CurrentTrack = null;
        Assert.False(rig.Vm.IsNoLyricsCentered);
    }

    /// <summary>Bug: with every lyrics provider off, the search set "Lyrics providers are turned
    /// off in Settings." but hid the Search Lyrics state that is the only place the page shows
    /// that message (LyricsView.axaml: the message sits inside the panel bound to
    /// ShowSearchButton), so the page went blank with no hint and no way to open the search.</summary>
    [AvaloniaFact]
    public async Task ProvidersOff_ShowsTheMessageAndSearchState()
    {
        var rig = Mount();
        rig.Persistence.Settings.LrcLibEnabled = false;
        await OpenAsync(rig, NewTrack());

        rig.Vm.SearchLyricsCommand.Execute(null);
        await SettleAsync(rig.Vm);

        Assert.Equal("Lyrics providers are turned off in Settings.", rig.Vm.SearchFailedMessage);
        Assert.True(rig.Vm.ShowSearchButton);
        Assert.True(rig.Vm.IsNoLyricsCentered);
    }
}

/// <summary>The lyrics page applying <see cref="LyricsViewModel.IsNoLyricsCentered"/>: the left
/// panel spans the page, the lyrics column fades out of the way, and Search Lyrics stays
/// reachable as a button inside the player island; narrow windows keep the lyrics column.</summary>
public class LyricsNoLyricsCenterViewTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private static (LyricsViewModel Vm, PlayerViewModel Player) MakeViewModel()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        player.CurrentTrack = new Track
        {
            Title = "Instrumental",
            Artist = "Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-center-{Guid.NewGuid():N}.mp3"),
            SourceType = SourceType.Local,
        };
        return (vm, player);
    }

    /// <summary>Runs frames for a stretch of real time, so transitions play out.</summary>
    private static async Task PumpAsync(int ms = 900)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static async Task SettleAsync(LyricsViewModel vm)
    {
        for (var i = 0; i < 40 && !vm.ShowSearchButton; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        await PumpAsync();
    }

    [AvaloniaFact]
    public async Task WideWindow_Centers_AndSearchLyricsStaysReachable_ThenLyricsSplitItAgain()
    {
        var (vm, player) = MakeViewModel();
        player.IsLyricsPageActive = true; // the lyrics-page pill's own (compact) width
        var opened = 0;
        vm.ShowLyricsSearchDialog = _ => { opened++; return Task.CompletedTask; };
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Avalonia.Controls.Window { Width = 1600, Height = 900, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            Assert.True(vm.IsNoLyricsCentered);

            var left = view.FindControl<Avalonia.Controls.Border>("LeftPanel")!;
            var right = view.FindControl<Avalonia.Controls.Grid>("RightPanel")!;
            var stack = view.FindControl<Avalonia.Controls.StackPanel>("LeftContentStack")!;
            var bar = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!;
            var button = bar.FindControl<Avalonia.Controls.Button>("LyricsSearchButton")!;
            var island = bar.FindControl<Avalonia.Controls.Border>("IslandBorder")!;
            await PumpAsync();

            Assert.Equal(2, Avalonia.Controls.Grid.GetColumnSpan(left));
            Assert.False(right.IsHitTestVisible);
            Assert.Equal(0.0, right.Opacity, 2);

            // The stack sits in the middle of the page.
            var stackCentre = stack.TranslatePoint(new Avalonia.Point(stack.Bounds.Width / 2, 0), win)!.Value.X;
            Assert.Equal(800, stackCentre, 0);

            // Search Lyrics sits inside the island at full size, and the pill keeps its
            // lyrics-page width with every button still inside it.
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(34, button.Bounds.Width, 1);
            var pillWidth = island.Bounds.Width; // 340 + the opt-in extras (mini player is on)
            Assert.Equal(376, pillWidth, 1);
            var islandRect = new Avalonia.Rect(island.TranslatePoint(default, win)!.Value, island.Bounds.Size);
            foreach (var b in island.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                var r = new Avalonia.Rect(b.TranslatePoint(default, win)!.Value, b.Bounds.Size);
                Assert.True(islandRect.Contains(r), $"{b.Name ?? b.GetType().Name} {r} spills out of the island {islandRect}");
            }
            Assert.Equal("Search Lyrics", Avalonia.Controls.ToolTip.GetTip(button));
            Assert.Equal("Search Lyrics", Avalonia.Automation.AutomationProperties.GetName(button));

            // It takes the click and opens the search dialog.
            var centre = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), win)!.Value;
            Assert.InRange(centre.Y, 0, 900);
            var hit = win.InputHitTest(centre) as Avalonia.Visual;
            Assert.NotNull(hit);
            Assert.True(ReferenceEquals(hit, button) || Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(button, hit!),
                $"hit {hit?.GetType().Name}, not the Search Lyrics button");
            button.Command!.Execute(button.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, opened);

            // A search running: the tooltip says so.
            vm.IsSearching = true;
            Assert.Equal("Searching for Lyrics", Avalonia.Controls.ToolTip.GetTip(button));
            vm.IsSearching = false;

            // Lyrics land (every apply path drops the Search Lyrics state first): back to split.
            vm.ShowSearchButton = false;
            Assert.True(vm.IsNoLyricsCentered); // held: not known yet
            vm.LyricLines.Add(new LyricLine { Text = "found", Timestamp = TimeSpan.FromSeconds(1) });
            Assert.False(vm.IsNoLyricsCentered);
            // Mid-slide the button is still there, narrowing (its width animates, no jump).
            await PumpAsync(60);
            Assert.True(button.IsVisible);
            Assert.InRange(button.Bounds.Width, 0.5, 33.5);
            await PumpAsync();
            Assert.Equal(1, Avalonia.Controls.Grid.GetColumnSpan(left));
            Assert.True(right.IsHitTestVisible);
            Assert.Equal(1.0, right.Opacity, 2);
            // The island gives the button back once it has slid shut; the pill never resized.
            Assert.False(button.IsVisible);
            Assert.Equal(pillWidth, island.Bounds.Width, 1);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task NarrowWindow_KeepsTheLyricsColumn()
    {
        var (vm, _) = MakeViewModel();
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Avalonia.Controls.Window { Width = 800, Height = 900, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            Assert.True(vm.IsNoLyricsCentered);

            var right = view.FindControl<Avalonia.Controls.Grid>("RightPanel")!;
            var bar = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!;
            Assert.True(right.IsHitTestVisible);
            Assert.Equal(1.0, right.Opacity, 2);
            // The lyrics column keeps its own Search Lyrics; the island stays as it was.
            Assert.False(bar.FindControl<Avalonia.Controls.Button>("LyricsSearchButton")!.IsVisible);
        }
        finally
        {
            win.Close();
        }
    }
}
