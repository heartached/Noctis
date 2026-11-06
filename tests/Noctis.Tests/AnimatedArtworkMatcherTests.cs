using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The animated-artwork chooser's ranking, link parsing and album-page reading. The chooser used
/// to accept only exact titles and stop at the first album with a loop, so it showed one album;
/// these pin the scoring that now orders every relevant candidate instead.
/// </summary>
public class AnimatedArtworkMatcherTests
{
    [Theory]
    [InlineData("Short n' Sweet", "short n sweet")]
    [InlineData("Short n’ Sweet", "short n sweet")]
    [InlineData("Beyoncé & JAY-Z", "beyonce and jay z")]
    [InlineData("  ¿Dónde   Están?  ", "donde estan")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_FoldsCaseAccentsPunctuationAndAmpersand(string? input, string expected)
        => Assert.Equal(expected, AnimatedArtworkMatcher.Normalize(input));

    [Theory]
    [InlineData("Short n' Sweet (Deluxe)", "short n sweet")]
    [InlineData("Short n' Sweet [Deluxe Edition]", "short n sweet")]
    [InlineData("Espresso - Single", "espresso")]
    [InlineData("Fruitcake - EP", "fruitcake")]
    [InlineData("Nonsense (feat. Coi Leray) - Single", "nonsense")]
    [InlineData("Old Town Road feat. Billy Ray Cyrus", "old town road")]
    [InlineData("Abbey Road (Remastered 2019)", "abbey road")]
    [InlineData("Abbey Road (2019 Remaster)", "abbey road")]
    [InlineData("Thriller (25th Anniversary Edition) [Explicit]", "thriller")]
    [InlineData("Short n' Sweet (Clean)", "short n sweet")]
    // A distinct release keeps its name: its artwork is different.
    [InlineData("1989 (Taylor's Version)", "1989 taylors version")]
    [InlineData("After Hours (The Til Dawn Edition)", "after hours the til dawn edition")]
    public void BaseTitle_StripsOnlyEditionAndFormatWrappers(string input, string expected)
        => Assert.Equal(expected, AnimatedArtworkMatcher.BaseTitle(input));

    [Fact]
    public void TitleSimilarity_ExactBeatsEditionBeatsDistinctRelease()
    {
        var exact = AnimatedArtworkMatcher.TitleSimilarity("Short n' Sweet", "short n’ sweet");
        var deluxe = AnimatedArtworkMatcher.TitleSimilarity("Short n' Sweet (Deluxe)", "Short n' Sweet");
        var taylors = AnimatedArtworkMatcher.TitleSimilarity("1989 (Taylor's Version)", "1989");
        var other = AnimatedArtworkMatcher.TitleSimilarity("Espresso - Single", "Short n' Sweet");

        Assert.Equal(1.0, exact);
        Assert.Equal(0.92, deluxe, 3);
        Assert.InRange(taylors, 0.01, 0.9);
        Assert.Equal(0, other);
    }

    [Theory]
    [InlineData("Happier Than Ever Billie Eilish")]
    [InlineData("Billie Eilish Happier Than Ever")]
    [InlineData("Happier Than Ever - Billie Eilish")]
    [InlineData("happier than ever by billie eilish")]
    public void TypedSimilarity_ReadsTheLeftoverWordsAsTheArtist(string typed)
    {
        var right = AnimatedArtworkMatcher.TypedSimilarity("Happier Than Ever", "Billie Eilish", typed);
        var single = AnimatedArtworkMatcher.TypedSimilarity("Happier Than Ever (Edit) - Single", "Billie Eilish", typed);
        var cover = AnimatedArtworkMatcher.TypedSimilarity("Happier Than Ever", "Piano Dreamers", typed);

        Assert.True(right >= 0.95, $"right album scored {right}");
        Assert.True(right > single, $"{right} vs single {single}");
        Assert.True(right > cover, $"{right} vs cover {cover}");
    }

    [Fact]
    public void TypedSimilarity_ThePlainTitleWithTheArtistRanksAboveItsDeluxe_AndViceVersa()
    {
        // Live 2026-10-18: "Short n' Sweet Sabrina Carpenter" scored the deluxe level with the
        // album itself, and the store's order put the deluxe first.
        const string artist = "Sabrina Carpenter";
        var plainQuery = "Short n' Sweet Sabrina Carpenter";
        Assert.True(
            AnimatedArtworkMatcher.TypedSimilarity("Short n' Sweet", artist, plainQuery) >
            AnimatedArtworkMatcher.TypedSimilarity("Short n' Sweet (Deluxe)", artist, plainQuery));

        var deluxeQuery = "Short n' Sweet Deluxe Sabrina Carpenter";
        Assert.True(
            AnimatedArtworkMatcher.TypedSimilarity("Short n' Sweet (Deluxe)", artist, deluxeQuery) >
            AnimatedArtworkMatcher.TypedSimilarity("Short n' Sweet", artist, deluxeQuery));
    }

    [Fact]
    public void ArtistSimilarity_UsesThePrimaryCreditOfAMultiArtistTag()
    {
        Assert.Equal(1.0, AnimatedArtworkMatcher.ArtistSimilarity("Lil Nas X", "Lil Nas X feat. Billy Ray Cyrus"));
        Assert.Equal(0.85, AnimatedArtworkMatcher.ArtistSimilarity("Lady Gaga & Bruno Mars", "Lady Gaga"));
        Assert.Equal(0, AnimatedArtworkMatcher.ArtistSimilarity("Karaoke Hits Band", "Bad Bunny"));
        Assert.Equal(0, AnimatedArtworkMatcher.ArtistSimilarity("Bad Bunny", ""));
    }

    private static ITunesArtworkService.ArtworkCandidate C(long id, string title, string artist)
        => new(id, title, artist, "t", "s", "h", $"https://music.apple.com/us/album/x/{id}");

    [Fact]
    public void Rank_TagSearch_PutsTheExactEditionsFirst_DeluxeNext_AndDropsUnrelatedTitles()
    {
        // The order the Apple Music search page returned them for "Sabrina Carpenter Short n'
        // Sweet" on 2026-10-18 (deluxe first), plus a karaoke record and an unrelated single.
        var candidates = new[]
        {
            C(1795512297, "Short n' Sweet (Deluxe)", "Sabrina Carpenter"),
            C(1750307020, "Short n' Sweet", "Sabrina Carpenter"),
            C(1740212432, "Espresso - Single", "Sabrina Carpenter"),
            C(1752214909, "Short n' Sweet", "Sabrina Carpenter"),
            C(9, "Short n' Sweet (Karaoke Version)", "Karaoke Hits Band"),
        };
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Short n' Sweet", "Sabrina Carpenter", "Sabrina Carpenter", Typed: false);

        var ranked = ITunesArtworkService.RankAnimatedCandidates(candidates, query, max: 10)
            .Select(r => r.Candidate.CollectionId).ToList();

        // The karaoke record is neither the title nor the artist: not offered (GitHub #167).
        Assert.Equal(new long[] { 1750307020, 1752214909, 1795512297 }, ranked);
    }

    [Fact]
    public void Rank_TagSearch_NeverOffersADifferentTitleByADifferentArtist()
    {
        // GitHub #167 (enoversum): "Wings of Love" by Erasure offered "Tears of Love" by Tears
        // for Fears — different title, artist and year. Two shared words were enough.
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Wings of Love", "Erasure", "Erasure", Typed: false);

        var onlyWrong = ITunesArtworkService.RankAnimatedCandidates(new[] { C(1, "Tears of Love", "Tears for Fears") }, query, 10);
        var both = ITunesArtworkService.RankAnimatedCandidates(
            new[] { C(1, "Tears of Love", "Tears for Fears"), C(2, "Wings of Love", "Erasure") }, query, 10);

        Assert.Empty(onlyWrong);
        Assert.Equal(new long[] { 2 }, both.Select(r => r.Candidate.CollectionId));
    }

    [Fact]
    public void Rank_TagSearch_KeepsTheSameTitleUnderAnotherCredit_ForAMistaggedArtist()
    {
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Wings of Love", "Erasure", "Erasure", Typed: false);
        var ranked = ITunesArtworkService.RankAnimatedCandidates(new[] { C(1, "Wings of Love", "Erasure & Friends Orchestra"), C(2, "Wings of Love", "Andy Bell") }, query, 10);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void Rank_TagSearch_YearAndSongCountOnlyBreakTiesBetweenEditions()
    {
        // Two same-titled editions: the one from the local album's year with its song count first,
        // even though the store listed the reissue first.
        var reissue = C(1, "Wings of Love", "Erasure") with { ReleaseYear = 2011, TrackCount = 20 };
        var original = C(2, "Wings of Love", "Erasure") with { ReleaseYear = 1988, TrackCount = 12 };
        var deluxe = C(3, "Wings of Love (Deluxe)", "Erasure") with { ReleaseYear = 1988, TrackCount = 12 };
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Wings of Love", "Erasure", "Erasure", Typed: false)
        {
            Year = 1988,
            TrackCount = 12,
        };

        var ranked = ITunesArtworkService.RankAnimatedCandidates(new[] { reissue, deluxe, original }, query, 10);

        // The tie-break never lifts an edition wrapper over the exact title.
        Assert.Equal(new long[] { 2, 1, 3 }, ranked.Select(r => r.Candidate.CollectionId));
    }

    [Theory]
    [InlineData(1988, 1988, 12, 12, 5)]
    [InlineData(1988, 1989, 12, 13, 2.5)]
    [InlineData(2011, 1988, 20, 12, 0)]
    [InlineData(0, 1988, 0, 12, 0)]
    public void EditionTieBreak_StaysBelowTheEditionGap(int candidateYear, int year, int candidateTracks, int tracks, double expected)
    {
        var bonus = AnimatedArtworkMatcher.EditionTieBreak(candidateYear, year, candidateTracks, tracks);
        Assert.Equal(expected, bonus, 3);
        Assert.True(bonus < 8);
    }

    [Fact]
    public void Rank_TypedQuery_PrefersTheRealAlbumOverACoverActAndKaraoke()
    {
        var candidates = new[]
        {
            C(1, "YHLQMDLG (Karaoke Version)", "Latin Karaoke Stars"),
            C(2, "YHLQMDLG", "Piano Covers Club"),
            C(1500776322, "YHLQMDLG", "Bad Bunny"),
        };
        var query = new ITunesArtworkService.AnimatedArtworkQuery("YHLQMDLG", "", "Bad Bunny", Typed: true);

        var ranked = ITunesArtworkService.RankAnimatedCandidates(candidates, query, max: 10);

        // The edited track's artist only breaks the tie between same-titled albums.
        Assert.Equal(1500776322, ranked[0].Candidate.CollectionId);
        Assert.Equal(1, ranked[^1].Candidate.CollectionId);
    }

    [Fact]
    public void Rank_WhenNoTitleIsClose_KeepsTheArtistsAlbumsSoAMistaggedAlbumStillOffersSomething()
    {
        var candidates = new[]
        {
            C(1, "Emails I Can't Send", "Sabrina Carpenter"),
            C(2, "Totally Different", "Someone Else"),
        };
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Unknown Thing", "Sabrina Carpenter", "Sabrina Carpenter", Typed: false);

        var ranked = ITunesArtworkService.RankAnimatedCandidates(candidates, query, max: 10);

        Assert.Equal(new long[] { 1 }, ranked.Select(r => r.Candidate.CollectionId));
    }

    [Fact]
    public void Rank_TypedArtistName_KeepsThatArtistsAlbums()
    {
        // Owner 11-02: typing "selena gomez" in the chooser of a 4batz & Drake single found
        // nothing — no album TITLE resembles an artist name, and the fallback only looked for
        // the edited track's own artist.
        var candidates = new[]
        {
            C(1, "Rare", "Selena Gomez"),
            C(2, "Revival (Deluxe Edition)", "Selena Gomez"),
            C(3, "I Said I Love You First", "Selena Gomez & benny blanco"),
            C(4, "Totally Different", "Someone Else"),
        };
        var query = new ITunesArtworkService.AnimatedArtworkQuery("selena gomez", "", "4batz & Drake", Typed: true);

        var ranked = ITunesArtworkService.RankAnimatedCandidates(candidates, query, max: 10);

        Assert.Equal(new long[] { 1, 2, 3 }, ranked.Select(r => r.Candidate.CollectionId).Order());
    }

    [Fact]
    public void Rank_CapsHowManyAlbumsAreOpened()
    {
        var candidates = Enumerable.Range(1, 20).Select(i => C(i, "Same Title", "Artist")).ToList();
        var query = new ITunesArtworkService.AnimatedArtworkQuery("Same Title", "Artist", "Artist", Typed: false);

        var ranked = ITunesArtworkService.RankAnimatedCandidates(candidates, query, max: 10);

        // Equal scores keep the store's own order.
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), ranked.Select(r => r.Candidate.CollectionId));
    }

