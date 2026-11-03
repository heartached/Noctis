using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #160: import play counts from Last.fm / ListenBrainz. Fetching against stubbed
/// replies shaped like the services' JSON (paging, now-playing, rate limits, errors), and the
/// matcher's release rules (same song on several albums).
/// </summary>
public class PlayCountImportTests
{
    private static Track T(string title, string artist, string album, int plays = 0, int year = 0, bool compilation = false)
        => new() { Title = title, Artist = artist, AlbumArtist = artist, Album = album, PlayCount = plays, Year = year,
                   IsCompilation = compilation, FilePath = $"C:/m/{artist}/{album}/{title}.flac" };

    // ── Matcher ──

    [Fact]
    public void Match_folds_case_accents_feat_and_remaster_decoration()
    {
        var song = T("Halo", "Beyoncé", "I Am... Sasha Fierce");
        var feat = T("Get Lucky", "Daft Punk feat. Pharrell Williams", "Random Access Memories");
        var r = PlayCountImportMatcher.Match(new[] { song, feat }, new[]
        {
            new SourcePlayCount("BEYONCE", "Halo - Remastered 2011", null, 3),
            new SourcePlayCount("Daft Punk", "Get Lucky (feat. Pharrell Williams)", "Random Access Memories", 5),
        });
        Assert.Equal(3, r.Counts[song]);
        Assert.Equal(5, r.Counts[feat]);
        Assert.Equal(0, r.UnmatchedEntries);
    }

    [Fact]
    public void Match_keeps_live_take_off_the_studio_song()
    {
        var studio = T("Song", "Band", "Album");
        var r = PlayCountImportMatcher.Match(new[] { studio }, new[]
        {
            new SourcePlayCount("Band", "Song (Live)", "Live at X", 4),
            new SourcePlayCount("Band", "Song", "Album", 2),
        });
        Assert.Equal(2, r.Counts[studio]);
        Assert.Equal(1, r.UnmatchedEntries);
        Assert.Equal(4, r.UnmatchedPlays);
    }

    [Fact]
    public void Match_splits_by_release_when_the_listen_names_the_album()
    {
        var original = T("Song", "Band", "First Album", year: 2001);
        var bestOf = T("Song", "Band", "Greatest Hits", year: 2010, compilation: true);
        var r = PlayCountImportMatcher.Match(new[] { original, bestOf }, new[]
        {
            new SourcePlayCount("Band", "Song", "First Album (Deluxe Edition)", 7),
            new SourcePlayCount("band", "song", "Greatest Hits", 3),
        });
        Assert.Equal(7, r.Counts[original]);
        Assert.Equal(3, r.Counts[bestOf]);
    }

    [Fact]
    public void Match_without_album_gives_all_plays_to_one_copy_not_each()
    {
        var original = T("Song", "Band", "First Album", year: 2001);
        var bestOf = T("Song", "Band", "Greatest Hits", year: 2010, compilation: true);
        var r = PlayCountImportMatcher.Match(new[] { bestOf, original }, new[]
        {
            new SourcePlayCount("Band", "Song", null, 6),
            new SourcePlayCount("Band", "Song", "Some Spotify Release", 2), // album not in the library
        });
        Assert.Single(r.Counts);
        Assert.Equal(8, r.Counts[original]); // non-compilation, earliest year
    }

    [Fact]
    public void Match_without_album_prefers_the_copy_played_most_in_Noctis()
    {
        var original = T("Song", "Band", "First Album", year: 2001);
        var bestOf = T("Song", "Band", "Greatest Hits", plays: 4, compilation: true);
        var r = PlayCountImportMatcher.Match(new[] { original, bestOf }, new[] { new SourcePlayCount("Band", "Song", null, 9) });
        Assert.Equal(9, r.Counts[bestOf]);
        Assert.False(r.Counts.ContainsKey(original));
    }

    [Fact]
    public void Apply_keeps_higher_by_default_and_replace_overwrites()
    {
        var a = T("A", "X", "Al", plays: 10);
        var b = T("B", "X", "Al", plays: 2);
        var src = new[] { new SourcePlayCount("X", "A", "Al", 4), new SourcePlayCount("X", "B", "Al", 5) };

        var changed = PlayCountImportMatcher.Apply(PlayCountImportMatcher.Match(new[] { a, b }, src), PlayCountImportMode.KeepHigher);
        Assert.Equal(10, a.PlayCount);
        Assert.Equal(5, b.PlayCount);
        Assert.Equal(new[] { b }, changed);

        changed = PlayCountImportMatcher.Apply(PlayCountImportMatcher.Match(new[] { a, b }, src), PlayCountImportMode.Replace);
        Assert.Equal(4, a.PlayCount);
        Assert.Equal(new[] { a }, changed);
    }

