using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): importing a playlist left songs "missing" that the library has under
/// a slightly different name — a feat. credit, a remaster suffix, an accent, "&amp;" for "and".
/// These pin the matcher on those spellings, and that it still tells a live take or a remix
/// apart from the studio song.
/// </summary>
public class FuzzyTrackMatcherVariantTests
{
    private static Track Lib(string title, string artist, string path = "")
        => new() { Title = title, Artist = artist, FilePath = path.Length > 0 ? path : $@"C:\m\{title}.mp3" };

    private static MatchResult One(PlaylistImportEntry entry, params Track[] library)
        => FuzzyTrackMatcher.Match(new[] { entry }, library).Single();

    [Fact]
    public void FeatCreditInTheEntryTitle_MatchesTheLibrarySongWithout()
    {
        var song = Lib("Stay", "The Kid LAROI");
        var m = One(new PlaylistImportEntry("Stay (with Justin Bieber)", "The Kid LAROI", ""), song,
            Lib("Stay With Me", "Sam Smith"));
        Assert.Same(song, m.Match);
    }

    [Fact]
    public void TrailingFeatInTheLibraryTitle_MatchesTheEntryWithout()
    {
        var song = Lib("Lose Yourself feat. Someone", "Eminem");
        var m = One(new PlaylistImportEntry("Lose Yourself", "Eminem", ""), song);
        Assert.Same(song, m.Match);
    }

    [Fact]
    public void RemasterSuffix_IsTheSameSong()
    {
        var song = Lib("Here Comes the Sun", "The Beatles");
        var m = One(new PlaylistImportEntry("Here Comes The Sun - Remastered 2009", "The Beatles", ""), song);
        Assert.Same(song, m.Match);
    }

    [Fact]
    public void Diacritics_AreFolded()
    {
        var song = Lib("Déjà Vu", "Beyoncé");
        var m = One(new PlaylistImportEntry("Deja Vu", "Beyonce", ""), song);
        Assert.Same(song, m.Match);
        Assert.Equal(1.0, m.Score, 3);
    }

    [Fact]
    public void AmpersandAndAnd_AreTheSame()
    {
        var song = Lib("Salt & Pepper", "Simon and Garfunkel");
        var m = One(new PlaylistImportEntry("Salt and Pepper", "Simon & Garfunkel", ""), song);
        Assert.Same(song, m.Match);
    }

    [Fact]
    public void FeaturedArtistListedFirst_StillMatchesOnTheSongsArtists()
    {
        // Exports often credit "A, B" where the library's primary artist is B.
        var song = Lib("Under Pressure", "Queen & David Bowie");
        var m = One(new PlaylistImportEntry("Under Pressure", "David Bowie", ""), song);
        Assert.Same(song, m.Match);
    }

    [Fact]
    public void StudioEntry_PrefersTheStudioSongOverTheLiveTake()
    {
        var live = Lib("Hotel California (Live)", "Eagles", @"C:\m\live.mp3");
        var studio = Lib("Hotel California - 2013 Remaster", "Eagles", @"C:\m\studio.mp3");
        var m = One(new PlaylistImportEntry("Hotel California", "Eagles", ""), live, studio);
        Assert.Same(studio, m.Match);
    }

    [Fact]
    public void RemixEntry_DoesNotSettleOnTheOriginal()
    {
        var original = Lib("Midnight City", "M83");
        var m = One(new PlaylistImportEntry("Midnight City (Eric Prydz Remix)", "M83", ""), original);
        Assert.Null(m.Match);
        // ...but the original is offered as a candidate for the review step.
        Assert.Contains(m.Candidates, c => ReferenceEquals(c.Track, original));
    }

    [Fact]
    public void UnmatchedEntry_CarriesItsBestCandidates_BestFirst()
    {
        var close = Lib("Smells Like Teen Spirt", "Nirvan");
        var farther = Lib("Smells Like Children", "Marilyn Manson");
        var unrelated = Lib("Wonderwall", "Oasis");
        var m = One(new PlaylistImportEntry("Smells Like Teen Spirit", "Nirvana Tribute Band", ""), farther, unrelated, close);
        Assert.True(m.Candidates.Count >= 1);
        Assert.Same(close, m.Candidates[0].Track);
        Assert.DoesNotContain(m.Candidates, c => ReferenceEquals(c.Track, unrelated));
        Assert.True(m.Candidates.Count <= FuzzyTrackMatcher.MaxCandidates);
    }

    [Fact]
    public void MatchedEntry_HasNoCandidates()
    {
        var m = One(new PlaylistImportEntry("Hello", "Adele", ""), Lib("Hello", "Adele"));
        Assert.NotNull(m.Match);
        Assert.Empty(m.Candidates);
    }

    [Fact]
    public void Search_FindsLibrarySongsByTitleOrArtistWords()
    {
        var a = Lib("Bohemian Rhapsody", "Queen");
        var b = Lib("Radio Ga Ga", "Queen");
        var c = Lib("Wonderwall", "Oasis");
        var hits = FuzzyTrackMatcher.Search("queen rhapsody", new[] { a, b, c }, 10);
        Assert.Same(a, hits[0]);
        Assert.DoesNotContain(c, hits);
        Assert.Contains(b, FuzzyTrackMatcher.Search("queen", new[] { a, b, c }, 10));
        Assert.Empty(FuzzyTrackMatcher.Search("  ", new[] { a, b, c }, 10));
    }
}
