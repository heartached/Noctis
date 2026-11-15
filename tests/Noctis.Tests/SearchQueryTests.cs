using System.Collections.Generic;
using System.Linq;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>GitHub #107: combined multi-field search and field tags.</summary>
public class SearchQueryTests
{
    private static Track T(string title, string artist, string album, string genre = "", int year = 0,
        string? albumArtist = null, string composer = "") => new()
    {
        Title = title,
        Artist = artist,
        Album = album,
        AlbumArtist = albumArtist ?? artist,
        Genre = genre,
        Year = year,
        Composer = composer,
    };

    private static readonly Track Prayer = T("Like a Prayer", "Madonna", "Like a Prayer", "Pop", 1989);
    private static readonly Track PrayerRemix = T("Like a Prayer (Remix)", "Madonna", "The Immaculate Collection", "Pop", 1990);
    private static readonly Track Airbag = T("Airbag", "Radiohead", "OK Computer", "Alternative Rock", 1997);
    private static readonly Track Creep = T("Creep", "Radiohead", "Pablo Honey", "Alternative Rock", 1993);

    private static bool M(string query, Track t) => SearchQuery.Parse(query).Matches(t);

    [Theory]
    [InlineData("radiohead ok computer", true)]   // artist + album (the issue's example)
    [InlineData("ok computer radiohead", true)]   // any order
    [InlineData("radiohead 1997", true)]          // artist + year
    [InlineData("rock 1997", true)]               // genre + year
    [InlineData("rock 1993", false)]
    [InlineData("radiohead pablo", false)]
    public void FreeWords_MatchAcrossFields(string query, bool expected)
        => Assert.Equal(expected, M(query, Airbag));

    [Fact]
    public void BareYear_MatchesExactYearOnly()
    {
        Assert.True(M("1997", Airbag));
        Assert.False(M("99", T("x", "y", "z", year: 1999)));
    }

    [Fact]
    public void ArtistTag_CombinesWithFreeText()
    {
        // The issue's example: all Madonna remixes; "+" is a connector, not a word.
        var q = SearchQuery.Parse("artist:Madonna + remix");
        Assert.True(q.Matches(PrayerRemix));
        Assert.False(q.Matches(Prayer));
        Assert.False(q.Matches(T("Song (Remix)", "Someone Else", "X")));
    }

    [Fact]
    public void FieldTag_OnlyMatchesItsField()
    {
        // "Like a Prayer" is Madonna's album AND title; title:/album: split them apart.
        Assert.True(M("album:immaculate", PrayerRemix));
        Assert.False(M("album:remix", PrayerRemix));
        Assert.True(M("title:remix", PrayerRemix));
        Assert.False(M("artist:prayer", Prayer));
        Assert.True(M("genre:pop", Prayer));
        Assert.False(M("genre:rock", Prayer));
    }

    [Fact]
    public void ArtistTag_AlsoMatchesAlbumArtist()
    {
        var feature = T("Guest Spot", "Guest", "Compilation", albumArtist: "Various Artists");
        Assert.True(M("artist:various", feature));
        Assert.True(M("albumartist:various", feature));
        Assert.False(M("albumartist:guest", feature));
    }

    [Fact]
    public void QuotedTagValue_KeepsPhraseTogether()
    {
        Assert.True(M("album:\"ok computer\"", Airbag));
        Assert.False(M("album:\"computer ok\"", Airbag));
    }

    [Theory]
    [InlineData("year:1997", true)]
    [InlineData("year:1990-1999", true)]
    [InlineData("year:1999-1990", true)]   // reversed range
    [InlineData("year:2000-2009", false)]
    [InlineData("year:>1996", true)]
    [InlineData("year:>1997", false)]
    [InlineData("year:>=1997", true)]
    [InlineData("year:<1998", true)]
    [InlineData("year:<=1996", false)]
    public void YearTag_ExactRangeAndComparisons(string query, bool expected)
        => Assert.Equal(expected, M(query, Airbag));

    [Fact]
    public void GenreAndYearTags_Combine()
    {
        var q = SearchQuery.Parse("genre:rock year:1990-1995");
        Assert.True(q.Matches(Creep));
        Assert.False(q.Matches(Airbag));
        Assert.False(q.Matches(Prayer));
    }

    [Fact]
    public void ComposerTag_MatchesComposer()
        => Assert.True(M("composer:bach", T("Air", "Orchestra", "Suites", composer: "J.S. Bach")));

    [Fact]
    public void IncompleteTag_DoesNotEmptyTheResults()
    {
        // Mid-typing "artist:" or "year:19" must not hide everything.
        Assert.True(M("radiohead artist:", Airbag));
        Assert.True(M("radiohead year:19", Airbag));
    }

    [Fact]
    public void UnknownPrefix_IsPlainText()
    {
        var reZero = T("Re:Zero Theme", "Someone", "OST");
        Assert.True(M("re:zero", reZero));
        Assert.False(SearchQuery.Parse("re:zero").HasFieldTerms);
    }

