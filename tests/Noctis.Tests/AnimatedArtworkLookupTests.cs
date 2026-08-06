using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression cover for the Apple Music animated-cover lookup. Two things went wrong in the
/// field: Apple renamed the JSON key the scraper keys off, and the search accepted whichever
/// candidate happened to yield a video — even when it was a different album entirely.
/// </summary>
public class AnimatedArtworkLookupTests
{
    // Trimmed from the live page for album 1526575291 (Juice WRLD — Legends Never Die),
    // captured 2026-08-02. The old scraper looked for "videoUrl", which no longer exists:
    // the stream now hangs off videoArtwork/tallVideoArtwork -> dictionary -> "video".
    private const string SquareUrl =
        "https://mvod.itunes.apple.com/itunes-assets/HLSMusic116/v4/08/e8/c7/" +
        "08e8c7ae-7ddd-4538-eb1f-0c5f42c2d399/P359221896_default.m3u8";

    private const string TallUrl =
        "https://mvod.itunes.apple.com/itunes-assets/HLSMusic125/v4/f1/06/a2/" +
        "f106a238-e9e3-bac6-7328-f265e5693f00/P359221696_default.m3u8";

    private const string CurrentAppleJson =
        "{\"videoArtwork\":{\"dictionary\":{\"motionDetailSquare\":{\"previewFrame\":" +
        "{\"bgColor\":\"bf7180\",\"height\":3840,\"url\":\"https://is1-ssl.mzstatic.com/image/thumb/" +
        "Video114/v4/70/01/43/preview.png/{w}x{h}bb.{f}\",\"width\":3840}," +
        "\"video\":\"" + SquareUrl + "\"}},\"cropStyle\":\"cc\"}," +
        "\"tallVideoArtwork\":{\"dictionary\":{\"motionDetailTall\":{\"previewFrame\":" +
        "{\"bgColor\":\"442630\",\"height\":2732,\"url\":\"https://is1-ssl.mzstatic.com/image/thumb/" +
        "Video124/v4/41/e7/2d/preview.png/{w}x{h}bb.{f}\",\"width\":2048}," +
        "\"video\":\"" + TallUrl + "\"}},\"cropStyle\":\"bb\"},\"trackCount\":22}";

    [Fact]
    public void FindsAnimatedStreamsInApplesCurrentVideoArtworkJson()
    {
        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(CurrentAppleJson);

        Assert.Contains(SquareUrl, urls);
        Assert.Contains(TallUrl, urls);
    }

    [Fact]
    public void IgnoresMediaThatIsNotTheAlbumsAnimatedArtwork()
    {
        // A music-video preview shares the mvod host, so the host-only fallback cannot tell it
        // apart from the cover loop. Once the structured artwork entries are found, only those
        // may be offered — otherwise the dialog hands the user a trailer as an animated cover.
        const string strayPreview =
            "https://mvod.itunes.apple.com/itunes-assets/HLSVideo999/v4/aa/bb/cc/music-video.m3u8";
        var html = "<div>" + CurrentAppleJson + "</div>" +
                   "<video src=\"" + strayPreview + "\"></video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html);

        Assert.Contains(SquareUrl, urls);
        Assert.DoesNotContain(strayPreview, urls);
    }

    [Fact]
    public void StillReadsAnimatedStreamFromTheMarkupWhenTheJsonIsAbsent()
    {
        // The rendered element carries the same stream; keep it working as a fallback so a
        // future JSON reshuffle degrades instead of going dark.
        var html = "<amp-ambient-video class=\"editorial-video\" src=\"" + SquareUrl + "\"></amp-ambient-video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html);

        Assert.Contains(SquareUrl, urls);
    }

    // ── the <amp-ambient-video> element ───────────────────────────────────────
    // Apple has already renamed the JSON key once ("videoUrl" -> "video"), and when that
    // happened everything fell through to the host-only sweep, which cannot tell a cover loop
    // from a music-video preview. The rendered element carries the same stream and is precise,
    // so it sits between the two as a second structured pass. Route confirmed by Ben Dodson,
    // who maintains the Apple Music Artwork Finder.

    [Fact]
    public void ReadsTheAmbientVideoElementWithoutTrustingTheHostOnlySweep()
    {
        // The JSON is gone, exactly as it would be after another rename. A music-video preview
        // sits on the same page: the loose sweep would happily offer it as the cover.
        const string strayPreview =
            "https://mvod.itunes.apple.com/itunes-assets/HLSVideo999/v4/aa/bb/cc/music-video.m3u8";
        var html = "<amp-ambient-video class=\"editorial-video\" src=\"" + SquareUrl + "\"></amp-ambient-video>" +
                   "<video src=\"" + strayPreview + "\"></video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html);

        Assert.Contains(SquareUrl, urls);
        Assert.DoesNotContain(strayPreview, urls);
    }

