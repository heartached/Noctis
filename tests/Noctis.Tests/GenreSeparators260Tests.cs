using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #260 "Genre separator settings gone?": genres were split on ';' only, so a file
/// tagged "Rock, Pop, Indie" was one made-up combined genre on the Genres page, album headers,
/// mixes and Autoplay. Settings › Library › Genre Separators (default ',' on top of the
/// always-on ';') splits them when the library is read and grouped; the files are never changed.
/// Process-wide configuration: always restores the defaults.
/// </summary>
public class GenreSeparators260Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public GenreSeparators260Tests() => GenreSeparators.ResetToDefaults();

    public void Dispose()
    {
        GenreSeparators.ResetToDefaults();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Album AlbumOf(string name, params string[] genres)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = name, Artist = name };
        var n = 1;
        foreach (var g in genres)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name}{n}", Album = name, AlbumId = album.Id, Genre = g,
                TrackNumber = n++, Duration = TimeSpan.FromMinutes(3),
            });
        return album;
    }

    // ── Defaults ──

    [Fact]
    public void Defaults_AreCommaOnTopOfTheAlwaysOnSemicolon()
    {
        Assert.Equal(new[] { "," }, GenreSeparators.Defaults);
        Assert.Equal(new[] { "," }, new AppSettings().GenreTagSeparators);
        Assert.Equal(new[] { ";", "," }, GenreSeparators.Active);
    }

    [Fact]
    public void DiscogsGenreWithCommas_StaysWhole()
    {
        // Discogs' top-level genre "Folk, World, & Country" is one genre, not three.
        Assert.Equal(new[] { "Folk, World, & Country" }, Track.SplitGenres("Folk, World, & Country"));
        Assert.Equal(new[] { "Rock", "folk, world, & country", "Pop" },
            Track.SplitGenres("Rock,folk, world, & country, Pop"));
        Assert.True(Track.SharesGenre(new[] { "Folk, World, & Country" }, "Rock; Folk, World, & Country"));
        Assert.False(Track.SharesGenre(new[] { "World" }, "Folk, World, & Country"));
        GenreSeparators.Configure(new[] { ",", "&" });
        Assert.Equal(new[] { "Folk, World, & Country", "R", "B" }, Track.SplitGenres("Folk, World, & Country, R&B"));
    }

    [Fact]
    public void CommaSeparatedGenre_SplitsIntoSeparateGenres()
    {
        Assert.Equal(new[] { "Rock", "Pop", "Indie" }, Track.SplitGenres("Rock, Pop,Indie"));
        Assert.Equal(new[] { "Rock", "Pop", "Jazz" }, Track.SplitGenres("Rock, Pop; Jazz ;rock"));
        Assert.Equal("Rock · Pop", Track.DotGenres("Rock, Pop"));
        Assert.Equal("Rock · Pop", new Album { Genre = "Rock, Pop" }.GenreDisplay);
    }

    [Fact]
    public void HipHopRap_StaysWhole()
    {
        Assert.Equal(new[] { "Hip-Hop/Rap" }, Track.SplitGenres("Hip-Hop/Rap"));
        Assert.True(GenresViewModel.TrackHasGenre(new Track { Genre = "Hip-Hop/Rap" }, "Hip-Hop/Rap"));
        Assert.False(GenresViewModel.TrackHasGenre(new Track { Genre = "Hip-Hop/Rap" }, "Rap"));
    }

    // ── Genres page ──

    [Fact]
    public void GenresPage_CommaTaggedSong_CountsUnderEachGenre()
    {
        var albums = new[] { AlbumOf("A", "Rock, Pop, Indie"), AlbumOf("B", "Rock") };

        var rows = GenresViewModel.BuildRows(albums);

        Assert.Equal(new[] { "Indie", "Pop", "Rock" }, rows.Select(r => r.Name));
        Assert.Equal((2, 2), rows.Single(r => r.Name == "Rock").Let(r => (r.SongCount, r.AlbumCount)));
        Assert.Equal(1, rows.Single(r => r.Name == "Pop").SongCount);
    }

    [Fact]
    public void TrackWithRockCommaPop_IsFoundUnderBoth()
    {
        var song = new Track { Genre = "Rock, Pop" };
        Assert.True(GenresViewModel.TrackHasGenre(song, "Rock"));
        Assert.True(GenresViewModel.TrackHasGenre(song, " pop "));
        Assert.False(GenresViewModel.TrackHasGenre(song, "Unknown"));
        Assert.False(GenresViewModel.TrackHasGenre(new Track { Genre = " , ;" }, "Rock"));
        Assert.True(GenresViewModel.TrackHasGenre(new Track { Genre = " , ;" }, "Unknown"));

        var albums = new[] { AlbumOf("A", "Rock, Pop", "Jazz") };
        Assert.Equal(new[] { "A1" }, GenresViewModel.TracksInGenre(albums, "Pop").Select(t => t.Title));
        Assert.Equal(new[] { "A1" }, GenresViewModel.TracksInGenre(albums, "Rock").Select(t => t.Title));
    }

    [Fact]
    public void CustomSeparator_AddedOrRemoved_ChangesGrouping()
    {
        var albums = new[] { AlbumOf("A", "Hip-Hop/Rap, Soul", "Rock / Pop") };
        Assert.Equal(new[] { "Hip-Hop/Rap", "Rock / Pop", "Soul" }, GenresViewModel.BuildRows(albums).Select(r => r.Name));

        var v0 = GenreSeparators.Version;
        GenreSeparators.Configure(new[] { ",", " / " });
        Assert.NotEqual(v0, GenreSeparators.Version);
        Assert.Equal(new[] { ";", ",", "/" }, GenreSeparators.Active);
        Assert.Equal(new[] { "Hip-Hop", "Pop", "Rap", "Rock", "Soul" }, GenresViewModel.BuildRows(albums).Select(r => r.Name));

        // Comma removed: "Hip-Hop/Rap, Soul" no longer splits on it; ';' always does.
        GenreSeparators.Configure(new[] { "/" });
        Assert.Equal(new[] { "Hip-Hop", "Pop", "Rap, Soul", "Rock" }, GenresViewModel.BuildRows(albums).Select(r => r.Name));
        Assert.Equal(new[] { "A", "B" }, Track.SplitGenres("A;B"));

        // Nothing configured: only ';'. A list naming ';' itself does not duplicate it.
        GenreSeparators.Configure(Array.Empty<string>());
        Assert.Equal(new[] { ";" }, GenreSeparators.Active);
        Assert.Equal(new[] { "Rock, Pop" }, Track.SplitGenres("Rock, Pop"));
        GenreSeparators.Configure(new[] { ";", " ; ", "," });
        Assert.Equal(new[] { ";", "," }, GenreSeparators.Active);
    }

    [Fact]
    public void Configure_SameList_DoesNotBumpTheVersion()
    {
        var v0 = GenreSeparators.Version;
        GenreSeparators.Configure(new[] { " , " });
        GenreSeparators.Configure(null);
        Assert.Equal(v0, GenreSeparators.Version);
    }

    [Fact]
    public void WordSeparator_SplitsCaseInsensitively_AndLongestWins()
    {
        GenreSeparators.Configure(new[] { ",", "and", "&" });
        Assert.Equal(new[] { "Drum", "Bass", "R", "B" }, Track.SplitGenres("Drum AND Bass, R&B"));
        Assert.True(Track.SharesGenre(new[] { "bass" }, "Drum and Bass"));
        // A word separator cuts whole words only.
        Assert.Equal(new[] { "Grandcore", "Band Music" }, Track.SplitGenres("Grandcore, Band Music"));
        Assert.Equal(new[] { "Grandcore", "Soul" }, Track.SplitGenres("Grandcore and Soul"));
    }

    // ── SharesGenre (Autoplay, radio, mixes, server) ──

    [Fact]
    public void SharesGenre_HonoursTheSeparators()
    {
        Assert.True(Track.SharesGenre(new[] { "Pop" }, "Rock, Pop"));
        Assert.True(Track.SharesGenre(new[] { "rock" }, "Rock,Pop"));
        Assert.False(Track.SharesGenre(new[] { "Rap" }, "Hip-Hop/Rap"));
        Assert.True(Track.SharesGenre(new[] { "Hip-Hop/Rap" }, "Hip-Hop/Rap, Soul"));

        GenreSeparators.Configure(new[] { "/" });
        Assert.True(Track.SharesGenre(new[] { "Rap" }, "Hip-Hop/Rap"));
        Assert.False(Track.SharesGenre(new[] { "Pop" }, "Rock, Pop"));
        Assert.True(Track.SharesGenre(new[] { "Rock, Pop" }, "Rock, Pop"));
    }

    [Fact]
    public void Autoplay_CommaTaggedSeed_MatchesEachGenre()
    {
        var seed = new Track { Title = "seed", Artist = "S", Genre = "Rock, Pop" };
        var pop = new Track { Title = "p", Artist = "Y", Genre = "Pop" };
        var jazz = new Track { Title = "j", Artist = "Z", Genre = "Jazz" };

        var picks = new AutoplayService().PickSimilar(seed, new[] { seed, pop, jazz }, 10, new HashSet<Guid>());

        Assert.Equal(new[] { "p" }, picks.Select(t => t.Title));
    }

    [Fact]
    public void ServerGenreLookup_FindsACommaTaggedSong()
    {
        Assert.True(Noctis.Services.Server.LibraryView.HasGenre("Rock, Pop", "Pop"));
        Assert.True(Noctis.Services.Server.LibraryView.HasGenre("Rock, Pop", "Rock, Pop")); // old combined name
    }

    // ── Stored form: never rewritten by the display split ──

    [Fact]
    public void StoredForm_IsSplitOnSemicolonsOnly()
    {
        Assert.Equal("Rock, Pop", Track.NormalizeGenre("Rock, Pop"));
        Assert.Equal("Rock, Pop; Jazz", Track.NormalizeGenre(" Rock, Pop ;Jazz;"));
        Assert.Equal("Rock, Pop", Track.JoinGenres(new[] { "Rock, Pop" }));
        Assert.Equal(new[] { "Rock, Pop", "Jazz" }, Track.SplitStoredGenres("Rock, Pop; Jazz"));
    }

    // ── Settings › Library ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings(FakeLibraryService? library = null) => new(
        new PersistenceService(_root), library ?? new FakeLibraryService(), new NoOpPlayHistoryService());

    [AvaloniaFact]
    public async Task OldSettingsFile_WithoutTheList_LoadsTheDefault()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), """{ "artistGroupMode": "Artist" }""");

        var vm = CreateSettings();
        await vm.LoadAsync();

        Assert.Equal(new[] { "," }, vm.GenreTagSeparators);
        Assert.Equal(new[] { ";", "," }, GenreSeparators.Active);
    }

    [AvaloniaFact]
    public async Task AddRemoveReset_RegroupAtOnce_AndPersist()
    {
        var library = new FakeLibraryService();
        var vm = CreateSettings(library);
        await vm.LoadAsync();
        var regroups = library.MetadataChangedCount;

        vm.NewGenreSeparator = "  /  ";
        vm.AddGenreSeparatorCommand.Execute(null);
        Assert.Equal(string.Empty, vm.NewGenreSeparator);
        Assert.Equal(new[] { ",", "/" }, vm.GenreTagSeparators);
        Assert.Equal(new[] { "Hip-Hop", "Rap" }, Track.SplitGenres("Hip-Hop/Rap"));
        Assert.Equal(regroups + 1, library.MetadataChangedCount);

        // Blank, duplicate and the built-in ';' are ignored (no regroup).
        foreach (var ignored in new[] { "  ", "/", ";", " ; " })
        {
            vm.NewGenreSeparator = ignored;
            vm.AddGenreSeparatorCommand.Execute(null);
        }
        Assert.Equal(new[] { ",", "/" }, vm.GenreTagSeparators);
        Assert.Equal(regroups + 1, library.MetadataChangedCount);

        vm.RemoveGenreSeparatorCommand.Execute(",");
        Assert.Equal(new[] { "Rock, Pop" }, Track.SplitGenres("Rock, Pop"));
        Assert.Equal(regroups + 2, library.MetadataChangedCount);
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.Equal(new[] { "/" }, reloaded.GenreTagSeparators);

        // An emptied list stays empty (only ';' splits) across a restart.
        reloaded.RemoveGenreSeparatorCommand.Execute("/");
        Assert.Empty(reloaded.GenreTagSeparators);
        await reloaded.SaveAsync();
        var empty = CreateSettings();
        await empty.LoadAsync();
        Assert.Empty(empty.GenreTagSeparators);
        Assert.Equal(new[] { ";" }, GenreSeparators.Active);

        empty.ResetGenreSeparatorsCommand.Execute(null);
        Assert.Equal(new[] { "," }, empty.GenreTagSeparators);
        Assert.Equal(new[] { ";", "," }, GenreSeparators.Active);
    }

    [AvaloniaFact]
    public async Task SeparatorChange_RefreshesTheSongRowsGenreText()
    {
        var library = new FakeLibraryService();
        var song = new Track { Id = Guid.NewGuid(), Title = "S", Genre = "Rock/Pop" };
        library.TrackList.Add(song);
        var vm = CreateSettings(library);
        await vm.LoadAsync();
        var changed = new List<string?>();
        song.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NewGenreSeparator = "/";
        vm.AddGenreSeparatorCommand.Execute(null);

        Assert.Contains(nameof(Track.GenreDisplay), changed);
        Assert.Equal("Rock · Pop", song.GenreDisplay);
    }
}

internal static class GenreTestExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