    [Theory]
    [InlineData("https://music.apple.com/us/album/short-n-sweet-deluxe/1795512297", "Album", "us", 1795512297L, "https://music.apple.com/us/album/short-n-sweet-deluxe/1795512297")]
    [InlineData("music.apple.com/gb/album/short-n-sweet-deluxe/1795512297", "Album", "gb", 1795512297L, "https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297")]
    // ?i= names a track on the album: the album is still the path's id.
    [InlineData("https://music.apple.com/us/album/taste/1795512297?i=1795512308", "Album", "us", 1795512297L, "https://music.apple.com/us/album/taste/1795512297")]
    [InlineData("https://music.apple.com/de/song/taste/1795512308", "Song", "de", 1795512308L, null)]
    [InlineData("https://itunes.apple.com/us/album/id1500776322", "Album", "us", 1500776322L, null)]
    // The slug can be a number itself; the id is the last numeric segment.
    [InlineData("https://music.apple.com/us/album/1000000-ep/1628626875", "Album", "us", 1628626875L, "https://music.apple.com/us/album/1000000-ep/1628626875")]
    [InlineData("1795512297", "Id", "us", 1795512297L, null)]
    public void Link_ParsesAlbumSongAndBareIds(string input, string kind, string storefront, long id, string? page)
    {
        Assert.True(AppleMusicLink.TryParse(input, out var link));
        Assert.Equal(kind, link.Kind.ToString());
        Assert.Equal(storefront, link.Storefront);
        Assert.Equal(id, link.Id);
        Assert.Equal(page, link.PageUrl);
    }

