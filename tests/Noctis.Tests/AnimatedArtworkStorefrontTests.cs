using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #229: "Teenage Astronauts" (Thomas Dybdahl) has animated artwork on Apple Music, but
/// Noctis said "None of the 1 matching albums has animated artwork." Checked against Apple on
/// 2026-11-11: every request went to the US storefront, where the album is the US edition
/// 1735486680 ("videoArtwork":null); the loop is on the international edition 1728035192, which
/// de/gb/fr/nl/au carry and the US store does not (/us/album/…/1728035192 is a 404). The responses
/// below are those requests, trimmed to what the code reads.
/// </summary>
public class AnimatedArtworkStorefrontTests
{
    private const string UsEdition = "1735486680";
    private const string IntlEdition = "1728035192";
    private const string Master = "https://mvod.itunes.apple.com/itunes-assets/HLSVideo122/v4/19/f8/20/x/P823647828_default.m3u8";

    private static string Lookup(string id, string sf) =>
        $"{{\"resultCount\":1,\"results\":[{{\"wrapperType\":\"collection\",\"collectionType\":\"Album\",\"collectionId\":{id}," +
        $"\"artistName\":\"Thomas Dybdahl\",\"collectionName\":\"Teenage Astronauts\",\"trackCount\":9," +
        $"\"releaseDate\":\"2024-03-15T07:00:00Z\"," +
        $"\"collectionViewUrl\":\"https://music.apple.com/{sf}/album/teenage-astronauts/{id}?uo=4\"," +
        $"\"artworkUrl100\":\"https://is1-ssl.mzstatic.com/image/thumb/Music112/v4/a/b.jpg/100x100bb.jpg\"}}]}}";

    private const string NoResults = "{\"resultCount\":0,\"results\":[]}";

    private static string SearchPage(string id, string sf) =>
        $"<a href=\"https://music.apple.com/{sf}/album/teenage-astronauts/{id}\">";

    private const string PageWithoutLoop = "<script>{\"videoArtwork\":null,\"tallVideoArtwork\":null}</script>";

    private const string PageWithLoop =
        "<script>{\"videoArtwork\":{\"dictionary\":{\"motionDetailSquare\":{\"previewFrame\":{\"url\":" +
        "\"https://is1-ssl.mzstatic.com/image/thumb/Video/v4/p.png/{w}x{h}bb.{f}\"},\"video\":\"" + Master + "\"}}}," +
        "\"tallVideoArtwork\":null}</script>";

    private const string MasterPlaylist =
        "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,CODECS=\"hvc1.2.20000000.L123.B0\",RESOLUTION=1080x1080\n" +
        "https://mvod.itunes.apple.com/itunes-assets/HLSVideo122/v4/19/f8/20/x/v_1080.m3u8\n";

    internal readonly List<string> Asked = new();

    /// <summary>Apple as it answered: the US store knows only the US edition (and the iTunes
    /// search API found nothing there at all); de and gb know only the international one.</summary>
    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        lock (Asked) Asked.Add(url);
        string? body = null;
        foreach (var sf in new[] { "us", "de", "gb" })
        {
            var id = sf == "us" ? UsEdition : IntlEdition;
            if (url.StartsWith($"https://music.apple.com/{sf}/search", StringComparison.Ordinal))
                body = SearchPage(id, sf);
            else if (url.StartsWith("https://itunes.apple.com/search", StringComparison.Ordinal) && url.Contains($"country={sf}&"))
                body = sf == "us" ? NoResults : Lookup(id, sf);
            else if (url.StartsWith("https://itunes.apple.com/lookup", StringComparison.Ordinal) && url.Contains($"country={sf}&"))
                body = url.Contains(id, StringComparison.Ordinal) ? Lookup(id, sf) : NoResults;
            else if (url.StartsWith($"https://music.apple.com/{sf}/album/teenage-astronauts/{id}", StringComparison.Ordinal))
                body = sf == "us" ? PageWithoutLoop : PageWithLoop;
            if (body != null) break;
        }
        if (url == Master) body = MasterPlaylist;

