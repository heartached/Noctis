using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Search metadata revamp (owner 10-08): the finder's fallbacks and confidences.
public class MetadataFinderServiceTests
{
    private static Track Lucid() => new() { Title = "Lucid Dreams", Artist = "Juice WRLD", Album = "Goodbye & Good Riddance" };

    private const string MbJson = """
    {"recordings":[
      {"id":"a","score":100,"title":"Lucid Dreams","artist-credit":[{"name":"Juice WRLD"}],"releases":[{"title":"Goodbye & Good Riddance","date":"2018-05-23"}]},
      {"id":"b","score":100,"title":"Totally Unrelated","artist-credit":[{"name":"Various Artists"}],"releases":[{"title":"Hits 2018"}]}
    ]}
    """;

    [Fact]
    public async Task WeakDeezerHit_StillAsksMusicBrainz_AndRanksBySimilarity()
    {
        var handler = new Handler(deezer: """{"data":[{"id":1,"title":"Lucid","artist":{"name":"Someone Else"},"album":{"title":"X"}}]}""", mb: MbJson);
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));

        var hits = await svc.IdentifyAsync(Lucid());

        Assert.Contains(handler.Hosts, h => h == "musicbrainz.org");
        Assert.Equal("MusicBrainz", hits[0].Source);
        Assert.Equal("Lucid Dreams", hits[0].Title);
        // MusicBrainz's search score (100) is relevance, not similarity.
        Assert.True(hits.Single(h => h.Title == "Totally Unrelated").Confidence < 0.5);
        Assert.Equal(hits.OrderByDescending(h => h.Confidence).Select(h => h.Title), hits.Select(h => h.Title));
    }

    [Fact]
    public async Task StrongDeezerHit_SkipsMusicBrainz()
    {
        var handler = new Handler(deezer: """{"data":[{"id":1,"title":"Lucid Dreams","artist":{"name":"Juice WRLD"},"album":{"title":"Goodbye & Good Riddance"}}]}""", mb: MbJson);
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));

        var hits = await svc.IdentifyAsync(Lucid());

        Assert.Equal("Deezer", Assert.Single(hits).Source);
        Assert.DoesNotContain(handler.Hosts, h => h == "musicbrainz.org");
    }

    [Fact]
    public async Task MusicBrainzRequests_CarryAContactUserAgent()
    {
        var handler = new Handler(deezer: """{"data":[]}""", mb: MbJson);
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings { DeezerEnabled = false }, new DeezerMetadataService(new HttpClient(handler)));

        await svc.IdentifyAsync(Lucid());

        Assert.Contains("( https://github.com/heartached/Noctis )", handler.MusicBrainzUserAgent);
    }

    private sealed class Handler(string deezer, string mb) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = new();
        public string MusicBrainzUserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            lock (Hosts) Hosts.Add(host);
            if (host == "musicbrainz.org") MusicBrainzUserAgent = request.Headers.UserAgent.ToString();
            var body = host == "musicbrainz.org" ? mb : deezer;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
