using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #100: the artist page's Albums and Singles &amp; EPs tabs take a Sort pick —
/// Newest first (the order the page always had), Oldest first (undated releases last) or
/// Name (A–Z). One setting for both tabs and every artist; the Overview rows, Latest
/// Release and Appears On stay newest-first.
/// </summary>
public class ArtistDetailReleaseSortTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Seven tracks make an album, one a single (the track-count fallback).</summary>
    private static Album Release(string name, int year, int tracks = 7, string releaseDate = "", string artist = "A")
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        for (var n = 1; n <= tracks; n++)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {n}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = n, DiscNumber = 1, Year = year, ReleaseDate = releaseDate,
                Duration = TimeSpan.FromMinutes(3),
            });
        }
        album.TrackCount = album.Tracks.Count;
        return album;
    }

    private static Album Single(string name, int year, string releaseDate = "") => Release(name, year, 1, releaseDate);

    private static FakeLibraryService Library(params Album[] albums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(albums);
        foreach (var a in albums) lib.TrackList.AddRange(a.Tracks);
        return lib;
    }

    private static PlayerViewModel Player(FakeLibraryService lib)
        => new(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());

    private static ArtistDetailViewModel Make(string artist, params Album[] albums)
    {
        var lib = Library(albums);
        return new ArtistDetailViewModel(artist, lib, Player(lib));
    }

    private static string[] Names(IEnumerable<Album> albums) => albums.Select(a => a.Name).ToArray();

    [Fact]
    public void AlbumsTab_FollowsTheSortPick_OverviewAndLatestStayNewestFirst()
    {
        var bravo = Release("Bravo", 2019);
        var alpha = Release("alpha", 2021, releaseDate: "2021-03-01");  // the tag date beats Charlie's bare year
        var charlie = Release("Charlie", 2021);
        var delta = Release("Delta", 0);                                 // untagged: no date, no year
        var vm = Make("A", delta, bravo, charlie, alpha);

        Assert.Equal("newest", vm.ReleaseSortMode);
        vm.SelectTabCommand.Execute("albums");
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo", "Delta" }, Names(vm.AlbumReleases)); // the pre-#100 order

        vm.SetReleaseSortCommand.Execute("oldest");
        Assert.Equal(new[] { "Bravo", "Charlie", "alpha", "Delta" }, Names(vm.AlbumReleases)); // undated last, not first
        Assert.Equal(Loc.T("ArtistDetail.SortOldest"), vm.ReleaseSortLabel);

        vm.SetReleaseSortCommand.Execute("name");
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie", "Delta" }, Names(vm.AlbumReleases)); // case-insensitive A–Z
        Assert.Equal(Loc.T("ArtistDetail.SortName"), vm.ReleaseSortLabel);
        Assert.Equal(4, vm.AlbumCount);

        // Only the tab grids follow the pick.
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo", "Delta" }, Names(vm.OverviewAlbums));
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo", "Delta" }, Names(vm.Releases));
        Assert.Same(alpha, vm.LatestRelease);

        vm.SetReleaseSortCommand.Execute("newest");
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo", "Delta" }, Names(vm.AlbumReleases));
        Assert.Equal(Loc.T("ArtistDetail.SortNewest"), vm.ReleaseSortLabel);

        vm.SetReleaseSortCommand.Execute("garbage");                     // unknown pick → the default
        Assert.Equal("newest", vm.ReleaseSortMode);
    }

    [Fact]
    public void SinglesTab_FollowsTheSameSort_AndAHiddenTabFillsInItOnOpen()
    {
        var lp = Release("LP", 2020);
        var early = Single("early", 2018);
        var late = Single("Late", 2022, "2022-06-01");
        var undated = Single("Also Undated", 0);
        var vm = Make("A", early, undated, lp, late);

        vm.SelectTabCommand.Execute("singles");
        Assert.Equal(new[] { "Late", "early", "Also Undated" }, Names(vm.SingleReleases));

        vm.SetReleaseSortCommand.Execute("oldest");
        Assert.Equal(new[] { "early", "Late", "Also Undated" }, Names(vm.SingleReleases));
        vm.SetReleaseSortCommand.Execute("name");
        Assert.Equal(new[] { "Also Undated", "early", "Late" }, Names(vm.SingleReleases));
        Assert.Equal(new[] { "Late", "early", "Also Undated" }, Names(vm.OverviewSingles)); // Overview row unchanged

        // Picked on Albums: the hidden Singles grid stays empty (filled only while open)
        // and opens in the new order.
        vm.SelectTabCommand.Execute("albums");
        vm.SetReleaseSortCommand.Execute("oldest");
        Assert.Empty(vm.SingleReleases);
        vm.SelectTabCommand.Execute("singles");
        Assert.Equal(new[] { "early", "Late", "Also Undated" }, Names(vm.SingleReleases));
    }

    [Fact]
    public void SortReleases_TieBreaks_AndNewestKeepsTheClassifyOrder()
    {
        var hitsOld = Release("Greatest Hits", 2010);
        var hitsNew = Release("greatest hits", 2015);   // same name ignoring case
        var zulu = Release("Zulu", 2018);
        var yankee = Release("Yankee", 2018);           // same year as Zulu
        var undatedB = Release("Undated B", 0);
        var undatedA = Release("undated a", 0);
        var releases = ArtistDetailViewModel.Classify(new[] { zulu, undatedB, hitsOld, yankee, undatedA, hitsNew }, "A").Releases;

        // Default = the page's order before #100: date descending, ties A–Z, undated last.
        Assert.Equal(new[] { "Yankee", "Zulu", "greatest hits", "Greatest Hits", "undated a", "Undated B" }, Names(releases));
        Assert.Equal(releases, ArtistDetailViewModel.SortReleases(releases, "newest"));
        Assert.Equal(releases, ArtistDetailViewModel.SortReleases(releases, null));

        // Oldest: date ascending, same-date ties by name, undated last (by name).
        Assert.Equal(new[] { "Greatest Hits", "greatest hits", "Yankee", "Zulu", "undated a", "Undated B" },
            Names(ArtistDetailViewModel.SortReleases(releases, "oldest")));

        // Name: A–Z ignoring case; the same name breaks newest first.
        Assert.Equal(new[] { hitsNew, hitsOld, undatedA, undatedB, yankee, zulu },
            ArtistDetailViewModel.SortReleases(releases, "name"));
    }

    [AvaloniaFact]
    public void SortPick_IsOneSettingForEveryArtistPage()
    {
        var settings = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        Assert.Equal("newest", settings.ArtistReleaseSortMode);
        settings.ArtistReleaseSortMode = "oldest";

        var lib = Library(Release("Old", 2001), Release("New", 2011), Release("B Side", 2005, artist: "B"));
        var player = Player(lib);

        var first = new ArtistDetailViewModel("A", lib, player, settings: settings);
        Assert.Equal("oldest", first.ReleaseSortMode);                   // opens in the saved order
        first.SelectTabCommand.Execute("albums");
        Assert.Equal(new[] { "Old", "New" }, Names(first.AlbumReleases));
        first.IsActive = false;                                          // left for another artist, kept in history

        var second = new ArtistDetailViewModel("B", lib, player, settings: settings);
        Assert.Equal("oldest", second.ReleaseSortMode);
        second.SetReleaseSortCommand.Execute("newest");
        Assert.Equal("newest", settings.ArtistReleaseSortMode);          // the pick is written through

        first.IsActive = true;                                           // Back to the first artist
        Assert.Equal("newest", first.ReleaseSortMode);
        Assert.Equal(new[] { "New", "Old" }, Names(first.AlbumReleases));
    }
}
