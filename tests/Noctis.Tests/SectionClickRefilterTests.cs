using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "When I click Songs/Albums/Artists on the sidebar they flicker" (09-27). Every section click
/// re-applied an empty search to Songs/Albums/Artists (MainWindowViewModel.Navigate), which
/// rebuilt the rows off-thread and Reset the list just after the page appeared, re-creating
/// every row and cover. On Albums it also superseded Refresh's pending library reload, so a
/// scan's new album stayed missing until the next visit. These replay Navigate's calls.
/// </summary>
public class SectionClickRefilterTests
{
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

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Album NewAlbum(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, Artist = "Artist", Year = 2020, Tracks = new List<Track>() };

    private static (LibraryAlbumsViewModel Vm, FakeLibraryService Lib) Albums(int count)
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < count; i++) ((List<Album>)lib.Albums).Add(NewAlbum($"Album {i}"));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        return (vm, lib);
    }

    /// <summary>MainWindowViewModel.Navigate("albums"): ResetFilterAndReturnAlbums, the
    /// CurrentView swap (IsActive), then the search clear.</summary>
    private static void ClickAlbums(LibraryAlbumsViewModel vm)
    {
        vm.ClearArtistFilter();
        vm.Refresh();
        vm.IsActive = true;
        MainWindowViewModel.ClearStaleSearch(vm);
    }

    private static Func<int> CountChanges(INotifyCollectionChanged collection)
    {
        var n = 0;
        collection.CollectionChanged += (_, _) => n++;
        return () => n;
    }

    [AvaloniaFact]
    public async Task ClickingSongs_WithNothingChanged_LeavesTheRowsAlone()
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < 50; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist",
                FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"), Duration = TimeSpan.FromMinutes(3),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        vm.IsActive = true;
        await PumpFor(300);
        Assert.Equal(50, vm.FilteredTracks.Count);
        vm.IsActive = false;

        var resets = CountChanges(vm.FilteredTracks);
        vm.Refresh();
        vm.IsActive = true;
        MainWindowViewModel.ClearStaleSearch(vm);
        await PumpFor(300);

        Assert.Equal(0, resets());
        Assert.Equal(50, vm.FilteredTracks.Count);
    }

    [AvaloniaFact]
    public async Task ClickingAlbums_WithNothingChanged_LeavesTheRowsAlone()
    {
        var (vm, _) = Albums(30);
        vm.Refresh();
        await PumpFor(300);
        Assert.NotEmpty(vm.FilteredAlbumRows);
        vm.IsActive = false;

        var resets = CountChanges(vm.FilteredAlbumRows);
        ClickAlbums(vm);
        await PumpFor(300);

        Assert.Equal(0, resets());
    }

    [AvaloniaFact]
    public async Task ClickingArtists_WithNothingChanged_LeavesTheRowsAlone()
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < 40; i++)
            lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = $"Artist {i}", TrackCount = 1, AlbumCount = 1 });
        var vm = new LibraryArtistsViewModel(lib);
        vm.IsActive = true;
        vm.Refresh();
        await PumpFor(300);
        Assert.NotEmpty(vm.ArtistRows);
        vm.IsActive = false;

        var resets = CountChanges(vm.ArtistRows);
        // ResetAndReturnArtists, then the CurrentView swap and the search clear.
        vm.SearchText = string.Empty;
        vm.IsSearchVisible = false;
        vm.Refresh();
        vm.IsActive = true;
        MainWindowViewModel.ClearStaleSearch(vm);
        await PumpFor(300);

        Assert.Equal(0, resets());
    }

    [AvaloniaFact]
    public async Task ClickingAlbums_AfterTheLibraryChanged_ShowsTheNewAlbum()
    {
        var (vm, lib) = Albums(30);
        vm.Refresh();
        await PumpFor(300);
        vm.IsActive = false;

        // A scan adds an album while another section is showing (hidden = dirty only).
        ((List<Album>)lib.Albums).Add(NewAlbum("Brand New"));
        lib.RaiseLibraryUpdated();
        await PumpFor(50);

        ClickAlbums(vm);
        await PumpFor(400);

        var shown = vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums).Select(a => a.Name).ToList();
        Assert.Contains("Brand New", shown);
        Assert.Equal(31, shown.Count);
    }

    [AvaloniaFact]
    public async Task ClickingSongs_StillClearsAFilterLeftFromAnEarlierVisit()
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < 20; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = i < 5 ? $"Match {i}" : $"Song {i}", Artist = "Artist",
                FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"), Duration = TimeSpan.FromMinutes(3),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        vm.IsActive = true;
        vm.ApplyFilter("match");
        await PumpFor(300);
        Assert.Equal(5, vm.FilteredTracks.Count);
        Assert.False(vm.IsSearchCleared);
        vm.IsActive = false;

        vm.Refresh();
        vm.IsActive = true;
        MainWindowViewModel.ClearStaleSearch(vm);
        await PumpFor(300);

        Assert.True(vm.IsSearchCleared);
        Assert.Equal(20, vm.FilteredTracks.Count);
    }
}
