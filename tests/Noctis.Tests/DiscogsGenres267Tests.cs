using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #267 "Discogs Genre Support": an album's Discogs styles (or genres) as its genre tag.
/// Matching against a Discogs search answer with decoys ("Debut + Post", "Debut Live", a
/// tribute), the options, 429 / rate-limit handling through a fake handler, and the write
/// through the metadata editor's tag writer on real files. No network.
/// </summary>
public class DiscogsGenres267Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-discogs-" + Guid.NewGuid().ToString("N"));

    public DiscogsGenres267Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string DebutJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", "bjork-debut-master.json"));

    private static readonly DiscogsAlbumQuery Debut = new("Björk", "Debut", 1993);

    private static DiscogsAlbumMatch Match(IReadOnlyList<string> genres, IReadOnlyList<string> styles)
        => new(1, "master", "A", "B", 2000, genres, styles, "https://www.discogs.com/master/1");

    private static readonly DiscogsAlbumMatch DebutMatch = Match(
        new[] { "Electronic", "Rock" }, new[] { "House", "Synth-pop", "Experimental", "Deep House" });

    // ── Matching ──

    [Fact]
    public void Parse_ReadsResultsSplittingArtistFromTitle()
    {
        var results = DiscogsGenreService.ParseSearch(DebutJson, "master");

        Assert.Equal(4, results.Count);
        var debut = results.Single(r => r.Id == 34486);
        Assert.Equal("Björk", debut.Artist);
        Assert.Equal("Debut", debut.Title);
        Assert.Equal(1993, debut.Year);
        Assert.Equal(new[] { "Electronic", "Rock" }, debut.Genres);
        Assert.Equal(new[] { "House", "Synth-pop", "Experimental", "Deep House" }, debut.Styles);
    }

    [Fact]
    public void PickBest_TakesTheExactAlbum_NotTheDecoysListedFirst()
    {
        var results = DiscogsGenreService.ParseSearch(DebutJson, "master");

        var match = DiscogsGenreService.PickBest(results, Debut, "master");

        Assert.NotNull(match);
        Assert.Equal(34486, match!.Id);
        Assert.Equal("master", match.Kind);
        Assert.Equal("https://www.discogs.com/master/34486-Bj%C3%B6rk-Debut", match.Url);
    }

    [Theory]
    [InlineData("Debut (Deluxe Edition)", "Björk")]
    [InlineData("Debut [Remastered]", "Bjork")] // no diacritic in the local tag
    [InlineData("debut", "BJÖRK")]
    public void PickBest_IgnoresOurEditionWords_CaseAndDiacritics(string album, string artist)
    {
        var results = DiscogsGenreService.ParseSearch(DebutJson, "master");

        var match = DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery(artist, album), "master");

        Assert.Equal(34486, match?.Id);
    }

    [Theory]
    [InlineData("Post", "Björk")]        // only "Debut + Post" shares a word: a weak match
    [InlineData("Debut", "Madonna")]    // right title, another artist
    [InlineData("Homogenic", "Björk")]
    public void PickBest_WeakMatch_IsNotFound(string album, string artist)
    {
        var results = DiscogsGenreService.ParseSearch(DebutJson, "master");

        Assert.Null(DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery(artist, album), "master"));
    }

    [Fact]
    public void PickBest_Releases_PreferTheYearClosestToTheAlbums()
    {
        const string json = """
            {"results":[
              {"id":1,"type":"release","title":"Björk - Debut","year":"1993","genre":["Electronic"],"style":["House"],"uri":"/release/1"},
              {"id":2,"type":"release","title":"Björk - Debut","year":"2008","genre":["Electronic"],"style":["Synth-pop"],"uri":"/release/2"},
              {"id":3,"type":"release","title":"Björk - Debut","year":"","genre":["Electronic"],"style":["Ambient"]}
            ]}
            """;
        var results = DiscogsGenreService.ParseSearch(json, "release");

        Assert.Equal(2, DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery("Björk", "Debut", 2008), "release")?.Id);
        Assert.Equal(1, DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery("Björk", "Debut", 1994), "release")?.Id);
        // No year of ours: Discogs' own order.
        Assert.Equal(1, DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery("Björk", "Debut"), "release")?.Id);
    }

    [Fact]
    public void PickBest_DisambiguatedArtist_AndCompilations()
    {
        const string json = """
            {"results":[
              {"id":7,"type":"master","title":"Nirvana (2) - Nevermind","year":1991,"genre":["Rock"],"style":["Grunge"]},
              {"id":8,"type":"master","title":"Various - Pure Moods","year":1994,"genre":["Electronic"],"style":["Ambient","New Age"]}
            ]}
            """;
        var results = DiscogsGenreService.ParseSearch(json, "master");

        Assert.Equal(7, DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery("Nirvana", "Nevermind"), "master")?.Id);
        Assert.Equal(8, DiscogsGenreService.PickBest(results, new DiscogsAlbumQuery(Track.VariousArtists, "Pure Moods"), "master")?.Id);
        Assert.Equal("Various", DiscogsGenreService.SearchArtist(Track.VariousArtists));
    }

    // ── Options ──

    [Fact]
    public void PickGenres_StylesByDefault_AllOfThem()
        => Assert.Equal(new[] { "House", "Synth-pop", "Experimental", "Deep House" },
            DiscogsGenreService.PickGenres(DebutMatch, new DiscogsGenreOptions()));

    [Fact]
    public void PickGenres_SourceAndMaxCount()
    {
        Assert.Equal(new[] { "Electronic", "Rock" },
            DiscogsGenreService.PickGenres(DebutMatch, new DiscogsGenreOptions(DiscogsGenreSource.Genres)));
        Assert.Equal(new[] { "House", "Synth-pop" },
            DiscogsGenreService.PickGenres(DebutMatch, new DiscogsGenreOptions(MaxGenres: 2)));
        Assert.Equal(new[] { "House", "Synth-pop", "Experimental", "Deep House", "Electronic", "Rock" },
            DiscogsGenreService.PickGenres(DebutMatch, new DiscogsGenreOptions(DiscogsGenreSource.StylesAndGenres)));
        Assert.Equal(new[] { "House", "Synth-pop", "Experimental", "Deep House", "Electronic" },
            DiscogsGenreService.PickGenres(DebutMatch, new DiscogsGenreOptions(DiscogsGenreSource.StylesAndGenres, MaxGenres: 5)));
    }

    [Fact]
    public void PickGenres_NoStyles_FallsBackToGenres()
        => Assert.Equal(new[] { "Classical" },
            DiscogsGenreService.PickGenres(Match(new[] { "Classical" }, Array.Empty<string>()), new DiscogsGenreOptions()));

    [Fact]
    public void ComposeGenre_ReplacesByDefault()
        => Assert.Equal("House; Synth-pop; Experimental; Deep House",
            DiscogsGenreService.ComposeGenre("Pop", DebutMatch, new DiscogsGenreOptions()));

    [Fact]
    public void ComposeGenre_AddToExisting_KeepsCurrentFirst_AndDedupesIgnoringCase()
    {
        var options = new DiscogsGenreOptions(DiscogsGenreSource.Genres, AddToExisting: true);

        Assert.Equal("electronic; Pop; Rock", DiscogsGenreService.ComposeGenre("electronic; Pop", DebutMatch, options));
        Assert.Equal("Electronic; Rock", DiscogsGenreService.ComposeGenre("", DebutMatch, options));
    }

    [Fact]
    public void Prefs_RoundTripThroughSettings()
    {
        var s = new AppSettings();
        Assert.Equal(new DiscogsGenreOptions(), MetadataHelper.DiscogsGenrePrefsFrom(s));

        var o = new DiscogsGenreOptions(DiscogsGenreSource.StylesAndGenres, 3, UseRelease: true, AddToExisting: true);
        MetadataHelper.ApplyDiscogsGenrePrefs(s, o);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("StylesAndGenres", back.DiscogsGenreSource);
        Assert.Equal(o, MetadataHelper.DiscogsGenrePrefsFrom(back));
    }

    // ── HTTP: query, 429, rate-limit window, fallbacks ──

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private static HttpResponseMessage Json(string body, int remaining = 24)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("x-discogs-ratelimit", "25");
        r.Headers.Add("x-discogs-ratelimit-remaining", remaining.ToString());
        return r;
    }

    private static HttpResponseMessage TooMany(TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } d) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(d);
        return r;
    }

    /// <summary>A service on a fake clock whose delays advance it (and are recorded) instantly.</summary>
    private static (DiscogsGenreService Service, List<TimeSpan> Delays) Service(FakeHandler handler)
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var delays = new List<TimeSpan>();
        var service = new DiscogsGenreService(new HttpClient(handler), TimeSpan.FromSeconds(2.5),
            clock: () => now,
            delay: (d, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                delays.Add(d);
                now += d;
                return Task.CompletedTask;
            });
        return (service, delays);
    }

    [Fact]
    public async Task Find_SearchesMasterWithBaseTitle_AndADescriptiveUserAgent()
    {
        var handler = new FakeHandler((_, _) => Json(DebutJson));
        var (service, _) = Service(handler);

        var match = await service.FindAsync(new DiscogsAlbumQuery("Björk", "Debut (Deluxe Edition)", 1993), useRelease: false, CancellationToken.None);

        Assert.Equal(34486, match?.Id);
        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        Assert.StartsWith("https://api.discogs.com/database/search?", url);
        Assert.Contains("type=master", url);
        Assert.Contains("release_title=Debut&", url);
        Assert.Contains("artist=Bj%C3%B6rk", req.RequestUri.AbsoluteUri);
        Assert.Contains("Noctis/", req.Headers.UserAgent.ToString());
        Assert.Contains("github.com", req.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Find_RepeatedAlbum_IsAnsweredFromTheCache()
    {
        var handler = new FakeHandler((_, _) => Json(DebutJson));
        var (service, _) = Service(handler);

        await service.FindAsync(Debut, false, CancellationToken.None);
        await service.FindAsync(Debut, false, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Find_NoMasterMatch_TriesReleases_UnlessReleaseIsAskedForDirectly()
    {
        const string releases = """{"results":[{"id":99,"type":"release","title":"Björk - Debut","year":"1993","genre":["Electronic"],"style":["House"]}]}""";
        var handler = new FakeHandler((r, _) => Json(r.RequestUri!.Query.Contains("type=master") ? """{"results":[]}""" : releases));
        var (service, _) = Service(handler);

        var match = await service.FindAsync(Debut, useRelease: false, CancellationToken.None);

        Assert.Equal(99, match?.Id);
        Assert.Equal("release", match?.Kind);
        Assert.Equal(2, handler.Requests.Count);

        var direct = new FakeHandler((r, _) => Json(releases));
        var (releaseService, _) = Service(direct);
        Assert.Equal(99, (await releaseService.FindAsync(Debut, useRelease: true, CancellationToken.None))?.Id);
        Assert.Contains("type=release", Assert.Single(direct.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task Find_429_BacksOffAndRetries()
    {
        var handler = new FakeHandler((_, n) => n == 1 ? TooMany(TimeSpan.FromSeconds(20)) : Json(DebutJson));
        var (service, delays) = Service(handler);

        var match = await service.FindAsync(Debut, false, CancellationToken.None);

        Assert.Equal(34486, match?.Id);
        Assert.Equal(2, handler.Requests.Count);
        // Waited out at least what Discogs asked for before asking again.
        Assert.True(delays.Sum(d => d.TotalSeconds) >= 20, string.Join(", ", delays));
    }

    [Fact]
    public async Task Find_429EveryTime_GivesUpWithBusy()
    {
        var handler = new FakeHandler((_, _) => TooMany());
        var (service, _) = Service(handler);

        await Assert.ThrowsAsync<DiscogsBusyException>(() => service.FindAsync(Debut, false, CancellationToken.None));
        Assert.InRange(handler.Requests.Count, 2, 6);

        // A new run asks again.
        service.BeginRun();
        await Assert.ThrowsAsync<DiscogsBusyException>(() => service.FindAsync(Debut with { Title = "Post" }, false, CancellationToken.None));
    }

    [Fact]
    public async Task Find_RateLimitWindowUsedUp_WaitsBeforeTheNextRequest()
    {
        var handler = new FakeHandler((_, n) => Json(DebutJson, remaining: n == 1 ? 0 : 20));
        var (service, delays) = Service(handler);

        await service.FindAsync(Debut, false, CancellationToken.None);
        Assert.Empty(delays);
        await service.FindAsync(new DiscogsAlbumQuery("Björk", "Post"), true, CancellationToken.None);

        Assert.Contains(delays, d => d >= TimeSpan.FromSeconds(55));
    }

    [Fact]
    public async Task Find_RequestsAreSpacedOut()
    {
        var handler = new FakeHandler((_, _) => Json("""{"results":[]}"""));
        var (service, delays) = Service(handler);

        await service.FindAsync(new DiscogsAlbumQuery("A", "One"), true, CancellationToken.None);
        await service.FindAsync(new DiscogsAlbumQuery("A", "Two"), true, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2.5), Assert.Single(delays));
    }

    [Fact]
    public async Task Find_ServerError_Throws_NotFoundIsNull()
    {
        var (failing, _) = Service(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.FindAsync(Debut, true, CancellationToken.None));

        var (missing, _) = Service(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await missing.FindAsync(Debut, true, CancellationToken.None));
    }

    // ── Writing through the editor's tag writer ──

    private string CopyFixture(string ext, params string[] genres)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.{ext}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + ext), path);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Album = "Debut";
            f.Tag.AlbumArtists = new[] { "Björk" };
            f.Tag.Performers = new[] { "Björk" };
            f.Tag.Title = "Human Behaviour";
            f.Tag.Genres = genres;
            f.Save();
        }
        return path;
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    public async Task Apply_WritesEachGenreAsItsOwnTagValue(string ext)
    {
        var metadata = new MetadataService();
        // More genres in the file than Discogs gives: the shorter list is still written (an edit, not a stray save).
        var track = metadata.ReadTrackMetadata(CopyFixture(ext, "Pop", "Trip Hop", "Art Pop", "Electronica", "Dance", "Alternative"))!;
        var genre = DiscogsGenreService.ComposeGenre(track.Genre, DebutMatch, new DiscogsGenreOptions(MaxGenres: 3));

        var result = await DiscogsGenreService.ApplyAsync(new[] { (track, genre) }, metadata, library: null);

        Assert.Equal(1, result.Written);
        Assert.Empty(result.Failed);
        Assert.Equal("House; Synth-pop; Experimental", track.Genre);
        using var f = TagLib.File.Create(track.FilePath);
        Assert.Equal(new[] { "House", "Synth-pop", "Experimental" }, f.Tag.Genres);
        Assert.Equal("House; Synth-pop; Experimental", metadata.ReadTrackMetadata(track.FilePath)!.Genre);
    }

    [Fact]
    public async Task Apply_FileThatCannotBeWritten_KeepsItsOldGenre()
    {
        var metadata = new MetadataService();
        var good = metadata.ReadTrackMetadata(CopyFixture("flac", "Pop"))!;
        var gone = metadata.ReadTrackMetadata(CopyFixture("flac", "Pop"))!;
        File.Delete(gone.FilePath);

        var result = await DiscogsGenreService.ApplyAsync(new[] { (good, "House"), (gone, "House") }, metadata, library: null);

        Assert.Equal(1, result.Written);
        Assert.Same(gone, Assert.Single(result.Failed));
        Assert.Equal("Pop", gone.Genre);
        Assert.Equal("House", good.Genre);
    }

    // ── The dialog's view model ──

    [Fact]
    public async Task ViewModel_FindsReviewsAndAppliesOnlyTickedAlbums()
    {
        var metadata = new MetadataService();
        var debut = metadata.ReadTrackMetadata(CopyFixture("flac", "Pop"))!;
        var other = metadata.ReadTrackMetadata(CopyFixture("mp3", "Rock"))!;
        other.Album = "Homogenic";
        var handler = new FakeHandler((_, _) => Json(DebutJson));
        var (service, _) = Service(handler);
        string? opened = null;
        DiscogsGenreOptions? saved = null;
        var vm = new BulkDiscogsGenresViewModel(new[] { new[] { debut }, new[] { other } }, service, metadata, library: null,
            savePrefs: o => saved = o, openUrl: u => opened = u);

        Assert.True(vm.CanStart);
        Assert.False(vm.CanApply);
        await vm.StartCommand.ExecuteAsync(null);

        var debutRow = vm.Rows[0];
        var otherRow = vm.Rows[1];
        Assert.Equal(BulkDiscogsGenresViewModel.RowState.Found, debutRow.State);
        Assert.Equal(BulkDiscogsGenresViewModel.RowState.NotFound, otherRow.State);
        Assert.True(debutRow.IsIncluded);
        Assert.Equal("House · Synth-pop · Experimental · Deep House", debutRow.ProposedText);
        Assert.Contains("1993", debutRow.MatchText);
        Assert.Equal("Pop", debut.Genre); // nothing written before Apply

        // Options reshape the proposal without a new lookup, and are kept.
        var requests = handler.Requests.Count;
        vm.MaxIndex = 2;
        Assert.Equal("House · Synth-pop", debutRow.ProposedText);
        Assert.Equal(2, saved?.MaxGenres);
        Assert.Equal(requests, handler.Requests.Count);

        vm.OpenMatchCommand.Execute(debutRow);
        Assert.Equal("https://www.discogs.com/master/34486-Bj%C3%B6rk-Debut", opened);

        Assert.True(vm.CanApply);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(BulkDiscogsGenresViewModel.RowState.Saved, debutRow.State);
        Assert.Equal("House; Synth-pop", debut.Genre);
        Assert.Equal("Rock", other.Genre);
        using var f = TagLib.File.Create(debut.FilePath);
        Assert.Equal(new[] { "House", "Synth-pop" }, f.Tag.Genres);
    }

    [Fact]
    public async Task ViewModel_UntickedAlbum_IsNotWritten_AndUseReleaseLooksUpAgain()
    {
        var metadata = new MetadataService();
        var debut = metadata.ReadTrackMetadata(CopyFixture("flac", "Pop"))!;
        var (service, _) = Service(new FakeHandler((_, _) => Json(DebutJson)));
        var vm = new BulkDiscogsGenresViewModel(new[] { new[] { debut } }, service, metadata, library: null);

        await vm.StartCommand.ExecuteAsync(null);
        vm.Rows[0].IsIncluded = false;
        Assert.False(vm.CanApply);

        vm.UseRelease = true;
        Assert.Equal(BulkDiscogsGenresViewModel.RowState.Pending, vm.Rows[0].State);
        Assert.True(vm.CanStart);
        Assert.Equal("Pop", debut.Genre);
    }

    [Fact]
    public async Task ViewModel_NoChange_IsNotTicked()
    {
        var metadata = new MetadataService();
        var debut = metadata.ReadTrackMetadata(CopyFixture("flac", "House", "Synth-pop", "Experimental", "Deep House"))!;
        var (service, _) = Service(new FakeHandler((_, _) => Json(DebutJson)));
        var vm = new BulkDiscogsGenresViewModel(new[] { new[] { debut } }, service, metadata, library: null);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.Rows[0].IsIncluded);
        Assert.False(vm.Rows[0].CanInclude);
        Assert.False(vm.CanApply);
    }
}
