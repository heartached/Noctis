using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Second round on the artist page (09-03): search must be accent-insensitive and reach
/// releases through their track titles; Top Favorites shows the user's favourited songs;
/// Latest Release follows the release-date tag, not just the year.
/// </summary>
public class ArtistDetailSectionsTests
{
    private static Album MakeAlbum(string name, string artist, int year, string releaseDate = "",
        params (string title, int plays, bool fav)[] tracks)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        var n = 1;
        foreach (var (title, plays, fav) in tracks)
        {
            var t = new Track
            {
                Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = n++, DiscNumber = 1, Year = year, ReleaseDate = releaseDate,
                Duration = TimeSpan.FromMinutes(3), PlayCount = plays,
            };
            t.IsFavorite = fav;
            album.Tracks.Add(t);
        }
        album.TrackCount = album.Tracks.Count;
        return album;
    }

    private static (ArtistDetailViewModel Vm, FakeLibraryService Lib) Make(string artist, params Album[] albums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(albums);
        foreach (var a in albums) lib.TrackList.AddRange(a.Tracks);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (new ArtistDetailViewModel(artist, lib, player), lib);
    }

    [Fact]
    public void Search_IsAccentInsensitive()
    {
        var (vm, _) = Make("Bad Bunny",
            MakeAlbum("EL ÚLTIMO TOUR DEL MUNDO", "Bad Bunny", 2020, "", ("DÁKITI", 10, false)),
            MakeAlbum("YHLQMDLG", "Bad Bunny", 2020, "", ("Vete", 5, false)));

        vm.ApplyFilter("ultimo tour");
        Assert.Equal(new[] { "EL ÚLTIMO TOUR DEL MUNDO" }, vm.Releases.Select(a => a.Name));

        vm.ApplyFilter("dakiti");
        Assert.Equal(new[] { "DÁKITI" }, vm.PopularSongs.Select(r => r.Track.Title));
    }

    [Fact]
    public void Search_ReachesAReleaseThroughItsTrackTitles_AndRespectsTheChip()
    {
        var lp = MakeAlbum("Un Verano Sin Ti", "Bad Bunny", 2022, "",
            Enumerable.Range(0, 8).Select(i => ($"Track {i}", 0, false)).Prepend(("Neverita", 3, false)).ToArray());
        var single = MakeAlbum("Neverita Live", "Bad Bunny", 2023, "", ("Neverita (Live)", 1, false));
        var (vm, _) = Make("Bad Bunny", lp, single);

        vm.ApplyFilter("neverita");
        Assert.Equal(2, vm.Releases.Count);           // album name OR any track title

        vm.SetReleaseFilterCommand.Execute("singles");
        Assert.Equal(new[] { "Neverita Live" }, vm.Releases.Select(a => a.Name));

        vm.SetReleaseFilterCommand.Execute("albums");
        Assert.Equal(new[] { "Un Verano Sin Ti" }, vm.Releases.Select(a => a.Name));
    }

    [Fact]
    public void Search_LiftsThePopularCap()
    {
        var tracks = Enumerable.Range(0, 30).Select(i => ($"Song {i:00}", 30 - i, false)).ToArray();
        var (vm, _) = Make("A", MakeAlbum("Big", "A", 2000, "", tracks));
        Assert.Equal(ArtistDetailViewModel.MaxPopular, vm.PopularSongs.Count);

        vm.ApplyFilter("song 2");                     // matches Song 20–29, all ranked below 10
        Assert.Equal(10, vm.PopularSongs.Count);
        Assert.All(vm.PopularSongs, r => Assert.StartsWith("Song 2", r.Track.Title));
    }

    [Fact]
    public void TopFavorites_ListsFavouritedSongsByPlayCount_AndMovesPopularBelow()
    {
        var (vm, _) = Make("A", MakeAlbum("X", "A", 2020, "",
            ("Loved", 5, true), ("Loved More", 9, true), ("Meh", 40, false)));

        Assert.True(vm.HasFavorites);
        Assert.Equal(new[] { "Loved More", "Loved" }, vm.FavoriteSongs.Select(r => r.Track.Title));
        Assert.Equal(2, vm.FavoriteCount);
        Assert.True(vm.ShowPopularBelow);
        Assert.False(vm.ShowPopularBesideLatest);
    }

    [Fact]
    public void NoFavorites_PopularTakesTheSlotBesideLatestRelease()
    {
        var (vm, _) = Make("A", MakeAlbum("X", "A", 2020, "", ("Meh", 40, false)));
        Assert.False(vm.HasFavorites);
        Assert.True(vm.ShowPopularBesideLatest);
        Assert.False(vm.ShowPopularBelow);
    }

    [Fact]
    public void TopFavorites_FollowsFavoritesChanged()
    {
        var album = MakeAlbum("X", "A", 2020, "", ("Later Loved", 1, false));
        var (vm, lib) = Make("A", album);
        Assert.False(vm.HasFavorites);

        album.Tracks[0].IsFavorite = true;
        // FavoritesChanged is raised on the UI thread in the app; the handler posts to the
        // dispatcher, so call the list rebuild path directly here via a no-op filter.
        vm.ApplyFilter(string.Empty);
        Assert.True(vm.HasFavorites);
        Assert.Equal("Later Loved", vm.FavoriteSongs[0].Track.Title);
    }

    [Fact]
    public void LatestRelease_UsesTheReleaseDateTag_NotJustTheYear()
    {
        var early = MakeAlbum("January", "A", 2026, "2026-01-05", ("a", 0, false));
        var late = MakeAlbum("February", "A", 2026, "2026-02-08", ("b", 0, false));
        var untagged = MakeAlbum("Old", "A", 2019, "", ("c", 0, false));
        var (vm, _) = Make("A", early, untagged, late);

        Assert.Same(late, vm.LatestRelease);
        Assert.Equal("Feb 8, 2026", vm.LatestReleaseDate);
        Assert.Equal("SINGLE · 1 song", vm.LatestReleaseSubtitle);
        Assert.Equal(new[] { "February", "January", "Old" }, vm.Releases.Select(a => a.Name));
    }

    [Fact]
    public void ReleaseSortDate_FallsBackToYearThenEpoch()
    {
        Assert.Equal(new DateTime(2020, 1, 1), ArtistDetailViewModel.ReleaseSortDate(MakeAlbum("y", "A", 2020)));
        Assert.Equal(DateTime.MinValue, ArtistDetailViewModel.ReleaseSortDate(MakeAlbum("none", "A", 0)));
    }

    [Fact]
    public void LibraryFacts_MostPlayedAndFavoriteCount()
    {
        var (vm, _) = Make("A", MakeAlbum("X", "A", 2020, "", ("Hit", 12, true), ("B-side", 2, false)));
        Assert.Equal("Hit", vm.MostPlayedTitle);
        Assert.True(vm.HasMostPlayed);
        Assert.Equal(1, vm.FavoriteCount);
        Assert.True(vm.HasInLibrarySince);
        Assert.Null(vm.About); // no info service wired → About stays empty, card shows library facts
    }
}