    [Fact]
    public void Link_ParsesPlaylists()
    {
        Assert.True(AppleMusicLink.TryParse(
            "https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb?l=en", out var link));
        Assert.Equal(AppleMusicLinkKind.Playlist, link.Kind);
        Assert.Equal("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", link.PageUrl);
    }

    [Theory]
    [InlineData("YHLQMDLG")]
    [InlineData("1989")]
    [InlineData("Blueprint 3 1000000 Hours")]
    [InlineData("https://notapple.com/us/album/x/1500776322")]
    [InlineData("https://music.apple.com.evil.example/us/album/x/1500776322")]
    [InlineData("ftp://music.apple.com/us/album/x/1500776322")]
    [InlineData("https://music.apple.com/us/artist/bad-bunny/1126808565")]
    [InlineData("https://music.apple.com/us/music-video/feather/1714377340")]
    [InlineData("https://music.apple.com/us/playlist/no-id-here")]
    [InlineData("")]
    [InlineData(null)]
    public void Link_RejectsTextAndForeignHosts(string? input)
        => Assert.False(AppleMusicLink.TryParse(input, out _));

    // Shape of a live album page (music.apple.com/us/album/short-n-sweet-deluxe/1795512297,
    // 2026-10-18), trimmed: the square loop, the tall loop with its nested gradient, and the
    // header's title and credit.
    private const string AlbumPage = """
        <meta name="apple:title" content="Short n&#39; Sweet (Deluxe)">
        <meta property="og:image" content="https://is1-ssl.mzstatic.com/image/thumb/Music221/v4/c9/x.jpg/1200x630wp-60.jpg">
        <script type="application/json" id="serialized-server-data">[{"title":"Short n' Sweet (Deluxe)","subtitleLinks":[{"title":"Sabrina Carpenter","segue":{}}],
        "videoArtwork":{"dictionary":{"motionDetailSquare":{"previewFrame":{"bgColor":"69b2c4","height":3840,"url":"https://is1-ssl.mzstatic.com/image/thumb/Video211/v4/f1/sq.png/{w}x{h}bb.{f}","width":3840},"video":"https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/82/square_default.m3u8"}},"cropStyle":"cc"},
        "tallVideoArtwork":{"dictionary":{"motionDetailTall":{"previewFrame":{"bgColor":"000000","gradient":{"color":"000000","y2":0.48},"height":2732,"textGradient":["ffffff","460d0f"],"url":"https://is1-ssl.mzstatic.com/image/thumb/Video211/v4/1a/tall.png/{w}x{h}bb.{f}","width":2048},"video":"https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/f4/tall_default.m3u8"}},"cropStyle":"bb"},"trackCount":17}]</script>
        """;

