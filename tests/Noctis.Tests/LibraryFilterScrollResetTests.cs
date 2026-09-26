using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// U21: the Songs/Albums/Artists views scrolled to the top on EVERY rebuild of their rows
/// while a search was active, so a metadata save, an analysis pass or a scan tick (all
/// LibraryUpdated reloads) threw the user out of the results they were scrolled into. And
/// the reset was also gated on SavedScrollOffset, which is never cleared: after one scrolled
/// visit a new search stopped scrolling to the top for the rest of the session. The reset
/// now keys on the filter itself.
/// </summary>
public class LibraryFilterScrollResetTests
{
    private const double ScrolledTo = 1500;

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

    /// <summary>Pumps jobs + layout until the condition holds (or 5 s); the rebuilds run off-thread.</summary>
    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump(6);
    }

    private static ScrollViewer Scroller(ListBox list) => list.FindDescendantOfType<ScrollViewer>()!;

    private static async Task ScrollTo(ScrollViewer sv, double y)
    {
        sv.Offset = new Vector(0, y);
        await PumpUntil(() => Math.Abs(sv.Offset.Y - y) < 1);
        Assert.Equal(y, sv.Offset.Y, 0);
    }

    private static (LibrarySongsViewModel vm, FakeLibraryService lib, LibrarySongsView view, Window win) MountSongs()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 400; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = "Album",
                FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"), Duration = TimeSpan.FromMinutes(3),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        return (vm, lib, view, win);
    }

    [AvaloniaFact]
    public async Task Songs_LibraryReloadWhileSearching_KeepsTheScrollPosition()
    {
        var (vm, lib, view, win) = MountSongs();
        vm.ApplyFilter("song");
        await PumpUntil(() => vm.FilteredTracks.Count == 400 && vm.HasActiveFilter);
        var sv = Scroller(view.TrackList);
        await ScrollTo(sv, ScrolledTo);

        // A metadata save / analysis pass / scan tick: LibraryUpdated re-fills the same results.
        var resets = 0;
        vm.FilteredTracks.CollectionChanged += (_, _) => resets++;
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => resets > 0);

        Assert.Equal(1, resets);
        Assert.Equal(ScrolledTo, sv.Offset.Y, 0);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Songs_NewSearchAfterLeavingAndReturning_ScrollsToTheTop()
    {
        var (vm, _, view, win) = MountSongs();
        vm.ApplyFilter("song");
        await PumpUntil(() => vm.FilteredTracks.Count == 400 && vm.HasActiveFilter);
        await ScrollTo(Scroller(view.TrackList), ScrolledTo);

        // Leave and come back: the detach saves the offset and the attach restores it.
        win.Content = null;
        Pump();
        win.Content = view;
        var sv = Scroller(view.TrackList);
        await PumpUntil(() => Math.Abs(sv.Offset.Y - ScrolledTo) < 1);
        Assert.Equal(ScrolledTo, sv.Offset.Y, 0);

        // A different search must start its results at the top.
        vm.ApplyFilter("song 1");
        await PumpUntil(() => vm.FilteredTracks.Count < 400 && sv.Offset.Y == 0);

        Assert.True(vm.FilteredTracks.Count > 100);
        Assert.Equal(0, sv.Offset.Y);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Songs_NewSearch_StillScrollsToTheTop()
    {
        var (vm, _, view, win) = MountSongs();
        vm.ApplyFilter("song");
        await PumpUntil(() => vm.FilteredTracks.Count == 400 && vm.HasActiveFilter);
        var sv = Scroller(view.TrackList);
        await ScrollTo(sv, ScrolledTo);

        vm.ApplyFilter("song 1");
        await PumpUntil(() => vm.FilteredTracks.Count < 400 && sv.Offset.Y == 0);

        Assert.Equal(0, sv.Offset.Y);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Artists_LibraryReloadWhileSearching_KeepsTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 700; i++)
            lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = $"Artist {i}", TrackCount = 1, AlbumCount = 1 });
        var vm = new LibraryArtistsViewModel(lib);
        var view = new LibraryArtistsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;

        vm.ApplyFilter("artist");
        await PumpUntil(() => vm.ArtistRows.Sum(r => r.Artists.Count) == 700);
        var sv = Scroller(view.ArtistListBox);
        await ScrollTo(sv, ScrolledTo);

        var resets = 0;
        vm.ArtistRows.CollectionChanged += (_, _) => resets++;
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => resets > 0);

        Assert.Equal(1, resets);
        Assert.Equal(ScrolledTo, sv.Offset.Y, 0);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Albums_LibraryReloadWhileSearching_KeepsTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < 200; i++)
            albums.Add(new Album
            {
                Id = Guid.NewGuid(), Name = $"Album {i}", Artist = "Artist", Year = 2020, Tracks = new List<Track>(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        vm.IsActive = true;
        // Let the activation load land first: a filter applied over it would build from
        // the still-empty album snapshot.
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);

        var filtered = 0;
        vm.FilteredAlbumRows.CollectionChanged += (_, _) => filtered++;
        vm.ApplyFilter("album");
        await PumpUntil(() => filtered > 0);
        // The album grid fills its rows in more than one change: wait until they stop, or a
        // late filter change is counted below as a reload reset.
        for (var seen = -1; seen != filtered;)
        {
            seen = filtered;
            await PumpUntil(() => filtered != seen, budgetMs: 250);
        }
        var sv = Scroller(view.AlbumListBox);
        await ScrollTo(sv, ScrolledTo);

        var resets = 0;
        vm.FilteredAlbumRows.CollectionChanged += (_, _) => resets++;
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => resets > 0);

        Assert.Equal(1, resets);
        Assert.Equal(ScrolledTo, sv.Offset.Y, 0);
        win.Close();
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