    [Fact]
    public void WholePhrase_StillMatchesWithoutSpaces()
    {
        // Pre-#107 behaviour kept: the whole query folds to one key.
        var t = T("Cruel Summer", "Taylor Swift", "Lover");
        Assert.True(M("taylorswift", t));
        Assert.True(M("dont you", T("Don't You", "X", "Y")));
    }

    [Fact]
    public void PunctuationOnlyQuery_DoesNotMatchEverything()
    {
        Assert.False(M("&", Airbag));
        Assert.True(M("&", T("Rock & Roll", "X", "Y")));
    }

    [Fact]
    public void BlankQuery_MatchesEverything()
    {
        Assert.True(M("", Airbag));
        Assert.True(M("   ", Airbag));
    }

    [Fact]
    public void MatchesName_FreeWordsAnyOrder_AndArtistTag()
    {
        Assert.True(SearchQuery.Parse("swift taylor").MatchesName("Taylor Swift", SearchText.Normalize("Taylor Swift")));
        Assert.True(SearchQuery.Parse("artist:madonna").MatchesName("Madonna", "madonna"));
        Assert.False(SearchQuery.Parse("artist:madonna").MatchesName("Radiohead", "radiohead"));
        // A tag an artist row can't answer doesn't narrow the list.
        Assert.True(SearchQuery.Parse("genre:rock").MatchesName("Radiohead", "radiohead"));
        Assert.False(SearchQuery.Parse("&").MatchesName("Radiohead", "radiohead"));
    }

    [Fact]
    public void MatchesAlbum_UsesAlbumLevelFieldsOnly()
    {
        var album = new Album { Name = "OK Computer", Artist = "Radiohead", Genre = "Alternative Rock", Year = 1997,
            Tracks = new List<Track> { Airbag } };
        Assert.True(SearchQuery.Parse("radiohead ok computer").MatchesAlbum(album));
        Assert.True(SearchQuery.Parse("rock 1997").MatchesAlbum(album));
        Assert.True(SearchQuery.Parse("artist:radiohead year:1990-1999").MatchesAlbum(album));
        Assert.False(SearchQuery.Parse("airbag").MatchesAlbum(album));        // a track title, not the release
        Assert.False(SearchQuery.Parse("title:airbag").MatchesAlbum(album));
    }

    [Fact]
    public void HighlightTerms_DropTagPrefixes()
        => Assert.Equal(new[] { "Madonna", "remix" },
            SearchQuery.Parse("artist:Madonna + remix year:1990").HighlightTerms);

    [Fact]
    public void SongsPage_FiltersWithCombinedQuery()
    {
        var tracks = new List<Track> { Prayer, PrayerRemix, Airbag, Creep };
        var result = LibrarySongsViewModel.BuildFilteredAndSortedTracks(
            tracks, "artist:madonna remix", "Title", sortAsc: true, favOnly: false, qualityFilter: "All");
        Assert.Equal(new[] { PrayerRemix }, result);

        result = LibrarySongsViewModel.BuildFilteredAndSortedTracks(
            tracks, "radiohead 1993", "Title", sortAsc: true, favOnly: false, qualityFilter: "All");
        Assert.Equal(new[] { Creep }, result);
    }

    // GitHub #227: a quoted tag value is the whole field (or one whole artist/genre of a
    // multi-value credit), not a substring; unquoted stays "contains".
    private static readonly Track Coin = T("Talk Too Much", "COIN", "How Will You Know If You Never Try", "Indie Pop", 2017);
    private static readonly Track Aucoin = T("Want to Want", "Rich Aucoin", "Release", "Electronic", 2016);

    [Fact]
    public void QuotedArtist_IsExactArtist()
    {
        Assert.True(M("artist:\"Coin\"", Coin));
        Assert.False(M("artist:\"Coin\"", Aucoin));
        Assert.True(M("artist:coin", Aucoin));              // unquoted keeps contains
        Assert.False(M("artist:\"Rich\"", Aucoin));
        Assert.True(M("artist:\"rich aucoin\"", Aucoin));
    }

    [Fact]
    public void QuotedArtist_MatchesOneWholeArtistOfACredit()
    {
        Assert.True(M("artist:\"Coin\"", T("Crash My Car", "Coin feat. Someone", "X")));
        Assert.True(M("artist:\"Someone\"", T("Crash My Car", "Coin feat. Someone", "X")));
        Assert.True(M("artist:\"Coin\"", T("Guest Spot", "Guest", "X", albumArtist: "Coin")));
        Assert.False(M("albumartist:\"Coin\"", T("Guest Spot", "Coin", "X", albumArtist: "Various Artists")));
        // A protected name stays whole, and the whole credit always matches itself.
        Assert.False(M("artist:\"AC\"", T("Thunderstruck", "AC/DC", "X")));
        Assert.True(M("artist:\"ac/dc\"", T("Thunderstruck", "AC/DC", "X")));
        Assert.True(M("artist:\"Simon & Garfunkel\"", T("Mrs. Robinson", "Simon & Garfunkel", "X")));
    }