    [Fact]
    public void Page_ReadsBothCropsTheirStillsAndTheHeader()
    {
        var page = MotionArtworkPage.Parse(AlbumPage);

        Assert.True(page.HasMotion);
        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/82/square_default.m3u8", page.SquareVideoUrl);
        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/f4/tall_default.m3u8", page.TallVideoUrl);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Video211/v4/f1/sq.png/{w}x{h}bb.{f}", page.SquarePosterUrl);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Video211/v4/1a/tall.png/{w}x{h}bb.{f}", page.TallPosterUrl);
        Assert.Equal("Short n' Sweet (Deluxe)", page.Title);
        Assert.Equal("Sabrina Carpenter", page.Artist);
        Assert.StartsWith("https://is1-ssl.mzstatic.com/", page.ImageUrl);
    }

    [Fact]
    public void Page_WithoutALoop_HasNoMotion()
    {
        var page = MotionArtworkPage.Parse("""<script>{"videoArtwork":null,"tallVideoArtwork":null,"trackCount":11}</script>""");
        Assert.False(page.HasMotion);
        Assert.Null(page.SquareVideoUrl);
        Assert.Null(page.TallVideoUrl);
    }

    [Fact]
    public void Page_IgnoresAStreamOffApplesMediaHosts()
    {
        var page = MotionArtworkPage.Parse(
            """{"videoArtwork":{"dictionary":{"motionDetailSquare":{"video":"https://evil.example/x_default.m3u8"}}}}""");
        Assert.False(page.HasMotion);
    }

    [Fact]
    public void Page_ReadsAnEscapedCredit()
    {
        var page = MotionArtworkPage.Parse("""{"subtitleLinks":[{"title":"Tyler, The \"Creator\""}]}""");
        Assert.Equal("Tyler, The \"Creator\"", page.Artist);
    }

    [Theory]
    [InlineData("https://x.mzstatic.com/a.png/{w}x{h}bb.{f}", "https://x.mzstatic.com/a.png/300x300bb.jpg")]
    [InlineData("https://x.mzstatic.com/a.png/{w}x{h}{c}.{f}", "https://x.mzstatic.com/a.png/300x300bb.jpg")]
    [InlineData("https://x.mzstatic.com/a.jpg/100x100bb.jpg", "https://x.mzstatic.com/a.jpg/100x100bb.jpg")]
    [InlineData(null, null)]
    public void SizeImageTemplate_FillsApplesTemplate(string? template, string? expected)
        => Assert.Equal(expected, MotionArtworkPage.SizeImageTemplate(template, 300));
}
