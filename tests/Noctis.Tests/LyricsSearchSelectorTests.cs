using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// LRCLIB /search is fuzzy and relevance-ordered, and the old picker ranked lyric
/// FORMAT above identity — a low-ranked different song with synced lyrics beat the
/// exact top match and got persisted to a sidecar. These pin the validation gate
/// (duration + names), the instrumental exclusion, and the Unknown Artist handling.
/// </summary>
public class LyricsSearchSelectorTests
{
    private static LrcLibResult Result(
        string? track = "Test Song", string? artist = "Test Artist", double duration = 200,
        string? plain = null, string? synced = null, string? lyricsfile = null, bool instrumental = false) =>
        new()
        {
            TrackName = track,
            ArtistName = artist,
            Duration = duration,
            PlainLyrics = plain,
            SyncedLyrics = synced,
            Lyricsfile = lyricsfile,
            Instrumental = instrumental,
        };

    [Fact]
    public void MalayalamTitle_WithTheVowelSignInItsTwoParts_StillMatches()
    {
        // GitHub #149: "കൊച്ചി" with "ൊ" (U+0D4A) locally, as "െ" + "ാ" (U+0D46 U+0D3E) online —
        // canonically the same text.
        var candidate = Result(track: "കൊച്ചി", artist: "ഗായകൻ", synced: "[00:01.00]x");

        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { candidate }, "ഗായകൻ", "കൊച്ചി", 200);

        Assert.Same(candidate, pick);
    }

    // ── Duration validation ──

    [Fact]
    public void DurationMismatch_RejectsCandidate()
    {
        var wrongSong = Result(duration: 260, synced: "[00:01.00]wrong");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { wrongSong }, "Test Artist", "Test Song", 200);
        Assert.Null(pick);
    }

    [Fact]
    public void DurationWithinTolerance_Accepts()
    {
        var candidate = Result(duration: 204, synced: "[00:01.00]right");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { candidate }, "Test Artist", "Test Song", 200);
        Assert.Same(candidate, pick);
    }

    [Fact]
    public void UnknownDuration_OnEitherSide_DoesNotReject()
    {
        var noDuration = Result(duration: 0, synced: "[00:01.00]x");
        Assert.NotNull(LyricsSearchSelector.PickFromSearchResults(
            new[] { noDuration }, "Test Artist", "Test Song", 200));
        Assert.NotNull(LyricsSearchSelector.PickFromSearchResults(
            new[] { Result(synced: "[00:01.00]x") }, "Test Artist", "Test Song", 0));
    }

    // ── Name validation ──

    [Fact]
    public void TitleMismatch_RejectsCandidate_NoFallbackToWrongSong()
    {
        var wrongSong = Result(track: "Completely Different", synced: "[00:01.00]wrong");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { wrongSong }, "Test Artist", "Test Song", 200);
        Assert.Null(pick);
    }

    [Fact]
    public void TitleContainment_ToleratesRemasterSuffix()
    {
        var candidate = Result(track: "Test Song (Remastered 2011)", plain: "words");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { candidate }, "Test Artist", "Test Song", 200);
        Assert.Same(candidate, pick);
    }

    [Fact]
    public void ArtistMismatch_RejectsCandidate()
    {
        var cover = Result(artist: "Some Cover Band", synced: "[00:01.00]x");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { cover }, "Test Artist", "Test Song", 200);
        Assert.Null(pick);
    }

    // ── Format tiers apply within the validated subset ──

    [Fact]
    public void FormatPreference_OnlyAmongValidatedCandidates()
    {
        // Old picker chose wrongSynced (format tier beat relevance). The right
        // song with plain lyrics must win over a duration-mismatched synced hit.
        var wrongSynced = Result(track: "Test Song", duration: 300, synced: "[00:01.00]wrong");
        var rightPlain = Result(plain: "right words");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { wrongSynced, rightPlain }, "Test Artist", "Test Song", 200);
        Assert.Same(rightPlain, pick);
    }

    [Fact]
    public void SyncedBeatsPlain_WithinValidatedSet()
    {
        var plain = Result(plain: "words");
        var synced = Result(synced: "[00:01.00]words");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { plain, synced }, "Test Artist", "Test Song", 200);
        Assert.Same(synced, pick);
    }

    [Fact]
    public void RequireSynced_SkipsPlainOnlyCandidates()
    {
        var plain = Result(plain: "words");
        Assert.Null(LyricsSearchSelector.PickFromSearchResults(
            new[] { plain }, "Test Artist", "Test Song", 200, requireSynced: true));

        var synced = Result(synced: "[00:01.00]words");
        Assert.Same(synced, LyricsSearchSelector.PickFromSearchResults(
            new[] { plain, synced }, "Test Artist", "Test Song", 200, requireSynced: true));
    }

    // ── Instrumental exclusion ──

    [Fact]
    public void InstrumentalEntries_AreNeverPicked()
    {
        var instrumental = Result(instrumental: true, synced: "[00:01.00]spurious");
        var pick = LyricsSearchSelector.PickFromSearchResults(
            new[] { instrumental }, "Test Artist", "Test Song", 200);
        Assert.Null(pick);
    }

    // ── Unknown Artist handling ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown Artist")]
    [InlineData("unknown artist")]
    public void IsUnknownArtist_TrueForPlaceholders(string? artist) =>
        Assert.True(LyricsSearchSelector.IsUnknownArtist(artist));

    [Fact]
    public void IsUnknownArtist_FalseForRealName() =>
        Assert.False(LyricsSearchSelector.IsUnknownArtist("Daft Punk"));

    [Fact]
    public void UnknownLocalArtist_SkipsArtistCheck_ButTitleAndDurationStillGate()
    {
        var candidate = Result(artist: "Whoever Tagged It", synced: "[00:01.00]x");
        Assert.Same(candidate, LyricsSearchSelector.PickFromSearchResults(
            new[] { candidate }, "Unknown Artist", "Test Song", 200));

        var wrongDuration = Result(artist: "Whoever", duration: 300, synced: "[00:01.00]x");
        Assert.Null(LyricsSearchSelector.PickFromSearchResults(
            new[] { wrongDuration }, "Unknown Artist", "Test Song", 200));
    }

    /// <summary>GitHub #232, live 11-11: Kugou's search for "Gus Gus Remembrance" credits "Gusgus"
    /// (486 s); spacing and case aside it is the same artist.</summary>
    [Fact]
    public void ArtistSpelledWithoutTheSpace_StillMatches_ButAnotherArtistDoesNot()
    {
        Assert.True(LyricsSearchSelector.MatchesTrack(Result(track: "Remembrance", artist: "Gusgus", duration: 486), "Gus Gus", "Remembrance", 485));
        Assert.True(LyricsSearchSelector.MatchesTrack(Result(track: "Remembrance", artist: "Gus Gus", duration: 486), "GusGus", "Remembrance", 485));
        Assert.False(LyricsSearchSelector.MatchesTrack(Result(track: "Remembrance", artist: "Gus Dapperton", duration: 486), "Gus Gus", "Remembrance", 485));
    }

    [Theory]
    [InlineData("Tim Neuhaus & The Cabinet", "Tim Neuhaus")]
    [InlineData("Sid Sriram feat. Yuvan Shankar Raja", "Sid Sriram")]
    [InlineData("Simon and Garfunkel", "Simon")]
    [InlineData("GusGus", "GusGus")]
    [InlineData("AC/DC", "AC/DC")]
    public void PrimaryArtist_IsTheFirstNameOfAJoinedCredit(string credit, string expected)
        => Assert.Equal(expected, LyricsSearchSelector.PrimaryArtist(credit));
}