    [Fact]
    public void QuotedArtist_FoldsCaseAndAccentsLikeUnquoted()
    {
        Assert.True(M("artist:\"beyonce\"", T("Halo", "Beyoncé", "I Am... Sasha Fierce")));
        Assert.True(M("artist:\"COIN\"", Coin));
    }

    [Fact]
    public void QuotedGenre_IsOneWholeGenre()
    {
        Assert.False(M("genre:\"Rock\"", Airbag));                       // "Alternative Rock"
        Assert.True(M("genre:\"alternative rock\"", Airbag));
        Assert.True(M("genre:\"Rock\"", T("x", "y", "z", genre: "Pop; Rock")));
        Assert.True(M("genre:rock", Airbag));                              // unquoted keeps contains
    }

    [Fact]
    public void QuotedTitleAlbumComposer_AreWholeValues()
    {
        Assert.True(M("title:\"creep\"", Creep));
        Assert.False(M("title:\"creep\"", T("Creep (Acoustic)", "Radiohead", "X")));
        Assert.False(M("album:\"ok\"", Airbag));
        Assert.False(M("composer:\"bach\"", T("Air", "Orchestra", "Suites", composer: "J.S. Bach")));
        Assert.True(M("composer:\"j.s. bach\"", T("Air", "Orchestra", "Suites", composer: "J.S. Bach")));
    }

    [Fact]
    public void QuotedWord_WithoutTag_MatchesWholeWordsOnly()
    {
        Assert.True(M("\"coin\"", Coin));
        Assert.False(M("\"coin\"", Aucoin));                              // was a substring hit
        Assert.True(M("coin", Aucoin));                                    // unquoted unchanged
        Assert.True(M("\"too much\"", Coin));                             // phrase inside a title
        Assert.False(M("\"much too\"", Coin));
        Assert.True(M("\"dont you\"", T("Don't You (Forget About Me)", "Simple Minds", "X")));
        Assert.True(M("\"ok computer\"", T("x", "Radiohead", "OK Computer OKNOTOK 1997 2017")));
        Assert.True(M("\"1997\"", Airbag));
        Assert.True(M("\"&\"", T("Rock & Roll", "X", "Y")));              // punctuation-only phrase
        Assert.False(M("\"&\"", Airbag));
    }

    [Fact]
    public void UnterminatedQuote_StaysContainsWhileTyping()
    {
        Assert.True(M("artist:\"Coin", Aucoin));
        Assert.True(M("artist:\"rich auc", Aucoin));
        Assert.True(M("\"auco", Aucoin));
    }

    [Fact]
    public void EscapedQuote_IsPartOfTheValue()
    {
        var t = T("Say \"Hi\"", "X", "Y");
        Assert.True(M("title:\"Say \\\"Hi\\\"\"", t));
        Assert.False(M("title:\"Say\"", t));
    }

    [Fact]
    public void QuotedValues_OnNameAndAlbumSurfaces()
    {
        Assert.True(SearchQuery.Parse("artist:\"Coin\"").MatchesName("COIN", "coin"));
        Assert.False(SearchQuery.Parse("artist:\"Coin\"").MatchesName("Rich Aucoin", "richaucoin"));
        Assert.False(SearchQuery.Parse("\"coin\"").MatchesName("Rich Aucoin", "richaucoin"));
        Assert.True(SearchQuery.Parse("\"aucoin\"").MatchesName("Rich Aucoin", "richaucoin"));

        var album = new Album { Name = "Release", Artist = "Rich Aucoin", Genre = "Electronic", Year = 2016,
            Tracks = new List<Track> { Aucoin } };
        Assert.False(SearchQuery.Parse("artist:\"Coin\"").MatchesAlbum(album));
        Assert.True(SearchQuery.Parse("artist:coin").MatchesAlbum(album));
        Assert.True(SearchQuery.Parse("album:\"release\"").MatchesAlbum(album));
        Assert.False(SearchQuery.Parse("\"coin\"").MatchesAlbum(album));
    }

    [Fact]
    public void HighlightTerms_DropQuotes()
        => Assert.Equal(new[] { "Coin" }, SearchQuery.Parse("artist:\"Coin\"").HighlightTerms);

    [Fact]
    public void PlaylistFind_UsesSameSyntax()
    {
        Assert.True(PlaylistViewModel.MatchesSearch(Airbag, "radiohead ok computer"));
        Assert.False(PlaylistViewModel.MatchesSearch(Creep, "radiohead ok computer"));
        Assert.Equal(2, new[] { Prayer, PrayerRemix, Airbag }.Count(t => PlaylistViewModel.MatchesSearch(t, "artist:madonna")));
    }
}
