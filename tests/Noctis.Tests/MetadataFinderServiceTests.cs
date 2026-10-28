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

    private static Track RefFull() => new() { Title = "ref_full", Artist = "Unknown Artist", Album = "gh127" };

    [Fact]
    public async Task NothingFound_ReturnsEmpty()
    {
        // What both services answer for the owner's synthetic "ref_full" tone (checked live 10-27).
        var handler = new Handler(deezer: """{"data":[],"total":0}""", mb: """{"count":0,"offset":0,"recordings":[]}""");
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));

        Assert.Empty(await svc.IdentifyAsync(RefFull()));
        Assert.Contains(handler.Hosts, h => h == "musicbrainz.org");
    }

    [Fact]
    public async Task DeezerTimeout_IsNotACancel_MusicBrainzStillAnswers()
    {
        // HttpClient's timeout throws TaskCanceledException; it used to be rethrown as the
        // caller's cancel and ended the whole Find Metadata run as "Cancelled".
        var handler = new Handler(deezer: "", mb: MbJson) { DeezerThrows = new TaskCanceledException("HttpClient.Timeout") };
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));

        var hits = await svc.IdentifyAsync(Lucid());

        Assert.Equal("Lucid Dreams", hits[0].Title);
    }

    [Fact]
    public async Task NothingFound_BecauseMusicBrainzFailed_Throws()
    {
        var handler = new Handler(deezer: """{"data":[]}""", mb: "") { MbStatus = HttpStatusCode.BadGateway };
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => svc.IdentifyAsync(RefFull()));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Fact]
    public async Task CallersCancel_StillCancels()
    {
        var handler = new Handler(deezer: """{"data":[]}""", mb: MbJson);
        var svc = new MetadataFinderService(new HttpClient(handler), () => new AppSettings(), new DeezerMetadataService(new HttpClient(handler)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.IdentifyAsync(Lucid(), cts.Token));
    }

    private sealed class Handler(string deezer, string mb) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = new();
        public string MusicBrainzUserAgent { get; private set; } = "";
        public Exception? DeezerThrows { get; init; }
        public HttpStatusCode MbStatus { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            lock (Hosts) Hosts.Add(host);
            if (host == "musicbrainz.org") MusicBrainzUserAgent = request.Headers.UserAgent.ToString();
            if (host != "musicbrainz.org" && DeezerThrows is not null) return Task.FromException<HttpResponseMessage>(DeezerThrows);
            var body = host == "musicbrainz.org" ? mb : deezer;
            var status = host == "musicbrainz.org" ? MbStatus : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