    [Fact]
    public void Apply_leaves_last_played_alone()
    {
        var a = T("A", "X", "Al");
        PlayCountImportMatcher.Apply(PlayCountImportMatcher.Match(new[] { a }, new[] { new SourcePlayCount("X", "A", null, 3) }),
            PlayCountImportMode.KeepHigher);
        Assert.Equal(3, a.PlayCount);
        Assert.Null(a.LastPlayed);
    }

    [Fact]
    public void Match_skips_external_tracks()
    {
        var ext = T("A", "X", "Al");
        ext.IsExternal = true;
        var r = PlayCountImportMatcher.Match(new[] { ext }, new[] { new SourcePlayCount("X", "A", null, 3) });
        Assert.Empty(r.Counts);
    }

    // ── Last.fm fetch ──

    private static string LastFmPage(int page, int totalPages, params string[] tracks)
        => "{\"recenttracks\":{\"track\":[" + string.Join(",", tracks) + "],\"@attr\":{\"user\":\"u\",\"page\":\"" + page +
           "\",\"perPage\":\"200\",\"totalPages\":\"" + totalPages + "\",\"total\":\"" + (totalPages * 2) + "\"}}}";

    private static string Scrobble(string artist, string title, string album, long uts)
        => "{\"artist\":{\"mbid\":\"\",\"#text\":\"" + artist + "\"},\"name\":\"" + title + "\",\"album\":{\"mbid\":\"\",\"#text\":\"" +
           album + "\"},\"date\":{\"uts\":\"" + uts + "\",\"#text\":\"x\"}}";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static PlayCountHistoryFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond, List<TimeSpan>? delays = null)
        => new(new HttpClient(new AnimatedArtworkChooserTests.StubHandler(r => Task.FromResult(respond(r)))),
            (d, ct) => { delays?.Add(d); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

    [Fact]
    public async Task LastFm_pages_pins_the_window_and_skips_now_playing()
    {
        var urls = new ConcurrentQueue<string>();
        var nowPlaying = "{\"artist\":{\"#text\":\"Band\"},\"name\":\"Song\",\"album\":{\"#text\":\"Al\"},\"@attr\":{\"nowplaying\":\"true\"}}";
        var delays = new List<TimeSpan>();
        var f = Fetcher(r =>
        {
            var url = r.RequestUri!.ToString();
            urls.Enqueue(url);
            Assert.Equal(HttpMethod.Get, r.Method);
            return url.Contains("page=1&")
                ? Json(LastFmPage(1, 2, nowPlaying, Scrobble("Band", "Song", "Al", 5), Scrobble("Band", "Song", "Al", 4)))
                : Json(LastFmPage(2, 2, Scrobble("Band", "Song", "", 3)));
        }, delays);

        var progress = new List<PlayCountFetchProgress>();
        var counts = await f.FetchLastFmAsync("u", new SyncProgress<PlayCountFetchProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(2, urls.Count);
        Assert.All(urls, u => Assert.Contains("method=user.getrecenttracks", u));
        Assert.All(urls, u => Assert.Contains("to=1800000000", u));
        Assert.All(urls, u => Assert.Contains("limit=200", u));
        Assert.Equal(2, counts.Single(c => c.Album == "Al").Count);
        Assert.Equal(1, counts.Single(c => c.Album == null).Count);
        Assert.Equal(3, progress[^1].Listens);
        Assert.Equal(new[] { PlayCountHistoryFetcher.PagePause }, delays);
    }

    [Fact]
    public async Task LastFm_single_track_object_and_string_page_counts_parse()
    {
        var f = Fetcher(_ => Json("{\"recenttracks\":{\"track\":" + Scrobble("A", "B", "C", 1) +
                                  ",\"@attr\":{\"totalPages\":\"1\",\"total\":\"1\"}}}"));
        var counts = await f.FetchLastFmAsync("u", null, CancellationToken.None);
        Assert.Equal(new SourcePlayCount("A", "B", "C", 1), Assert.Single(counts));
    }

    [Fact]
    public async Task LastFm_retries_rate_limit_and_server_errors()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var f = Fetcher(_ => ++calls switch
        {
            1 => Json("{\"error\":29,\"message\":\"Rate Limit Exceeded\"}"),
            2 => Json("busy", HttpStatusCode.BadGateway),
            _ => Json(LastFmPage(1, 1, Scrobble("A", "B", "C", 1))),
        }, delays);
        var counts = await f.FetchLastFmAsync("u", null, CancellationToken.None);
        Assert.Single(counts);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task LastFm_gives_up_after_max_attempts_as_unreachable()
    {
        var calls = 0;
        var f = Fetcher(_ => { calls++; throw new HttpRequestException("offline"); });
        var ex = await Assert.ThrowsAsync<PlayCountImportException>(() => f.FetchLastFmAsync("u", null, CancellationToken.None));
        Assert.Equal(PlayCountImportError.Unreachable, ex.Kind);
        Assert.Equal(PlayCountHistoryFetcher.MaxAttempts, calls);
    }

    [Theory]
    [InlineData(6, PlayCountImportError.UserNotFound)]
    [InlineData(17, PlayCountImportError.PrivateHistory)]
    public async Task LastFm_user_errors_are_reported(int code, PlayCountImportError kind)
    {
        var f = Fetcher(_ => Json("{\"error\":" + code + ",\"message\":\"m\"}", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<PlayCountImportException>(() => f.FetchLastFmAsync("u", null, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task LastFm_cancel_stops_between_pages()
    {
        using var cts = new CancellationTokenSource();
        var f = Fetcher(_ =>
        {
            cts.Cancel();
            return Json(LastFmPage(1, 5, Scrobble("A", "B", "C", 1)));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.FetchLastFmAsync("u", null, cts.Token));
    }

    // ── ListenBrainz fetch ──

    private static string Listen(long ts, string artist, string title, string? release)
        => "{\"listened_at\":" + ts + ",\"track_metadata\":{\"artist_name\":\"" + artist + "\",\"track_name\":\"" + title + "\"" +
           (release == null ? "" : ",\"release_name\":\"" + release + "\"") + ",\"additional_info\":{}}}";

    private static string Listens(params string[] items)
        => "{\"payload\":{\"count\":" + items.Length + ",\"user_id\":\"u\",\"listens\":[" + string.Join(",", items) + "]}}";

    [Fact]
    public async Task ListenBrainz_pages_backwards_without_losing_listens_that_share_a_second()
    {
        var urls = new List<string>();
        var f = Fetcher(r =>
        {
            var url = r.RequestUri!.PathAndQuery;
            urls.Add(url);
            if (url.EndsWith("/listen-count")) return Json("{\"payload\":{\"count\":4}}");
            if (!url.Contains("max_ts"))
                return Json(Listens(Listen(300, "A", "One", "Al"), Listen(200, "A", "Two", null)));
            if (url.Contains("max_ts=201"))
                // Same second as the boundary: "Two" again (already read) and "Three" (new).
                return Json(Listens(Listen(200, "A", "Two", null), Listen(200, "A", "Three", null), Listen(100, "A", "One", "Al")));
            if (url.Contains("max_ts=101")) return Json(Listens(Listen(100, "A", "One", "Al")));
            return Json(Listens());
        });
        var counts = await f.FetchListenBrainzAsync("https://lb.example/1/", "us er", "tok", null, CancellationToken.None);

        Assert.Equal("/1/user/us%20er/listen-count", urls[0]);
        Assert.Equal(2, counts.Single(c => c.Title == "One").Count);
        Assert.Equal(1, counts.Single(c => c.Title == "Two").Count);
        Assert.Equal(1, counts.Single(c => c.Title == "Three").Count);
        Assert.Null(counts.Single(c => c.Title == "Two").Album);
        Assert.Contains(urls, u => u.Contains("count=1000"));
    }

    [Fact]
    public async Task ListenBrainz_sends_token_only_on_reads_and_waits_out_the_rate_limit()
    {
        var delays = new List<TimeSpan>();
        var f = Fetcher(r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("Token", r.Headers.Authorization?.Scheme);
            var resp = r.RequestUri!.PathAndQuery.Contains("max_ts") ? Json(Listens()) : Json(Listens(Listen(5, "A", "B", "C")));
            resp.Headers.Add("X-RateLimit-Remaining", "0");
            resp.Headers.Add("X-RateLimit-Reset-In", "3");
            return resp;
        }, delays);
        await f.FetchListenBrainzAsync(ListenBrainzService.DefaultApiUrl, "u", "tok", null, CancellationToken.None);
        Assert.Contains(TimeSpan.FromSeconds(3), delays);
    }

    [Fact]
    public async Task ListenBrainz_unknown_user_is_reported()
    {
        var f = Fetcher(_ => Json("{\"code\":404,\"error\":\"Cannot find user\"}", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<PlayCountImportException>(() =>
            f.FetchListenBrainzAsync(ListenBrainzService.DefaultApiUrl, "nobody", null, null, CancellationToken.None));
        Assert.Equal(PlayCountImportError.UserNotFound, ex.Kind);
    }

    [Fact]
    public async Task ListenBrainz_missing_listen_count_still_imports()
    {
        var f = Fetcher(r => r.RequestUri!.PathAndQuery.EndsWith("/listen-count")
            ? Json("nope", HttpStatusCode.MethodNotAllowed)
            : r.RequestUri.PathAndQuery.Contains("max_ts") ? Json(Listens()) : Json(Listens(Listen(5, "A", "B", "C"))));
        var counts = await f.FetchListenBrainzAsync("http://koito.lan/apis/listenbrainz", "u", "tok", null, CancellationToken.None);
        Assert.Single(counts);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
