using System.Net.Http.Headers;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Artwork › Artist videos against Apple's live endpoints: the real lookup and
/// download, through the real fetcher, into a temporary data folder, with an HttpClient set up
/// as the app's (15 s timeout, the app's agent). Off by default (network); set
/// NOCTIS_LIVE_ARTIST_VIDEO=1 to run it (NOCTIS_LIVE_ARTIST_VIDEO_DIR keeps the data folder
/// there), and read the result in the test output.
/// </summary>
public class ArtistVideoLiveProbeTests
{
    private readonly ITestOutputHelper _out;

    public ArtistVideoLiveProbeTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("Chase Atlantic")]
    public async Task LookupAndDownload(string artist)
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_ARTIST_VIDEO") != "1")
            return;

        var dir = Environment.GetEnvironmentVariable("NOCTIS_LIVE_ARTIST_VIDEO_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Path.GetTempPath(), "NoctisArtistVideoProbe-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var itunes = new ITunesArtworkService(http) { HomeStorefront = "us" };
        var persistence = new PersistenceService(dir);
        var fetcher = ArtistVideoAutoFetcher.For(itunes, persistence, albums: null);

        var before = new HashSet<string>(DebugLog.Snapshot().Split('\n'));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var path = await fetcher.FetchAsync(LibraryService.ComputeArtistId(artist), artist,
            new AppSettings().AnimatedArtworkCacheMb * 1024L * 1024L, CancellationToken.None);

        _out.WriteLine($"{artist}: {path ?? "(none)"} in {sw.ElapsedMilliseconds} ms" +
                       (path != null ? $", {new FileInfo(path).Length} bytes" : string.Empty));
        foreach (var line in DebugLog.Snapshot().Split('\n').Where(l => l.Contains("[AnimatedArt]") && !before.Contains(l)))
            _out.WriteLine("   log: " + line);
        Assert.NotNull(path);
    }
}
