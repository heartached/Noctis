using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DeezerEnrichmentTests
{
    private const string TrackJson = """
    {
      "id": 142986206,
      "title": "Lucid Dreams",
      "isrc": "USUM71808193",
      "track_position": 8,
      "disk_number": 1,
      "bpm": 83.9,
      "artist": { "name": "Juice WRLD" },
      "album": { "id": 14801948, "title": "Goodbye & Good Riddance", "release_date": "2018-05-23" },
      "contributors": [ { "name": "Juice WRLD", "role": "Main" } ]
    }
    """;

    private const string AlbumJson = """
    {
      "id": 14801948,
      "title": "Goodbye & Good Riddance",
      "nb_tracks": 17,
      "release_date": "2018-05-23",
      "artist": { "name": "Juice WRLD" },
      "genres": { "data": [ { "name": "Rap/Hip Hop" } ] }
    }
    """;

    [Fact]
    public void ParseTrack_ExtractsPerTrackFields()
    {
        var t = DeezerApi.ParseTrack(TrackJson);
        Assert.NotNull(t);
        Assert.Equal(14801948, t!.AlbumId);
        Assert.Equal("Lucid Dreams", t.Title);
        Assert.Equal("USUM71808193", t.Isrc);
        Assert.Equal(8, t.TrackNumber);
        Assert.Equal(1, t.DiscNumber);
        Assert.Equal(84, t.Bpm);            // 83.9 rounds to 84
        Assert.Equal("Juice WRLD", t.AlbumArtist);
        Assert.Equal(2018, t.AlbumYear);    // from the nested album.release_date (original date)
    }

    [Fact]
    public void ParseAlbum_ExtractsAlbumFields()
    {
        var a = DeezerApi.ParseAlbum(AlbumJson);
        Assert.NotNull(a);
        Assert.Equal("Goodbye & Good Riddance", a!.Title);
        Assert.Equal(17, a.TrackCount);
        Assert.Equal(2018, a.Year);
        Assert.Equal("Rap/Hip Hop", a.Genre);
        Assert.Equal("Juice WRLD", a.AlbumArtist);
    }

    [Fact]
    public void ParseTrack_Malformed_ReturnsNull()
    {
        Assert.Null(DeezerApi.ParseTrack("not json"));
        Assert.Null(DeezerApi.ParseTrack("{}"));
    }

    // Deezer localises genre names from the request locale and falls back to IP geolocation when
    // no Accept-Language is sent (a Spanish user got "Alternativo" while the genre picker says
    // "Alternative"). Every Deezer request must pin English so genres match the picker.
    [Fact]
    public async Task EnrichAsync_PinsEnglishAcceptLanguage_OnEveryDeezerRequest()
    {
        var handler = new RecordingHandler();
        var svc = new DeezerMetadataService(new HttpClient(handler));

        var hit = await svc.EnrichAsync("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance");

        Assert.NotNull(hit);
        Assert.Equal("Rap/Hip Hop", hit!.Genre);
        Assert.Equal(3, handler.Requests.Count); // search, track, album
        Assert.All(handler.Requests, r =>
            Assert.Equal("en", Assert.Single(r.Headers.AcceptLanguage).Value));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            string body = path.StartsWith("/search") ? """{ "data": [ { "id": 142986206 } ] }"""
                : path.StartsWith("/track/") ? TrackJson
                : AlbumJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
