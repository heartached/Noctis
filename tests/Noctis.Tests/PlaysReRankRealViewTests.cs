using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Andre, 11-04, v1.6.7): Songs sorted by Plays, descending — a playing song showed 17
/// plays among the 15s. The real Songs view in a window, Andre's ties (17, 16 ×4, 15 ×150),
/// sorted by two Plays header clicks, driven through the player's own paths.
/// </summary>
public class PlaysReRankRealViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-rerank-view-{Guid.NewGuid():N}");
    public PlaysReRankRealViewTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static async Task PumpFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(5); }
        Pump(6);
    }

    private sealed record Rig(LibrarySongsViewModel Vm, PlayerViewModel Player, FakeAudioPlayer Audio,
        FakeLibraryService Lib, LibrarySongsView View, Window Win, SettingsViewModel Settings);

    private Rig Mount(int percent)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var counts = new List<int> { 17, 16, 16, 16, 16 };
        for (var i = 0; i < 150; i++) counts.Add(15);
        for (var i = 0; i < 100; i++) counts.Add(3);
        for (var i = 0; i < counts.Count; i++)
        {
            var path = Path.Combine(_dir, $"s{i}.mp3");
            File.WriteAllBytes(path, new byte[] { 0 });
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"S{i:000}", Artist = "Artist", Album = "Album",
                FilePath = path, Duration = TimeSpan.FromMinutes(3), PlayCount = counts[i],
            });
        }
        var persistence = new TestPersistenceService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService())
        { PlayCountThresholdPercent = percent };
        player.SetSettingsViewModel(settings);
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        // Header clicks: Plays (ascending), Plays again (descending).
        vm.SortCommand.Execute("Plays");
        vm.SortCommand.Execute("Plays");
        return new Rig(vm, player, audio, lib, view, win, settings);
    }

    private static int IndexOf(Rig r, Track t) => r.Vm.FilteredTracks.IndexOf(t);

    /// <summary>The list is in Plays order, descending.</summary>
    private static bool Ranked(Rig r)
    {
        var l = r.Vm.FilteredTracks;
        for (var i = 1; i < l.Count; i++) if (l[i - 1].PlayCount < l[i].PlayCount) return false;
        return true;
    }

    // Rows 0-4 are 17, 16, 16, 16, 16; row 8 is a 15.
    private async Task<(Rig r, Track x)> StartRow8(int percent, bool wholeList)
    {
        var r = Mount(percent);
        await PumpFor(400);
        var x = r.Vm.FilteredTracks[8];
        Assert.Equal(15, x.PlayCount);
        if (wholeList) r.Vm.PlayFromHereCommand.Execute(x);
        else r.Player.ReplaceQueueAndPlay(new[] { x }, 0); // a single song: Home, search, a menu's Play
        await PumpFor(200);
        Listen(r, percent);
        Assert.Equal(16, x.PlayCount);
        Assert.Equal(8, IndexOf(r, x)); // counted in place while it plays
        return (r, x);
    }

    private void Settle(Rig r) => typeof(PlayerViewModel)
        .GetField("_lastSeekTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .SetValue(r.Player, DateTime.UtcNow - TimeSpan.FromMinutes(10));

    /// <summary>Plays the current song through (GitHub #101 threshold); nothing for Immediately.</summary>
    private void Listen(Rig r, int percent)
    {
        if (percent == 0) return;
        Settle(r);
        for (var s = 1; s <= 179; s++) { r.Audio.RaisePositionChanged(TimeSpan.FromSeconds(s)); Dispatcher.UIThread.RunJobs(); }
    }

    /// <summary>
    /// The root cause: the last song of the queue ends, no next song starts, so the row never
    /// moved; played again it counted to 17 still among the 15s (Andre's screenshot).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(0, true)]
    public async Task QueueEnds_TheRowMovesOnceItsSongIsOver(int percent, bool keepPlayed)
    {
        var (r, x) = await StartRow8(percent, wholeList: false);
        r.Player.KeepPlayedInQueue = keepPlayed;

        r.Audio.RaiseTrackEnded();
        await PumpFor(500);
        Assert.Equal(PlaybackState.Stopped, r.Player.State);
        Assert.InRange(IndexOf(r, x), 1, 5); // among the 16s
        Assert.True(Ranked(r));

        // Played again: 17, and it ranks with the 17 once that song is over too.
        r.Player.ReplaceQueueAndPlay(new[] { x }, 0);
        await PumpFor(200);
        Listen(r, percent);
        Assert.Equal(17, x.PlayCount);
        r.Audio.RaiseTrackEnded();
        await PumpFor(500);
        Assert.InRange(IndexOf(r, x), 0, 1);
        Assert.True(Ranked(r));
        r.Win.Close();
    }

    /// <summary>
    /// "Count a play after: Immediately" (the default) counts the next song as it starts, so
    /// the re-rank of the finished one saw a second changed row, gave up on the move and
    /// replaced the whole list: the song that just started jumped from under the pointer.
    /// </summary>
    [AvaloniaFact]
    public async Task Immediately_TheFinishedRowMoves_TheStartedOneHoldsItsPlace()
    {
        var (r, x) = await StartRow8(0, wholeList: true);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        r.Vm.FilteredTracks.CollectionChanged += (_, e) => changes.Add(e.Action);

        r.Audio.RaiseTrackEnded();
        await PumpFor(500);
        var y = r.Player.CurrentTrack!;
        Assert.Equal(16, y.PlayCount);
        Assert.Equal(5, IndexOf(r, x));  // 17, 16 ×4, then x
        Assert.Equal(9, IndexOf(r, y));  // still where it was clicked/started (row 8 moved out above it)
        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Move }, changes);

        r.Audio.RaiseTrackEnded();       // y is over now: it moves
        await PumpFor(500);
        Assert.Equal(6, IndexOf(r, y));
        r.Win.Close();
    }

    /// <summary>Repeat One: the repeating song is the playing row — it holds still through the
    /// repeats and ranks once it is over (repeat off and it ends, or another song starts).</summary>
    [AvaloniaFact]
    public async Task RepeatOne_HoldsWhileRepeating_MovesOnceOver()
    {
        var (r, x) = await StartRow8(0, wholeList: true);
        r.Player.RepeatMode = RepeatMode.One;
        r.Audio.RaiseTrackEnded();
        await PumpFor(500);
        Assert.Equal(17, x.PlayCount);
        Assert.Equal(8, IndexOf(r, x));

        r.Player.RepeatMode = RepeatMode.Off;
        r.Audio.RaiseTrackEnded();
        await PumpFor(500);
        Assert.Equal(1, IndexOf(r, x)); // after the other 17 (library order)
        r.Win.Close();
    }
}
