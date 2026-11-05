using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Andre, 10-28): Songs sorted by Plays (descending) never re-ranked while he
/// played. A counted play raises no LibraryUpdated, so the list stayed clean: the Plays
/// cell counted up (Track.PlayCount is observable) but the row kept its old place.
/// The row now moves once its song ends (Settings → Playback → Re-rank Plays at song
/// end), or on the next song's start when that is off — and only that row.
/// </summary>
public class LibrarySongsPlaysReRankTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-songs-rerank-{Guid.NewGuid():N}");

    public LibrarySongsPlaysReRankTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Track Trk(string title, int plays)
    {
        var path = Path.Combine(_dir, $"{title}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = "Artist",
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3),
            PlayCount = plays,
        };
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(LibrarySongsViewModel vm, PlayerViewModel player, Track a, Track b, Track c)> CreateAsync(string sort = "Plays")
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var a = Trk("a", 10);
        var b = Trk("b", 10);
        var c = Trk("c", 0);
        lib.TrackList.AddRange(new[] { a, b, c });

        vm.SelectSortCommand.Execute(sort);
        vm.SelectSortCommand.Execute("Descending");
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 3);
        return (vm, player, a, b, c);
    }

    private static string Order(LibrarySongsViewModel vm) => string.Concat(vm.FilteredTracks.Select(t => t.Title));

    [AvaloniaFact]
    public async Task CountedPlay_CountsInPlace_ThenReRanksWhenTheSongIsOver()
    {
        var (vm, player, _, b, _) = await CreateAsync();
        Assert.Equal("abc", Order(vm)); // tie at 10: library order

        var cellRaised = false;
        b.PropertyChanged += (_, e) => cellRaised |= e.PropertyName == nameof(Track.PlayCount);
        player.PlayFromRow(vm.FilteredTracks.ToList(), 1);
        await PumpFor(200);

        // The cell counts up at once; the playing row stays put.
        Assert.Equal(11, b.PlayCount);
        Assert.True(cellRaised);
        Assert.Equal("abc", Order(vm));

        var changes = new List<NotifyCollectionChangedAction>();
        vm.FilteredTracks.CollectionChanged += (_, e) => changes.Add(e.Action);
        player.NextCommand.Execute(null); // b is over, c starts

        await PumpUntil(() => Order(vm) == "bac");
        Assert.Equal("bac", Order(vm));
        // Moved, not rebuilt: the rest of the rows (and the view's scroll/containers) stay.
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, changes);
    }

    [AvaloniaFact]
    public async Task ReRank_WaitsWhileTheUserIsBusyWithTheList()
    {
        var (vm, player, _, _, _) = await CreateAsync();
        var busy = true;
        vm.IsUserBusy = () => busy;

        player.PlayFromRow(vm.FilteredTracks.ToList(), 1);
        player.NextCommand.Execute(null);
        await PumpFor(300);
        Assert.Equal("abc", Order(vm)); // held while scrolling / rows selected

        busy = false;
        await PumpUntil(() => Order(vm) == "bac", budgetMs: 6000); // retry lands it
        Assert.Equal("bac", Order(vm));
    }

    [AvaloniaFact]
    public async Task CountedPlay_UnderAnotherSort_LeavesTheListAlone()
    {
        var (vm, player, _, _, _) = await CreateAsync(sort: "Title");
        var changes = 0;
        vm.FilteredTracks.CollectionChanged += (_, _) => changes++;

        player.PlayFromRow(vm.FilteredTracks.ToList(), 1);
        player.NextCommand.Execute(null);
        await PumpFor(300);

        Assert.Equal(0, changes);
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private async Task<(LibrarySongsViewModel vm, PlayerViewModel player, Track b)> CreateWithSettingAsync(bool reRankAtEnd)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        settings.PlaysReRankAtSongEnd = reRankAtEnd;
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence, settings);
        var a = Trk("a", 10);
        var b = Trk("b", 10);
        var c = Trk("c", 0);
        lib.TrackList.AddRange(new[] { a, b, c });

        vm.SelectSortCommand.Execute("Plays");
        vm.SelectSortCommand.Execute("Descending");
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 3);

        player.PlayFromRow(vm.FilteredTracks.ToList(), 1); // b counts 10 -> 11, stays put
        await PumpFor(200);
        Assert.Equal(11, b.PlayCount);
        Assert.Equal("abc", Order(vm));
        return (vm, player, b);
    }

    [AvaloniaFact]
    public async Task CountedPlay_AtEndMode_ReRanksWhenSongEndsWithNoNext()
    {
        var (vm, player, _) = await CreateWithSettingAsync(reRankAtEnd: true);

        player.StopAndClear("test");
        await PumpUntil(() => Order(vm) == "bac", budgetMs: 6000);

        Assert.Equal("bac", Order(vm));
    }

    [AvaloniaFact]
    public async Task CountedPlay_OnChangeMode_HoldsUntilNextSongStarts()
    {
        var (vm, player, _) = await CreateWithSettingAsync(reRankAtEnd: false);

        // Song ends with no next: off means the row holds its old place.
        player.StopAndClear("test");
        await PumpFor(300);
        Assert.Equal("abc", Order(vm));

        // The pending move lands when the next song starts.
        player.PlayFromRow(vm.FilteredTracks.ToList(), 2); // c counts 0 -> 1
        await PumpUntil(() => Order(vm) == "bac", budgetMs: 6000);
        Assert.Equal("bac", Order(vm));
    }
}
