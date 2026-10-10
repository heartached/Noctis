using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// GitHub #122 (2026-10-10): live albums and compilations get their own Type filter
/// entries on the Albums page and their own tabs / Overview rows on the artist page,
/// instead of being folded into "Other" / the Albums tab; "Live album" reads "Live Album".
/// </summary>
public class LiveCompilationReleaseTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LiveCompilationReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Classification from the tags the scanner reads ──

    private ReleaseType? ReadFlacReleaseType(string key, params string[] values)
    {
        var path = CreateFlac(_dir);
        using (var f = TagLib.File.Create(path))
        {
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
            xiph.SetField(key, values);
            f.Save();
        }
        using var read = TagLib.File.Create(path);
        return ExtendedTagIO.ReadReleaseType(read, out _);
    }

    [Theory]
    [InlineData("RELEASETYPE", "live", ReleaseType.Live)]
    [InlineData("RELEASETYPE", "album; live", ReleaseType.Live)]
    [InlineData("RELEASETYPE", "album; compilation", ReleaseType.Compilation)]
    [InlineData("MUSICBRAINZ_ALBUM_TYPE", "album/compilation", ReleaseType.Compilation)]
    public void Tag_ClassifiesLiveAndCompilation(string key, string value, ReleaseType expected)
        => Assert.Equal(expected, ReadFlacReleaseType(key, value));

    /// <summary>Picard writes a multi-valued release type to a Vorbis comment as repeated
    /// fields (RELEASETYPE=album, RELEASETYPE=live); only the first one was read, so a
    /// Picard-tagged FLAC live album classified as a plain Album.</summary>
    [Theory]
    [InlineData(ReleaseType.Live, "album", "live")]
    [InlineData(ReleaseType.Compilation, "album", "compilation")]
    public void Tag_RepeatedVorbisFields_AreAllRead(ReleaseType expected, params string[] values)
        => Assert.Equal(expected, ReadFlacReleaseType("RELEASETYPE", values));

    /// <summary>The ID3v2.4 form of the same: one TXXX frame holding null-separated values.</summary>
    [Fact]
    public void Tag_MultiValuedId3Frame_IsAllRead()
    {
        var path = CreateMp3WithId3v2Only(_dir);
        using (var f = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "RELEASETYPE", true);
            frame.Text = new[] { "album", "live" };
            f.Save();
        }
        using var read = TagLib.File.Create(path);
        Assert.Equal(ReleaseType.Live, ExtendedTagIO.ReadReleaseType(read, out _));
    }

    private static Album MakeAlbum(string name, string artist, int year, int tracks,
        ReleaseType? tagged = null, bool compilationFlag = false)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        for (var i = 1; i <= tracks; i++)
        {
            var t = new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
                IsCompilation = compilationFlag,
            };
            if (tagged is { } rt) { t.ReleaseType = rt; t.ReleaseTypeFromTag = true; }
            album.Tracks.Add(t);
        }
        album.TrackCount = album.Tracks.Count;
        return album;
    }

    [Fact]
    public void Album_ResolvesLiveFromTag_AndCompilationFromTheFlag()
    {
        Assert.Equal(ReleaseType.Live, MakeAlbum("Alive", "A", 2003, 12, ReleaseType.Live).ReleaseType);
        Assert.Equal(ReleaseType.Compilation, MakeAlbum("Hits", "A", 2010, 15, ReleaseType.Compilation).ReleaseType);
        // Untagged, but every track carries the compilation flag (TCMP / COMPILATION=1).
        Assert.Equal(ReleaseType.Compilation, MakeAlbum("Mix", "A", 2010, 15, compilationFlag: true).ReleaseType);
    }

    [Fact]
    public void KindTitle_CapitalisesEveryWord()
    {
        var live = MakeAlbum("Alive", "A", 2003, 12, ReleaseType.Live);
        Assert.Equal("Live Album", live.ReleaseKindTitle);
        Assert.Equal("Live Album · 2003", live.ReleaseKindYearLine);
        Assert.Equal("Remix Album", MakeAlbum("Rmx", "A", 0, 12, ReleaseType.Remix).ReleaseKindTitle);
        Assert.Equal("Compilation", MakeAlbum("Hits", "A", 0, 12, ReleaseType.Compilation).ReleaseKindTitle);
        Assert.Equal("Album", MakeAlbum("LP", "A", 0, 12).ReleaseKindTitle);
        Assert.Equal("EP", MakeAlbum("Short", "A", 0, 4).ReleaseKindTitle);
        Assert.Equal("LIVE ALBUM", live.ReleaseKindLabel); // the album-page kicker stays upper-case
    }

    // ── Albums page Type filter ──

    [Theory]
    [InlineData(ReleaseType.Live, ReleaseType.Live, true)]
    [InlineData(ReleaseType.Live, ReleaseType.Album, false)]
    [InlineData(ReleaseType.Live, ReleaseType.Other, false)]
    [InlineData(ReleaseType.Compilation, ReleaseType.Compilation, true)]
    [InlineData(ReleaseType.Compilation, ReleaseType.Album, false)]
    [InlineData(ReleaseType.Compilation, ReleaseType.Other, false)]
    [InlineData(ReleaseType.Soundtrack, ReleaseType.Other, true)]
    [InlineData(ReleaseType.Remix, ReleaseType.Other, true)]
    [InlineData(ReleaseType.Other, ReleaseType.Other, true)]
    [InlineData(ReleaseType.Album, ReleaseType.Other, false)]
    [InlineData(ReleaseType.Album, ReleaseType.Album, true)]
    [InlineData(ReleaseType.EP, ReleaseType.Other, false)]
    public void TypeFilter_GivesLiveAndCompilationsTheirOwnEntries(ReleaseType type, ReleaseType filter, bool shown)
        => Assert.Equal(shown, LibraryAlbumsViewModel.MatchesReleaseTypeFilter(type, filter));

    [Fact]
    public void TypeFilter_Labels()
    {
        Assert.Equal("Live Albums", Loc.T("Main.LiveAlbums"));
        Assert.Equal("Compilations", Loc.T("Main.Compilations"));
        Assert.Equal("Live Albums", Loc.T("ArtistDetail.LiveAlbums"));
        Assert.Equal("Compilations", Loc.T("ArtistDetail.Compilations"));
    }

    // ── Artist page ──

    private static ArtistDetailViewModel MakeArtist(string artist, params Album[] albums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(albums);
        foreach (var a in albums) lib.TrackList.AddRange(a.Tracks);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        return new ArtistDetailViewModel(artist, lib, player);
    }

    [Fact]
    public void ArtistPage_SplitsLiveAndCompilationsOutOfAlbums()
    {
        var vm = MakeArtist("A",
            MakeAlbum("LP", "A", 2001, 10),
            MakeAlbum("Soundtrack", "A", 2002, 10, ReleaseType.Soundtrack),
            MakeAlbum("Alive", "A", 2003, 12, ReleaseType.Live),
            MakeAlbum("Hits", "A", 2004, 15, ReleaseType.Compilation),
            MakeAlbum("Single", "A", 2005, 1));

        Assert.Equal(5, vm.Releases.Count);
        Assert.Equal(2, vm.AlbumCount);   // LP + Soundtrack: anything without its own tab
        Assert.Equal(1, vm.SingleCount);
        Assert.Equal(1, vm.LiveCount);
        Assert.Equal(1, vm.CompilationCount);
        Assert.True(vm.ShowLiveTab);
        Assert.True(vm.ShowCompilationsTab);
        Assert.Equal(new[] { "Alive" }, vm.OverviewLive.Select(a => a.Name));
        Assert.Equal(new[] { "Hits" }, vm.OverviewCompilations.Select(a => a.Name));
        Assert.DoesNotContain(vm.OverviewAlbums, a => a.Name is "Alive" or "Hits");

        vm.SelectTabCommand.Execute("albums");
        Assert.Equal(new[] { "Soundtrack", "LP" }, vm.AlbumReleases.Select(a => a.Name));
        Assert.Empty(vm.LiveReleases);    // tab grids fill when their tab opens (09-15)
        vm.SelectTabCommand.Execute("live");
        Assert.True(vm.IsTabLive);
        Assert.Equal(new[] { "Alive" }, vm.LiveReleases.Select(a => a.Name));
        vm.SelectTabCommand.Execute("compilations");
        Assert.True(vm.IsTabCompilations);
        Assert.Equal(new[] { "Hits" }, vm.CompilationReleases.Select(a => a.Name));
    }

    [Fact]
    public void ArtistPage_HidesTheTabsWhenThereAreNone()
    {
        var vm = MakeArtist("A", MakeAlbum("LP", "A", 2001, 10), MakeAlbum("Single", "A", 2005, 1));

        Assert.False(vm.ShowLiveTab);
        Assert.False(vm.ShowCompilationsTab);
        Assert.False(vm.HasOverviewLive);
        Assert.False(vm.HasOverviewCompilations);
    }

    [Fact]
    public void ArtistPage_SearchKeepsTheTab_ButEmptiesItsGrid()
    {
        var vm = MakeArtist("A", MakeAlbum("LP", "A", 2001, 10), MakeAlbum("Alive", "A", 2003, 12, ReleaseType.Live));
        vm.SelectTabCommand.Execute("live");

        vm.ApplyFilter("LP");
        Assert.True(vm.ShowLiveTab);      // the artist still has one; the search just hides it
        Assert.True(vm.IsTabLive);
        Assert.False(vm.HasLive);
        Assert.Empty(vm.LiveReleases);
    }
}