    [Fact]
    public void DecodesHtmlEntitiesInTheAmbientVideoSource()
    {
        // It is markup, so the attribute is HTML-encoded. An &amp; left as-is produces a URL
        // that 404s.
        var encoded = SquareUrl + "?a=1&amp;b=2";
        var html = "<amp-ambient-video src=\"" + encoded + "\"></amp-ambient-video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html);

        Assert.Equal(SquareUrl + "?a=1&b=2", Assert.Single(urls));
    }

    [Fact]
    public void ResolvesARelativeAmbientVideoSourceAgainstThePageUrl()
    {
        // Apple serves an absolute src today, but a protocol-relative one is a normal thing
        // for a CDN to switch to, and dropping it would silently kill the feature.
        const string albumPage = "https://music.apple.com/us/album/legends-never-die/1526575291";
        var html = "<amp-ambient-video src=\"//mvod.itunes.apple.com/itunes-assets/x/y.m3u8\"></amp-ambient-video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html, albumPage);

        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/x/y.m3u8", Assert.Single(urls));
    }

    [Fact]
    public void StillPrefersTheStructuredJsonWhenBothArePresent()
    {
        // The live page carries both. Neither may be dropped, and neither may open the door
        // to the loose sweep.
        const string ambientOnly =
            "https://mvod.itunes.apple.com/itunes-assets/HLSMusic999/v4/zz/ambient.m3u8";
        var html = CurrentAppleJson +
                   "<amp-ambient-video src=\"" + ambientOnly + "\"></amp-ambient-video>";

        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(html);

        Assert.Contains(SquareUrl, urls);
        Assert.Contains(ambientOnly, urls);
    }

    [Fact]
    public void AcceptsTheAlbumThatWasSearchedFor()
    {
        Assert.True(ITunesArtworkService.IsLikelySameAlbum(
            "Legends Never Die", "Juice WRLD", "Legends Never Die", "Juice WRLD"));
    }

    [Fact]
    public void AcceptsTheSameAlbumUnderADifferentEditionSuffix()
    {
        // Local tags routinely carry an edition the store spells differently.
        Assert.True(ITunesArtworkService.IsLikelySameAlbum(
            "Legends Never Die", "Juice WRLD", "Legends Never Die (Video Version)", "Juice WRLD"));

        Assert.True(ITunesArtworkService.IsLikelySameAlbum(
            "Nothing Was the Same", "Drake", "Nothing Was the Same (Deluxe)", "Drake"));
    }

    [Fact]
    public void RejectsADifferentAlbumByTheSameArtist()
    {
        // The shipped behaviour: searching Drake's "Nothing Was the Same (Deluxe)" offered
        // Take Care's animated cover, because it was simply the first candidate with a video.
        Assert.False(ITunesArtworkService.IsLikelySameAlbum(
            "Take Care (Deluxe Version)", "Drake", "Nothing Was the Same (Deluxe)", "Drake"));
    }

    [Fact]
    public void RejectsARerecordingThatHasItsOwnArtwork()
    {
        // "(Taylor's Version)" is not an edition wrapper — it is a different release with a
        // different cover, so it must not be normalised away.
        Assert.False(ITunesArtworkService.IsLikelySameAlbum(
            "1989 (Taylor's Version) [Deluxe]", "Taylor Swift", "1989 (Deluxe Edition)", "Taylor Swift"));
    }

    [Fact]
    public void RejectsCoverAndKaraokeRecordsWithTheSameTitle()
    {
        Assert.False(ITunesArtworkService.IsLikelySameAlbum(
            "Piano Dreamers Play Chase Atlantic (Instrumental)", "Piano Dreamers",
            "Chase Atlantic", "Chase Atlantic"));

        Assert.False(ITunesArtworkService.IsLikelySameAlbum(
            "Chase Atlantic", "The Cat and Owl", "Chase Atlantic", "Chase Atlantic"));
    }

    [Fact]
    public void RejectsCandidatesWithNothingToCompare()
    {
        Assert.False(ITunesArtworkService.IsLikelySameAlbum("", "Juice WRLD", "Legends Never Die", "Juice WRLD"));
        Assert.False(ITunesArtworkService.IsLikelySameAlbum("Legends Never Die", "Juice WRLD", null, "Juice WRLD"));
    }
}