        return body == null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }

    private ITunesArtworkService Service(string home)
        => new(new HttpClient(new AnimatedArtworkChooserTests.StubHandler(r => Task.FromResult(Route(r))))) { HomeStorefront = home };

    private static ITunesArtworkService.AnimatedArtworkQuery Query(bool other = false)
        => new("Teenage Astronauts", "Thomas Dybdahl", "Thomas Dybdahl", Typed: false) { OtherStorefronts = other };

    [Fact]
    public async Task TheHomeStorefrontIsSearchedBeforeTheUsOne()
    {
        var ranked = await Service("de").FindAnimatedArtworkCandidatesAsync(Query());

        Assert.Equal(new long[] { long.Parse(IntlEdition), long.Parse(UsEdition) },
            ranked.Select(r => r.Candidate.CollectionId).ToArray());
        Assert.StartsWith("https://music.apple.com/de/album/", ranked[0].Candidate.ViewUrl);
    }

    [Fact]
    public void OnlyTheStorefrontsNotAskedYetAreTheSecondStage()
    {
        Assert.Equal(new[] { "us" }, Service("us").AnimatedSearchStorefronts(false));
        Assert.Equal(new[] { "gb", "de" }, Service("us").AnimatedSearchStorefronts(true));
        Assert.Equal(new[] { "de", "us" }, Service("de").AnimatedSearchStorefronts(false));
        Assert.Equal(new[] { "gb" }, Service("de").AnimatedSearchStorefronts(true));
    }

    [Fact]
    public async Task TheChooserFindsTheLoopOnAnotherStorefrontsEdition()
    {
        var p = new TestPersistenceService();
        var track = new Track { Title = "Teenage Astronauts", Artist = "Thomas Dybdahl", AlbumArtist = "Thomas Dybdahl",
            Album = "Teenage Astronauts", FilePath = "C:\\nope.mp3" };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), p,
            new FakeAnimatedCoverService(), itunes: Service("us"));

        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        Assert.True(vm.HasAnimatedArtworkSearchResults, vm.AnimatedSearchStatus + " / " + vm.AnimatedSearchDetail);
        var result = Assert.Single(vm.AnimatedArtworkSearchResults);
        Assert.Equal(long.Parse(IntlEdition), result.Album.CollectionId);
        // The US edition was opened first and had no loop.
        Assert.Contains(Asked, u => u.StartsWith($"https://music.apple.com/us/album/teenage-astronauts/{UsEdition}"));
    }

    [Fact]
    public async Task TheAutomaticLookupFindsItToo()
    {
        var itunes = Service("us");
        string? downloaded = null;
        var bulk = new AnimatedArtworkBulkService(
            (q, ct) => itunes.FindAnimatedArtworkCandidatesAsync(q, 5, ct),
            (c, ct) => itunes.GetAnimatedArtworkAlbumAsync(c.ViewUrl, c, ct),
            (v, path, _) => { downloaded = v.Url; File.WriteAllText(path, "mp4"); return Task.FromResult(true); },
            new FakeAnimatedCoverService(), ledgerPath: null);
        var album = new[] { new Track { Title = "t", Album = "Teenage Astronauts", AlbumArtist = "Thomas Dybdahl",
            Artist = "Thomas Dybdahl", AlbumId = Guid.NewGuid(), SourceType = SourceType.Local } };
        var dest = Path.Combine(Path.GetTempPath(), $"noctis-229-{Guid.NewGuid():N}.mp4");
        try
        {
            var outcome = await bulk.FindAndDownloadAsync(album, new PoliteGate(TimeSpan.Zero), dest, preferMax: false, CancellationToken.None);

            Assert.Equal(AnimatedArtworkLookup.Saved, outcome);
            Assert.EndsWith("v_1080.m3u8", downloaded);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public void MissesRecordedByTheOlderLookupAreAskedAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-229-ledger-{Guid.NewGuid():N}.json");
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        var album = Guid.NewGuid();
        try
        {
            // A miss written by 1.6.9 (no generation in the file).
            File.WriteAllText(path, $"{{\"{album}\":{{\"CheckedUtc\":\"{now.AddDays(-1):O}\",\"Rank\":0}}}}");

            Assert.True(new BulkRunLedger(path, () => now).CheckedRecently(album, 0));
            var current = new BulkRunLedger(path, () => now) { Generation = AnimatedArtworkBulkService.LookupGeneration };
            Assert.False(current.CheckedRecently(album, 0));

            current.Record(album, 0);
            current.Save();
            Assert.True(new BulkRunLedger(path, () => now) { Generation = AnimatedArtworkBulkService.LookupGeneration }
                .CheckedRecently(album, 0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
