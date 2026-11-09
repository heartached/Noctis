using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #219: Indian film songs are often tagged "Title (From "Movie")" and credited
/// "Singer feat. Other". LRCLIB's /search has nothing under those (live 11-09:
/// "Pramadavanam (From "His Highness Abdullah")" → 0 results, "Pramadavanam" → its synced lyrics;
/// "Entammede Jimikki Kammal - From "Velipadinte Pusthakam"" → 0, without the suffix → 18;
/// artist "Sid Sriram feat. Yuvan Shankar Raja" → 0, "Sid Sriram" → 20). A miss is asked once more
/// with those parts left out; the answer is still validated against the song.
/// </summary>
public class LrcLibLookupFallbackTests
{
    private sealed class FakeLrcLib : ILrcLibService
    {
        public readonly List<(string Artist, string Title)> Searches = new();
        public readonly List<(string Artist, string Title)> Gets = new();
        public Func<string, string, List<LrcLibResult>> Search = (_, _) => new();

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Gets.Add((artist, trackName));
            return Task.FromResult<LrcLibResult?>(null);
        }

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        {
            Searches.Add((artist, trackName));
            return Task.FromResult(Search(artist, trackName));
        }
    }

    private static LrcLibResult Hit(string title, string artist, double duration = 347) => new()
    {
        TrackName = title, ArtistName = artist, Duration = duration,
        SyncedLyrics = "[00:01.00]Pramadavanam veendum", PlainLyrics = "Pramadavanam veendum",
    };

    [Theory]
    [InlineData("Pramadavanam (From \"His Highness Abdullah\")", "Pramadavanam")]
    [InlineData("Entammede Jimikki Kammal - From \"Velipadinte Pusthakam\"", "Entammede Jimikki Kammal")]
    [InlineData("Malare [From \"Premam\"]", "Malare")]
    [InlineData("Kannaana Kanney (feat. D. Imman)", "Kannaana Kanney")]
    [InlineData("Uyiril Thodum (From \"Kumbalangi Nights\") (feat. Anne Amie)", "Uyiril Thodum")]
    public void SearchTitle_DropsMovieSuffixAndFeaturedCredits(string title, string expected)
        => Assert.Equal(expected, LrcLibLookup.SimplifiedQuery("A", title).Title);

    [Theory]
    [InlineData("Sid Sriram feat. Yuvan Shankar Raja", "Sid Sriram")]
    [InlineData("Sid Sriram ft. Yuvan", "Sid Sriram")]
    [InlineData("Anirudh Ravichander, Jonita Gandhi", "Anirudh Ravichander, Jonita Gandhi")]
    public void SearchArtist_DropsFeaturedCredits(string artist, string expected)
        => Assert.Equal(expected, LrcLibLookup.SimplifiedQuery(artist, "Song").Artist);

    [Theory]
    [InlineData("From Me to You")]
    [InlineData("Letters From Home")]
    [InlineData("Malare")]
    public void PlainTitles_AreLeftAlone(string title)
        => Assert.Equal(title, LrcLibLookup.SimplifiedQuery("A", title).Title);

    [Fact]
    public async Task MovieSuffixMiss_IsAskedAgainWithoutIt_AndTheHitIsValidated()
    {
        var lrcLib = new FakeLrcLib
        {
            Search = (_, t) => t == "Pramadavanam"
                ? new List<LrcLibResult> { Hit("Pramadavanam (From ''His Highness Abdullah'')", "K. J. Yesudas") }
                : new List<LrcLibResult>(),
        };

        var r = await LrcLibLookup.FetchAsync(lrcLib, "K. J. Yesudas", "Pramadavanam (From \"His Highness Abdullah\")", 347, CancellationToken.None);

        Assert.NotNull(r);
        Assert.True(r!.HasSyncedLyrics);
        Assert.Equal(new[] { ("K. J. Yesudas", "Pramadavanam (From \"His Highness Abdullah\")"), ("K. J. Yesudas", "Pramadavanam") }, lrcLib.Searches);
    }

    [Fact]
    public async Task FeaturedArtistMiss_IsAskedAgainWithTheMainArtist()
    {
        var lrcLib = new FakeLrcLib
        {
            Search = (a, _) => a == "Sid Sriram"
                ? new List<LrcLibResult> { Hit("Thaarame Thaarame", "Sid Sriram", 228) }
                : new List<LrcLibResult>(),
        };

        var r = await LrcLibLookup.FetchAsync(lrcLib, "Sid Sriram feat. Yuvan Shankar Raja", "Thaarame Thaarame", 228, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal(2, lrcLib.Searches.Count);
    }

    [Fact]
    public async Task FallbackHit_ForAnotherSong_IsRejected()
    {
        // Wrong length: another recording, not this song.
        var lrcLib = new FakeLrcLib
        {
            Search = (_, t) => t == "Pramadavanam" ? new List<LrcLibResult> { Hit("Pramadavanam", "K. J. Yesudas", 200) } : new(),
        };
        var r = await LrcLibLookup.FetchAsync(lrcLib, "K. J. Yesudas", "Pramadavanam (From \"His Highness Abdullah\")", 347, CancellationToken.None);
        Assert.Null(r);
    }

    [Fact]
    public async Task PlainTitleMiss_CostsNoExtraRequest()
    {
        var lrcLib = new FakeLrcLib();
        var r = await LrcLibLookup.FetchAsync(lrcLib, "M. G. Sreekumar", "Kandu Kandu", 300, CancellationToken.None);
        Assert.Null(r);
        Assert.Single(lrcLib.Searches);
    }

    [Fact]
    public async Task FirstSearchHit_IsNotAskedAgain()
    {
        var lrcLib = new FakeLrcLib
        {
            Search = (_, _) => new List<LrcLibResult> { Hit("Malare (From \"Premam\")", "Vijay Yesudas", 316) },
        };
        var r = await LrcLibLookup.FetchAsync(lrcLib, "Vijay Yesudas", "Malare (From \"Premam\")", 316, CancellationToken.None);
        Assert.NotNull(r);
        Assert.Single(lrcLib.Searches);
    }
}
