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
/// GitHub #223 on the other pages the issue asks about: an album page and the Songs list,
/// scrolled down, then the library republishes the same songs as new objects (what a re-read
/// file does: Find Lyrics saving into the files, the folder watcher importing them again).
/// The artist page's case is in ArtistDetailViewMountTests.
/// </summary>
public class RebuildKeepsScroll223Tests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static void Pump(int n = 20)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static List<Track> Songs(Guid albumId, int count) => Enumerable.Range(1, count).Select(i => new Track
    {
        Id = Guid.NewGuid(), Title = $"Song {i:000}", Artist = "Kinnship", AlbumArtist = "Kinnship", Album = "Restless",
        AlbumId = albumId, TrackNumber = i, DiscNumber = 1, Duration = TimeSpan.FromSeconds(200),
        FilePath = TestPaths.Primary("m", $"Song {i:000}.flac"),
    }).ToList();

    /// <summary>The same songs as new objects, as an import of the re-read files hands them out.</summary>
    private static List<Track> Reread(IEnumerable<Track> tracks) => tracks.Select(t => new Track
    {
        Id = t.Id, Title = t.Title, Artist = t.Artist, AlbumArtist = t.AlbumArtist, Album = t.Album, AlbumId = t.AlbumId,
        TrackNumber = t.TrackNumber, DiscNumber = t.DiscNumber, Duration = t.Duration, FilePath = t.FilePath,
        Lyrics = "found",
    }).ToList();

    [AvaloniaFact]
    public void AlbumPage_ReReadSongs_KeepTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = "Restless", Artist = "Kinnship", Tracks = Songs(id, 80), TrackCount = 80 };
        ((List<Album>)lib.Albums).Add(album);
        lib.TrackList.AddRange(album.Tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new NoLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Pump();

        var sv = view.FindControl<ScrollViewer>("TrackScrollViewer")!;
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 1500, $"page only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom - 100);
        Pump();
        var before = sv.Offset.Y;

        var rebuilt = new Album { Id = id, Name = album.Name, Artist = album.Artist, Tracks = Reread(album.Tracks), TrackCount = 80 };
        ((List<Album>)lib.Albums)[0] = rebuilt;
        lib.TrackList.Clear();
        lib.TrackList.AddRange(rebuilt.Tracks);
        lib.RaiseLibraryUpdated();
        Pump();

        Assert.Equal(before, sv.Offset.Y, 1);
        Assert.Same(rebuilt.Tracks[0], vm.Tracks[0]);
        Assert.Same(rebuilt.Tracks[79], vm.DiscGroups.Single().Tracks[79]);
        win.Close();
    }

    [AvaloniaFact]
    public async Task SongsList_ReReadSongs_KeepTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var id = Guid.NewGuid();
        var songs = Songs(id, 400);
        lib.TrackList.AddRange(songs);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 400);

        var sv = view.TrackList.GetVisualDescendants().OfType<ScrollViewer>().First();
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 3000, $"list only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom / 2);
        Pump();
        var before = sv.Offset.Y;

        var reread = Reread(songs);
        lib.TrackList.Clear();
        lib.TrackList.AddRange(reread);
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => vm.FilteredTracks.Any(t => ReferenceEquals(t, reread[0])));
        Assert.Contains(vm.FilteredTracks, t => ReferenceEquals(t, reread[0]));

        Assert.Equal(before, sv.Offset.Y, 1);
        win.Close();
    }

    [AvaloniaFact]
    public async Task AlbumsGrid_ReBuiltAlbums_KeepTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < 200; i++)
            albums.Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:000}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 10);

        var sv = view.AlbumListBox.FindDescendantOfType<ScrollViewer>()!;
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 3000, $"grid only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom / 2);
        Pump();
        var before = sv.Offset.Y;

        var rows = vm.FilteredAlbumRows.ToList();
        for (var i = 0; i < albums.Count; i++)
        {
            var a = albums[i];
            albums[i] = new Album { Id = a.Id, Name = a.Name, Artist = a.Artist, Year = a.Year, Tracks = a.Tracks };
        }
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => !vm.FilteredAlbumRows.SequenceEqual(rows));
        Assert.False(vm.FilteredAlbumRows.SequenceEqual(rows));

        Assert.Equal(before, sv.Offset.Y, 1);
        win.Close();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump();
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    internal sealed class NoLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }
}
