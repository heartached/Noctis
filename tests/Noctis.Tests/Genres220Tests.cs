using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #220 "View and sort songs by genre": a Genres page (genres with album/song counts),
/// a genre's albums and songs on the Albums grid, and clickable genre labels on album and
/// artist pages. Multi-genre tags count under each genre (';' as the scanner stores them),
/// spellings merge case-insensitively and untagged songs gather under Unknown.
/// </summary>
public class Genres220Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-genres220-{Guid.NewGuid():N}");

    public Genres220Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Album NewAlbum(string name, string artist, int year = 2020, string? art = null)
        => new() { Id = Guid.NewGuid(), Name = name, Artist = artist, Year = year, ArtworkPath = art };

    private Track Song(Album album, string title, string genre, int track = 1, int plays = 0)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        var t = new Track
        {
            Id = Guid.NewGuid(), Title = title, Artist = album.Artist, AlbumArtist = album.Artist,
            Album = album.Name, AlbumId = album.Id, FilePath = path, Genre = genre,
            TrackNumber = track, Duration = TimeSpan.FromMinutes(3), PlayCount = plays,
        };
        album.Tracks.Add(t);
        return t;
    }

    /// <summary>Rock (two spellings), a Rock; Pop multi-genre song, an untagged one and one
    /// tagged "unknown" across three albums.</summary>
    private (FakeLibraryService lib, Album rockAlbum, Album mixed, Album bare) Library()
    {
        var lib = new FakeLibraryService();
        var rockAlbum = NewAlbum("Rocks", "Band A", 2001, art: "rocks.jpg");
        Song(rockAlbum, "R1", "Rock", 1, plays: 5);
        Song(rockAlbum, "R2", "rock", 2, plays: 1);
        var mixed = NewAlbum("Mixed", "Band B", 1999);
        Song(mixed, "M1", "Rock; Pop", 1, plays: 9);
        Song(mixed, "M2", "Jazz", 2);
        var bare = NewAlbum("Bare", "Band C", 2010);
        Song(bare, "B1", "", 1);
        Song(bare, "B2", "unknown", 2);
        foreach (var a in new[] { rockAlbum, mixed, bare })
        {
            ((List<Album>)lib.Albums).Add(a);
            lib.TrackList.AddRange(a.Tracks);
        }
        return (lib, rockAlbum, mixed, bare);
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ── Genres page rows ──

    [Fact]
    public void BuildRows_SplitsMultiGenreTags_MergesCase_AndPutsUnknownLast()
    {
        var (lib, _, _, _) = Library();
        var rows = GenresViewModel.BuildRows(lib.Albums);

        Assert.Equal(new[] { "Jazz", "Pop", "Rock", GenreRow.UnknownKey }, rows.Select(r => r.Name));
        var rock = rows.Single(r => r.Name == "Rock");
        Assert.Equal(3, rock.SongCount);   // R1, R2 ("rock"), M1 ("Rock; Pop")
        Assert.Equal(2, rock.AlbumCount);  // Rocks + Mixed
        Assert.Equal(TimeSpan.FromMinutes(9), rock.TotalDuration);
        Assert.Equal("rocks.jpg", rock.ArtworkPath);
        var pop = rows.Single(r => r.Name == "Pop");
        Assert.Equal((1, 1), (pop.SongCount, pop.AlbumCount));
        // An untagged song and one tagged "unknown" share the Unknown row.
        var unknown = rows.Single(r => r.IsUnknown);
        Assert.Equal((2, 1), (unknown.SongCount, unknown.AlbumCount));
    }

    [Theory]
    [InlineData("Rock; Pop", "rock", true)]
    [InlineData("Rock; Pop", "POP", true)]
    [InlineData("Hip-Hop/Rap", "Hip-Hop", false)] // '/' is part of the name, not a separator
    [InlineData("Indie Rock", "Rock", false)]     // whole genres only, unlike genre: search
    [InlineData("", "Unknown", true)]
    [InlineData("unknown", "Unknown", true)]
    [InlineData("Rock", "Unknown", false)]
    public void TrackHasGenre_MatchesWholeGenresCaseInsensitively(string tag, string genre, bool expected)
        => Assert.Equal(expected, GenresViewModel.TrackHasGenre(new Track { Genre = tag }, genre));

    [Fact]
    public void TracksInGenre_KeepsOnlyTheGenresSongs_ByAlbumArtistThenTrack()
    {
        var (lib, _, _, _) = Library();
        var titles = GenresViewModel.TracksInGenre(lib.Albums, "ROCK").Select(t => t.Title);
        Assert.Equal(new[] { "R1", "R2", "M1" }, titles); // Band A before Band B; M2 (Jazz) left out
    }

    [Fact]
    public void FilterRows_MatchesTheShownName()
    {
        var (lib, _, _, _) = Library();
        var rows = GenresViewModel.BuildRows(lib.Albums);
        Assert.Equal(new[] { "Pop" }, GenresViewModel.FilterRows(rows, "po").Select(r => r.Name));
        Assert.Equal(rows.Count, GenresViewModel.FilterRows(rows, " ").Count);
    }

    [AvaloniaFact]
    public async Task GenresPage_BuildsOffThreadWhenShown_AndRebuildsOnLibraryChange()
    {
        var (lib, _, _, bare) = Library();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        using var vm = new GenresViewModel(lib, player);
        Assert.False(vm.IsEmpty); // not loaded yet: no empty-state flash

        vm.IsActive = true;
        await PumpUntil(() => vm.Rows.Count == 4);
        Assert.Equal(4, vm.Rows.Count);

        Song(bare, "B3", "Ambient", 3);
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => vm.Rows.Count == 5);
        Assert.Contains(vm.Rows, r => r.Name == "Ambient");

        vm.ApplyFilter("amb");
        Assert.Equal(new[] { "Ambient" }, vm.Rows.Select(r => r.Name));

        string? opened = null;
        vm.GenreOpened += (_, g) => opened = g;
        vm.OpenGenreCommand.Execute(vm.Rows[0]);
        Assert.Equal("Ambient", opened);

        vm.PlayGenreCommand.Execute(vm.Rows[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("B3", player.CurrentTrack?.Title);
    }

    // ── A genre on the Albums grid ──

    private static LibraryAlbumsViewModel AlbumsVm(FakeLibraryService lib, out PlayerViewModel player)
    {
        var persistence = new TestPersistenceService();
        player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new FakeHistoryLog()));
    }

    [AvaloniaFact]
    public async Task SetGenreFilter_ShowsTheGenresSongsAndAlbums_AndPlaysOnlyItsSongs()
    {
        var (lib, rockAlbum, mixed, bare) = Library();
        var vm = AlbumsVm(lib, out var player);
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);

        vm.SetGenreFilter("rock");
        Assert.True(vm.IsGenreFiltered);
        Assert.True(vm.IsPageFiltered);
        Assert.False(vm.IsArtistFiltered);
        Assert.Equal("rock", vm.HeaderText);

        var albums = vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums).ToList();
        Assert.Equal(new[] { rockAlbum, mixed }, albums); // by album artist: Band A, Band B
        Assert.DoesNotContain(bare, albums);
        var songs = vm.FilteredAlbumRows.OfType<ArtistSongsRow>().SelectMany(r => r.Songs).Select(s => s.Track.Title);
        Assert.Equal(new[] { "M1", "R1", "R2" }, songs); // most played first

        vm.PlayAllArtistTracksCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("M1", player.CurrentTrack?.Title);
        var queued = new[] { player.CurrentTrack! }.Concat(player.UpNext).Select(t => t.Title).Distinct().ToList();
        Assert.DoesNotContain("M2", queued); // the Jazz song on the mixed album stays out
        Assert.Equal(3, queued.Count);

        vm.ClearArtistFilter();
        Assert.False(vm.IsPageFiltered);
        Assert.Equal(string.Empty, vm.GenreFilterName);
    }

    [AvaloniaFact]
    public async Task SetGenreFilter_Unknown_ShowsUntaggedSongs_WithALocalizedTitle()
    {
        var (lib, _, _, bare) = Library();
        var vm = AlbumsVm(lib, out _);
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);

        vm.SetGenreFilter(GenreRow.UnknownKey);
        Assert.Equal(Noctis.Localization.Loc.T("Genres.Unknown"), vm.HeaderText);
        Assert.Equal(new[] { bare }, vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums));
    }

    [AvaloniaFact]
    public async Task ArtistAndGenreFilters_ReplaceEachOther()
    {
        var (lib, _, _, _) = Library();
        var vm = AlbumsVm(lib, out _);
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);

        vm.SetGenreFilter("Jazz");
        vm.SetArtistFilter("Band A");
        Assert.False(vm.IsGenreFiltered);
        Assert.Equal("Band A", vm.HeaderText);
        vm.SetGenreFilter("Jazz");
        Assert.False(vm.IsArtistFiltered);
    }



    // ── Sidebar ──

    [Fact]
    public void Sidebar_HasGenresAfterArtists_AndAnOlderSavedOrderGainsIt()
    {
        var order = SidebarViewModel.DefaultPageOrder.ToList();
        Assert.Equal(order.IndexOf("artists") + 1, order.IndexOf("genres"));
        Assert.Equal("TagIcon", SidebarViewModel.PageIcon("genres"));
        Assert.Equal("Nav.Genres", SidebarViewModel.LabelKey("genres"));
        Assert.True(SidebarViewModel.CanHidePage("genres"));

        // A layout saved before #220 (no "genres"): it lands right after Artists.
        var saved = new[] { "albums", "home", "songs", "artists", "folders", "playlists", "settings" };
        var merged = SidebarViewModel.MergePageOrder(saved);
        Assert.Equal(merged.IndexOf("artists") + 1, merged.IndexOf("genres"));
    }

    // ── Mounted views (the XAML resolves its resources and bindings at runtime) ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    [AvaloniaFact]
    public async Task GenresView_RealizesRows_AndEnterOpensTheSelectedGenre()
    {
        EnsureAppStyles();
        var (lib, _, _, _) = Library();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        using var vm = new GenresViewModel(lib, player);
        var view = new GenresView { DataContext = vm };
        var win = new Window { Width = 1100, Height = 700, Content = view };
        win.Show();
        try
        {
            vm.IsActive = true;
            await PumpUntil(() => view.GetVisualDescendants().OfType<ListBoxItem>().Count() == 4);
            Assert.Equal(4, view.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "3"); // Rock's songs
            Assert.Equal(8, view.GetVisualDescendants().OfType<Button>().Count()); // Play + Shuffle per row

            string? opened = null;
            vm.GenreOpened += (_, g) => opened = g;
            var list = view.FindControl<ListBox>("GenreList")!;
            list.SelectedIndex = 2;
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("Rock", opened);
        }
        finally { win.Close(); }
    }


}
