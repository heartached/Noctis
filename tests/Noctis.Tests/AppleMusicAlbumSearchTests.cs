using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Cover for the animated-artwork album lookup falling through two sources. The iTunes Search
/// API's index is not the Apple Music catalogue and has holes in it: searching "YHLQMDLG"
/// (Bad Bunny) returns one hit there, a cover act's record, so the strict album match
/// correctly rejected it and the dialog reported a miss for an album Apple does serve an
/// animated cover for. The Apple Music web search page is asked first because it ranks the
/// real album top.
/// </summary>
public class AppleMusicAlbumSearchTests
{
    // Shape of the album links on a music.apple.com search page, trimmed from the live result
    // for "YHLQMDLG" captured 2026-08-02. Track links carry their album's ID too, so the same
    // ID repeats — 1500776322 is Bad Bunny's album and must come out first and once.
    private const string SearchPageHtml = """
        <a href="/us/album/yhlqmdlg/1500776322">YHLQMDLG</a>
        <a href="/us/album/vete/1500776322">Vete</a>
        <a href="/us/album/soli%C3%A1/1500776322">Solia</a>
        <a href="/us/artist/bad-bunny/1126808565">Bad Bunny</a>
        <a href="/us/album/2021/1568292352">2021</a>
        <a href="/gb/album/yhlqmdlg/1630423755">Yhlqmdlg</a>
        <a href="/us/playlist/pure-fuego/pl.abc123">Pure Fuego</a>
        """;

    [Fact]
    public void ReadsAlbumIdsFromTheSearchPageInRankOrder()
    {
        var ids = ITunesArtworkService.ExtractAppleMusicAlbumIds(SearchPageHtml);

        Assert.Equal(new long[] { 1500776322, 1568292352, 1630423755 }, ids);
    }

    [Fact]
    public void IgnoresLinksThatAreNotAlbums()
    {
        var ids = ITunesArtworkService.ExtractAppleMusicAlbumIds(SearchPageHtml);

        // The artist and playlist links on the same page are not albums.
        Assert.DoesNotContain(1126808565, ids);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void ReturnsNothingForAPageWithNoAlbums()
    {
        Assert.Empty(ITunesArtworkService.ExtractAppleMusicAlbumIds("<html><body>No results</body></html>"));
        Assert.Empty(ITunesArtworkService.ExtractAppleMusicAlbumIds(""));
    }

    [Fact]
    public void KeepsTheStrictAlbumMatchOnWhateverTheSourceReturns()
    {
        // Whichever source found the album, the wrong-album guard still has to reject the
        // cover act that shares the title — that is the whole reason the search was strict.
        Assert.True(ITunesArtworkService.IsLikelySameAlbum(
            "YHLQMDLG", "Bad Bunny", "YHLQMDLG", "Bad Bunny"));
        Assert.False(ITunesArtworkService.IsLikelySameAlbum(
            "Yhlqmdlg", "Jbeat Mix", "YHLQMDLG", "Bad Bunny"));
    }

    [Fact]
    public void PrefersTheKnownArtistWhenATypedNameMatchesOnTitleAlone()
    {
        // Typing "YHLQMDLG" carries no artist, so the strict match falls back to the title
        // and a cover act's identically titled album passes it too. The loaded track's artist
        // is what separates them, without being allowed to reject either.
        Assert.True(ITunesArtworkService.IsLikelySameArtist("Bad Bunny", "Bad Bunny"));
        Assert.False(ITunesArtworkService.IsLikelySameArtist("Jbeat Mix", "Bad Bunny"));

        // Partial credit both ways, so multi-artist tags still corroborate.
        Assert.True(ITunesArtworkService.IsLikelySameArtist("Rema & Selena Gomez", "Rema"));
        Assert.False(ITunesArtworkService.IsLikelySameArtist("", "Bad Bunny"));
    }

    // ── typed input classification ────────────────────────────────────────────
    // The box now doubles as an album-name field, so "is this an ID/URL" has to be a real
    // test rather than "contains 6+ digits anywhere", which swallowed typed album names.

    [Fact]
    public void ReadsAnAlbumIdFromAPastedAppleMusicUrl()
    {
        Assert.True(MetadataViewModel.TryExtractAppleAlbumId(
            "https://music.apple.com/us/album/yhlqmdlg/1500776322", out var id));
        Assert.Equal(1500776322, id);

        Assert.True(MetadataViewModel.TryExtractAppleAlbumId(
            "https://music.apple.com/gb/album/some-album/id1710982865?l=en", out var withPrefix));
        Assert.Equal(1710982865, withPrefix);
    }

    [Fact]
    public void ReadsABareAlbumId()
    {
        Assert.True(MetadataViewModel.TryExtractAppleAlbumId(" 1500776322 ", out var id));
        Assert.Equal(1500776322, id);
    }

    [Fact]
    public void TreatsATypedAlbumNameAsAQueryNotAnId()
    {
        Assert.False(MetadataViewModel.TryExtractAppleAlbumId("YHLQMDLG", out _));
        Assert.False(MetadataViewModel.TryExtractAppleAlbumId("1989", out _));
        Assert.False(MetadataViewModel.TryExtractAppleAlbumId("", out _));
        Assert.False(MetadataViewModel.TryExtractAppleAlbumId(null, out _));
        // The old rule matched any 6+ digit run anywhere, so this album name searched as an ID.
        Assert.False(MetadataViewModel.TryExtractAppleAlbumId("Blueprint 3 1000000 Hours", out _));
    }
}
