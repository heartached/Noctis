using System.Diagnostics;
using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services.MetadataSearch;
using Xunit;

namespace Noctis.Tests;

// Search metadata revamp (owner 10-08). Every response here was recorded from the live APIs on
// 2026-10-08 (Fixtures/MetadataSearch, noise fields like preview URLs/country lists trimmed),
// served through a fake handler — no test touches the network.
public class MetadataSearchEngineTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MetadataSearch", name), Encoding.UTF8);

    private static readonly TimeSpan Album320 = TimeSpan.FromSeconds(320);

    // ── Parsing ──

    [Fact]
    public void Deezer_ParseSearchTracks_ReadsFieldsAndCoverSizes()
    {
        var hits = DeezerProvider.ParseSearchTracks(Fixture("deezer_search_track.json"));

        Assert.Equal(10, hits.Count);
        var first = hits[0];
        Assert.Equal("3135553", first.ProviderId);
        Assert.Equal("One More Time", first.Title);
        Assert.Equal("Daft Punk", first.Artist);
        Assert.Equal("Discovery", first.Album);
        Assert.Equal("GBDUW0000053", first.Isrc);
        Assert.Equal(Album320, first.Duration);
        Assert.False(first.Explicit);
        Assert.Contains("/1000x1000-", first.ArtworkUrl!.AbsoluteUri);
        Assert.Equal(1000, first.ArtworkSize);
        Assert.Contains("/250x250-", first.ArtworkThumbUrl!.AbsoluteUri);
        Assert.Equal("https://www.deezer.com/track/3135553", first.WebUrl!.AbsoluteUri);
    }

    [Fact]
    public void Deezer_ParseTrack_ThenAlbum_FillsEveryField()
    {
        var track = DeezerProvider.ParseTrack(Fixture("deezer_track_3135553.json"))!;
        var full = DeezerProvider.ApplyAlbum(track, Fixture("deezer_album_302127.json"));

        Assert.Equal(1, full.TrackNumber);
        Assert.Equal(1, full.DiscNumber);
        Assert.Null(full.Bpm); // Deezer reports 0 = unknown
        Assert.Equal("2001-03-07", full.ReleaseDate);
        Assert.Equal(2001, full.Year);
        Assert.Equal("Electro", full.Genre);
        Assert.Equal("Daft Life Ltd./ADA France", full.Label);
        Assert.Equal("724384960650", full.Barcode);
        Assert.Equal(14, full.TrackCount);
        Assert.Equal("Daft Punk", full.AlbumArtist);
    }

    [Fact]
    public void Deezer_ParseAlbum_WithTrackList_HasPositions()
    {
        var album = DeezerProvider.ParseAlbum(Fixture("deezer_album_302127.json"), Fixture("deezer_album_302127_tracks.json"))!;

        Assert.Equal("Discovery", album.Album);
        Assert.Equal(14, album.TrackCount);
        Assert.Equal(1, album.DiscCount);
        Assert.Equal(14, album.Tracks.Count);
        Assert.Equal((1, 1, "One More Time"), (album.Tracks[0].TrackNumber!.Value, album.Tracks[0].DiscNumber!.Value, album.Tracks[0].Title));
        Assert.Equal("GBDUW0000053", album.Tracks[0].Isrc);
        Assert.Equal(Album320, album.Tracks[0].Duration);
    }

    [Fact]
    public void MusicBrainz_ParseRecordings_PlacesRecordingOnItsOfficialAlbum()
    {
        var list = MusicBrainzProvider.ParseRecordings(Fixture("musicbrainz_recording_search.json"), "Discovery", "GBDUW0000053");
        var (c, releaseId) = list.Single(x => x.Candidate.ProviderId == "60fa767a-d85d-4991-82bc-4294e0b11ae7");

        // Of "Discovery / Human After All", two official "Discovery" releases (2024, 2005) and a
        // bootleg, the earliest official "Discovery" wins.
        Assert.Equal("6cd30d99-4923-4d5e-8e51-9d87506976f1", releaseId);
        Assert.Equal("Discovery", c.Album);
        Assert.Equal("2005-01-24", c.ReleaseDate);
        Assert.Equal("GBDUW0000053", c.Isrc); // the query's ISRC out of the recording's two
        Assert.Equal(TimeSpan.FromMilliseconds(320746), c.Duration);
        Assert.Equal(1, c.TrackNumber);
        Assert.Equal(1, c.DiscNumber);
        Assert.Equal(14, c.TrackCount);
        Assert.Equal("Daft Punk", c.Artist);
    }

    [Fact]
    public void MusicBrainz_ParseRelease_ReadsLabelOriginalDateAndCoverArtArchive()
    {
        var r = MusicBrainzProvider.ParseRelease(Fixture("musicbrainz_release_6cd30d99.json"))!;

        Assert.Equal("Virgin", r.Label); // the distributor (ADA France) is skipped
        Assert.Equal("724384960650", r.Barcode);
        Assert.Equal("2001-02-26", r.ReleaseDate); // release-group original, not the 2005 reissue
        Assert.Equal(2001, r.Year);
        Assert.Equal(14, r.Tracks.Count);
        Assert.Equal(1, r.DiscCount);
        Assert.Equal("https://coverartarchive.org/release/6cd30d99-4923-4d5e-8e51-9d87506976f1/front-1200", r.ArtworkUrl!.AbsoluteUri);
        Assert.EndsWith("/front-250", r.ArtworkThumbUrl!.AbsoluteUri);
        Assert.False(string.IsNullOrEmpty(r.Genre));
    }

    [Fact]
    public void MusicBrainz_ApplyRecordingDetails_ReadsComposerFromWorkRelationships()
    {
        var c = new MetadataCandidate { Provider = ProviderNames.MusicBrainz, ProviderId = "60fa767a-d85d-4991-82bc-4294e0b11ae7" };
        var full = MusicBrainzProvider.ApplyRecordingDetails(c, Fixture("musicbrainz_recording_works.json"));

        // Composer + writers of the performed work, de-duplicated, composer first.
        Assert.Equal(string.Join(ArtistCredit.JoinText, "Thomas Bangalter", "Guy‐Manuel de Homem‐Christo", "Anthony Wayne Moore"), full.Composer);
        Assert.Equal("House", full.Genre); // most-voted genre, title-cased
        Assert.False(string.IsNullOrEmpty(full.Isrc));
    }

    // A "samples material" relation points at another song; its writers are not this one's.
    [Fact]
    public void MusicBrainz_ApplyRecordingDetails_IgnoresSampledWorks()
    {
        const string json = """
        {"id":"r","relations":[
          {"type":"samples material","target-type":"work","work":{"title":"Shape of My Heart","relations":[{"type":"composer","artist":{"name":"Sting"}}]}},
          {"type":"performance","target-type":"work","work":{"title":"Lucid Dreams","relations":[{"type":"writer","artist":{"name":"Jarad Higgins"}}]}}
        ]}
        """;
        var full = MusicBrainzProvider.ApplyRecordingDetails(new MetadataCandidate { ProviderId = "r" }, json);
        Assert.Equal("Jarad Higgins", full.Composer);
    }

    [Fact]
    public void AppleMusic_ParseSongs_ReadsCountsExplicitAndResizesArtwork()
    {
        var songs = AppleMusicProvider.ParseSongs(Fixture("itunes_search_song.json"));

        Assert.Equal(10, songs.Count);
        var s = songs[0];
        Assert.Equal("697195462", s.ProviderId);
        Assert.Equal("One More Time", s.Title);
        Assert.Equal("Discovery", s.Album);
        Assert.Equal((1, 14, 1, 1), (s.TrackNumber!.Value, s.TrackCount!.Value, s.DiscNumber!.Value, s.DiscCount!.Value));
        Assert.False(s.Explicit);
        Assert.Equal("Dance", s.Genre);
        Assert.Equal(TimeSpan.FromMilliseconds(320357), s.Duration);
        Assert.EndsWith("/3000x3000bb.jpg", s.ArtworkUrl!.AbsoluteUri);
        Assert.EndsWith("/300x300bb.jpg", s.ArtworkThumbUrl!.AbsoluteUri);
        Assert.Null(s.ArtworkSize); // Apple serves min(requested, master): size not known up front
    }

    [Fact]
    public void AppleMusic_ApplyCollection_AddsCopyrightAndAlbumDate()
    {
        var song = AppleMusicProvider.ParseSongs(Fixture("itunes_search_song.json"))[0];
        Assert.Equal("2000-11-30", song.ReleaseDate); // the song's single date

        var full = AppleMusicProvider.ApplyCollection(song, Fixture("itunes_lookup_697194953.json"));

        Assert.Equal("℗ 2001 Daft Life Limited", full.Copyright);
        Assert.Equal("2001-03-12", full.ReleaseDate);
    }

    [Fact]
    public void AppleMusic_ParseAlbumLookup_HasTrackList()
    {
        var album = AppleMusicProvider.ParseAlbumLookup(Fixture("itunes_lookup_697194953.json"))!;

        Assert.Equal("Discovery", album.Album);
        Assert.Equal(14, album.Tracks.Count);
        Assert.Equal("One More Time", album.Tracks[0].Title);
        Assert.Equal("℗ 2001 Daft Life Limited", album.Copyright);
    }

    // ── Text matching ──

    [Theory]
    [InlineData("One More Time (Short Radio Edit)", "onemoretime", "radio,edit")]
    [InlineData("One More Time / Aerodynamic (Live)", "onemoretimeaerodynamic", "live")]
    [InlineData("Song - Remastered 2011", "song", "")]
    [InlineData("Song (Album Version)", "song", "")]
    [InlineData("Song (feat. Someone)", "song", "")]
    [InlineData("One More Time (As Made Famous By Daft Punk)", "onemoretime", "made famous")]
    [InlineData("Get Lucky (Drumless Edition)", "getlucky", "drumless")]
    [InlineData("Titi Me Pregunto (Salsa Version)", "titimepregunto", "salsa")]
    [InlineData("Love Story (Taylor's Version)", "lovestory", "taylor")]
    [InlineData("Song (10th Anniversary Edition) [2011 Remaster]", "song", "")]
    [InlineData("One More Time (12\" Mix)", "onemoretime", "mix")]
    [InlineData("Song (Original Mix)", "song", "")]
    public void AnalyzeTitle_SplitsBaseAndVersionMarkers(string title, string expectedBase, string markers)
    {
        var (b, m) = MatchText.AnalyzeTitle(title);
        Assert.Equal(expectedBase, b);
        Assert.Equal(markers.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x), m.OrderBy(x => x));
    }

    [Fact]
    public void ArtistSimilarity_FoldsDiacriticsAndFeaturedCredits()
    {
        Assert.Equal(1.0, MatchText.ArtistSimilarity("Beyonce", "Beyoncé"));
        Assert.True(MatchText.ArtistSimilarity("Daft Punk", "Daft Punk feat. Pharrell Williams") >= 0.99);
        Assert.True(MatchText.ArtistSimilarity("Daft Punk", "Data Punk") < 0.9);
    }

    // ── Scoring ──

    [Fact]
    public void Score_IsrcMatch_IsDecisive()
    {
        var q = new MetadataQuery { Title = "Totally different words", Artist = "Someone", Isrc = "GB-DUW-00-00053" };
        var c = new MetadataCandidate { Title = "One More Time", Artist = "Daft Punk", Isrc = "GBDUW0000053" };

        var scored = CandidateScorer.ScoreTrack(q, c);

        Assert.True(scored.Confidence >= CandidateScorer.IsrcFloor);
        Assert.Equal("ISRC match", scored.MatchNotes[0]);
    }

    [Fact]
    public void MapTracks_PairsByNumberTitleAndDuration()
    {
        var local = new List<Track>
        {
            new() { Title = "Aerodynamic", TrackNumber = 2, DiscNumber = 1, Duration = TimeSpan.FromSeconds(212) },
            new() { Title = "One More Time", TrackNumber = 1, DiscNumber = 1, Duration = TimeSpan.FromSeconds(320) },
            new() { Title = "Bonus Thing", TrackNumber = 15, DiscNumber = 1, Duration = TimeSpan.FromSeconds(100) },
        };
        var cand = new List<CandidateTrack>
        {
            new() { Title = "One More Time", TrackNumber = 1, DiscNumber = 1, Duration = TimeSpan.FromSeconds(320) },
            new() { Title = "Aerodynamic", TrackNumber = 2, DiscNumber = 1, Duration = TimeSpan.FromSeconds(213) },
        };

        var map = CandidateScorer.MapTracks(local, cand);

        Assert.Equal(2, map.Matched);
        Assert.Equal(local[1].Id, map.Tracks[0].MatchedLocalTrackId);
        Assert.Equal(local[0].Id, map.Tracks[1].MatchedLocalTrackId);
    }

    // ── Merging ──

    [Fact]
    public void Merge_SameIsrcAcrossProviders_UnionsFieldsByPrecedence()
    {
        var deezer = new MetadataCandidate
        {
            Provider = ProviderNames.Deezer, ProviderId = "1", Title = "One More Time", Artist = "Daft Punk",
            Isrc = "GBDUW0000053", Bpm = 123, Label = "Deezer Label", ReleaseDate = "2001-03-07", Year = 2001,
            ArtworkUrl = new Uri("https://cdn-images.dzcdn.net/a/1000x1000-000000-80-0-0.jpg"), ArtworkSize = 1000, Confidence = 0.9,
        };
        var mb = new MetadataCandidate
        {
            Provider = ProviderNames.MusicBrainz, ProviderId = "m", Title = "One More Time", Artist = "Daft Punk",
            Isrc = "GBDUW0000053", Label = "Virgin", Composer = "Thomas Bangalter", ReleaseDate = "2001-02-26", Year = 2001,
            ArtworkUrl = new Uri("https://coverartarchive.org/release/x/front"), Confidence = 0.8,
        };
        var other = new MetadataCandidate { Provider = ProviderNames.Deezer, ProviderId = "2", Title = "Aerodynamic", Artist = "Daft Punk", Isrc = "GBDUW0000057" };

        var groups = CandidateMerger.Group(new[] { deezer, mb, other }, albumScope: false);
        Assert.Equal(2, groups.Count);

        var merged = CandidateMerger.Merge(groups[0]);
        Assert.Equal("Deezer + MusicBrainz", merged.Provider);
        Assert.Equal(123, merged.Bpm);                      // Deezer only
        Assert.Equal("Virgin", merged.Label);               // MusicBrainz first
        Assert.Equal("Thomas Bangalter", merged.Composer);
        Assert.Equal("2001-02-26", merged.ReleaseDate);     // MusicBrainz original date first
        Assert.Contains("coverartarchive", merged.ArtworkUrl!.Host); // CAA original over Deezer 1000 px
    }

    [Fact]
    public void Group_NeverMergesTwoHitsFromTheSameProvider()
    {
        var a = new MetadataCandidate { Provider = ProviderNames.Deezer, ProviderId = "1", Title = "X", Artist = "Y", Isrc = "USAAA0000001" };
        var b = a with { ProviderId = "2" };
        Assert.Equal(2, CandidateMerger.Group(new[] { a, b }, false).Count);
    }

    // ── Engine (all providers over recorded responses) ──

    [Fact]
    public async Task Search_Track_MergesProvidersAndRanksAlbumVersionFirst()
    {
        var handler = AllFixtures();
        var svc = Engine(handler);

        var result = await svc.SearchAsync(new MetadataQuery
        {
            Title = "One More Time", Artist = "Daft Punk", Album = "Discovery", Duration = Album320,
        });

        Assert.All(result.Providers, p => Assert.Equal(ProviderOutcome.Ok, p.Outcome));
        var top = result.Candidates[0];
        Assert.Equal("Apple Music + Deezer + MusicBrainz", top.Provider);
        Assert.Equal("One More Time", top.Title);
        Assert.Equal("Discovery", top.Album);
        Assert.Equal("GBDUW0000053", top.Isrc);           // Deezer's
        Assert.Equal("℗ 2001 Daft Life Limited", top.Copyright); // Apple's
        Assert.StartsWith("Thomas Bangalter", top.Composer); // MusicBrainz's
        Assert.Equal("Virgin", top.Label);
        Assert.Equal(1, top.TrackNumber);
        Assert.Equal(14, top.TrackCount);
        Assert.EndsWith("/3000x3000bb.jpg", top.ArtworkUrl!.AbsoluteUri);
        Assert.True(top.Confidence > 0.9, $"confidence {top.Confidence}");
        Assert.Contains(top.MatchNotes, n => n.StartsWith("Duration ±", StringComparison.Ordinal));
        Assert.Contains("Found on 3 sources", top.MatchNotes);

        // Results come best-first, and the 12" mix / radio edits trail the album version.
        Assert.Equal(result.Candidates.OrderByDescending(c => c.Confidence).Select(c => c.Confidence), result.Candidates.Select(c => c.Confidence));
        var radio = result.Candidates.First(c => c.Title.Contains("Short Radio Edit", StringComparison.Ordinal));
        Assert.True(radio.Confidence < top.Confidence - 0.15);
    }

    [Fact]
    public async Task Search_Track_DurationPicksTheMatchingEdit()
    {
        var svc = Engine(AllFixtures());

        var result = await svc.SearchAsync(new MetadataQuery
        {
            Title = "One More Time", Artist = "Daft Punk", Duration = TimeSpan.FromSeconds(235), Providers = new[] { ProviderNames.Deezer },
        });

        Assert.Equal("3157685", result.Candidates[0].ProviderId); // "One More Time (Short Radio Edit)", 3:55
    }

    [Fact]
    public async Task Search_Track_IsrcLookupRanksFirst_AndUsesExactEndpoints()
    {
        var handler = AllFixtures();
        var svc = Engine(handler);

        var result = await svc.SearchAsync(new MetadataQuery
        {
            Title = "One More Time", Artist = "Daft Punk", Isrc = "GBDUW0000053",
            Providers = new[] { ProviderNames.Deezer, ProviderNames.MusicBrainz },
        });

        var top = result.Candidates[0];
        Assert.Equal("ISRC match", top.MatchNotes[0]);
        Assert.True(top.Confidence >= CandidateScorer.IsrcFloor);
        Assert.Contains(handler.Requests, r => r.AbsolutePath == "/track/isrc:GBDUW0000053");
        Assert.Contains(handler.Requests, r => Uri.UnescapeDataString(r.Query).Contains("query=isrc:GBDUW0000053", StringComparison.Ordinal));
        // ISRC hit on MusicBrainz: no text search spent on top of it.
        Assert.DoesNotContain(handler.Requests, r => r.Host == "musicbrainz.org" && Uri.UnescapeDataString(r.Query).Contains("recording:\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_Album_MapsReleaseTracksOntoLocalTracks()
    {
        var local = AppleMusicProvider.ParseAlbumLookup(Fixture("itunes_lookup_697194953.json"))!.Tracks
            .Select(t => new Track { Title = t.Title, Artist = "Daft Punk", Album = "Discovery", TrackNumber = t.TrackNumber ?? 0, DiscNumber = 1, Duration = t.Duration ?? TimeSpan.Zero })
            .ToList();
        var svc = Engine(AllFixtures());

        var result = await svc.SearchAsync(new MetadataQuery
        {
            AlbumScope = true, Album = "Discovery", AlbumArtist = "Daft Punk", AlbumTracks = local,
        });

        var top = result.Candidates[0];
        Assert.Equal("Discovery", top.Album);
        Assert.Contains("+", top.Provider); // the same edition found on several sources
        Assert.Equal(14, top.Tracks.Count);
        Assert.All(top.Tracks, t => Assert.NotNull(t.MatchedLocalTrackId));
        Assert.Equal(local[0].Id, top.Tracks[0].MatchedLocalTrackId);
        Assert.Contains("14/14 tracks matched", top.MatchNotes);
        Assert.Contains("Same track count (14)", top.MatchNotes);
        Assert.True(top.Confidence > 0.9, $"confidence {top.Confidence}");
    }

    [Fact]
    public async Task Search_ProviderFailures_BecomeStatuses_NotExceptions()
    {
        var handler = new RoutedHandler();
        handler.Route(u => u.Host == "api.deezer.com", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Route(u => u.Host == "itunes.apple.com", _ => throw new HttpRequestException("No such host is known.", null, null));
        handler.Route(u => u.Host == "musicbrainz.org", _ => Json("""{"recordings":[]}"""));
        var svc = Engine(handler);

        var result = await svc.SearchAsync(new MetadataQuery { Title = "One More Time", Artist = "Daft Punk" });

        Assert.Empty(result.Candidates);
        Assert.Equal(ProviderOutcome.Failed, Status(result, ProviderNames.Deezer).Outcome);
        Assert.Equal("HTTP 500", Status(result, ProviderNames.Deezer).Message);
        Assert.Equal(ProviderOutcome.Failed, Status(result, ProviderNames.AppleMusic).Outcome);
        Assert.Equal(ProviderOutcome.NoResults, Status(result, ProviderNames.MusicBrainz).Outcome);
    }

    [Fact]
    public async Task Search_DisabledProviders_AreReportedAndNotCalled()
    {
        var handler = AllFixtures();
        var settings = new AppSettings { MusicBrainzEnabled = false, AppleMusicMetadataEnabled = false };
        var svc = Engine(handler, settings);

        var result = await svc.SearchAsync(new MetadataQuery { Title = "One More Time", Artist = "Daft Punk" });

        Assert.Equal(ProviderOutcome.Disabled, Status(result, ProviderNames.MusicBrainz).Outcome);
        Assert.Equal(ProviderOutcome.Disabled, Status(result, ProviderNames.AppleMusic).Outcome);
        Assert.Equal(ProviderOutcome.Ok, Status(result, ProviderNames.Deezer).Outcome);
        Assert.All(handler.Requests, r => Assert.Equal("api.deezer.com", r.Host));
    }

    [Fact]
    public async Task Search_SlowProvider_TimesOut_OthersStillAnswer()
    {
        var svc = new MetadataSearchService(new HttpClient(new RoutedHandler()), () => new AppSettings(), new IMetadataProvider[]
        {
            new FakeProvider("Slow", TimeSpan.FromMilliseconds(100), async ct => { await Task.Delay(5000, ct); return Array.Empty<MetadataCandidate>(); }),
            new FakeProvider("Fast", TimeSpan.FromSeconds(5), _ => Task.FromResult<IReadOnlyList<MetadataCandidate>>(new[] { new MetadataCandidate { Provider = "Fast", ProviderId = "1", Title = "One More Time" } })),
        });

        var result = await svc.SearchAsync(new MetadataQuery { Title = "One More Time" });

        Assert.Equal(ProviderOutcome.TimedOut, Status(result, "Slow").Outcome);
        Assert.Equal(ProviderOutcome.Ok, Status(result, "Fast").Outcome);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task Search_Cancelled_ThrowsOperationCanceled()
    {
        var svc = new MetadataSearchService(new HttpClient(new RoutedHandler()), () => new AppSettings(), new IMetadataProvider[]
        {
            new FakeProvider("Slow", TimeSpan.FromSeconds(30), async ct => { await Task.Delay(30000, ct); return Array.Empty<MetadataCandidate>(); }),
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SearchAsync(new MetadataQuery { Title = "x" }, cts.Token));
    }

    [Fact]
    public async Task Search_Repeated_IsServedFromCache()
    {
        var handler = AllFixtures();
        var svc = Engine(handler);
        var q = new MetadataQuery { Title = "One More Time", Artist = "Daft Punk", Album = "Discovery", Duration = Album320 };

        var first = await svc.SearchAsync(q);
        var calls = handler.Requests.Count;
        var second = await svc.SearchAsync(q with { });

        Assert.True(calls > 0);
        Assert.Equal(calls, handler.Requests.Count);
        Assert.Equal(first.Candidates.Select(c => c.ProviderId), second.Candidates.Select(c => c.ProviderId));
    }

    [Fact]
    public async Task MusicBrainzRequests_ArePacedAndCarryAContactUserAgent()
    {
        Assert.True(RequestPacer.MusicBrainz.MinInterval >= TimeSpan.FromSeconds(1));

        var handler = AllFixtures();
        var pacer = new RequestPacer(TimeSpan.FromMilliseconds(200));
        var mb = new MusicBrainzProvider(new HttpClient(handler), null, pacer);

        await mb.SearchAsync(new MetadataQuery { Title = "One More Time", Artist = "Daft Punk", Album = "Discovery" }, CancellationToken.None);

        var stamps = handler.Log.Where(l => l.Uri.Host == "musicbrainz.org").ToList();
        Assert.True(stamps.Count >= 3, $"expected search + release + recording lookups, got {stamps.Count}");
        for (var i = 1; i < stamps.Count; i++)
            Assert.True(stamps[i].At - stamps[i - 1].At >= TimeSpan.FromMilliseconds(180), $"gap {i}: {(stamps[i].At - stamps[i - 1].At).TotalMilliseconds} ms");
        Assert.All(stamps, s => Assert.Matches(@"^Noctis/\d+\.\d+\.\d+ \( https://github\.com/heartached/Noctis \)$", s.UserAgent));
    }

    [Fact]
    public async Task RequestPacer_EnforcesWindowCap()
    {
        var pacer = new RequestPacer(TimeSpan.Zero, maxPerWindow: 2, window: TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        await pacer.WaitAsync(CancellationToken.None);
        await pacer.WaitAsync(CancellationToken.None);
        var beforeThird = sw.Elapsed;
        await pacer.WaitAsync(CancellationToken.None);

        Assert.True(beforeThird < TimeSpan.FromMilliseconds(150));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(280), $"third request after {sw.Elapsed.TotalMilliseconds} ms");
    }

    // ── Artwork ──

    [Fact]
    public async Task DownloadArtwork_SkipsNonImages_FallsBackToSmallerSize()
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF;
        var handler = new RoutedHandler();
        handler.Route(u => u.AbsolutePath.EndsWith("/3000x3000bb.jpg", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>nope</html>", Encoding.UTF8, "text/html") });
        handler.Route(u => u.AbsolutePath.EndsWith("/1200x1200bb.jpg", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) { Headers = { ContentType = new("image/jpeg") } } });
        var svc = new MetadataSearchService(new HttpClient(handler), () => new AppSettings(), Array.Empty<IMetadataProvider>());

        var bytes = await svc.DownloadArtworkAsync(new MetadataCandidate
        {
            ArtworkUrl = new Uri("https://is1-ssl.mzstatic.com/image/thumb/x/y.jpg/3000x3000bb.jpg"),
            ArtworkThumbUrl = new Uri("https://is1-ssl.mzstatic.com/image/thumb/x/y.jpg/300x300bb.jpg"),
        });

        Assert.Equal(jpeg, bytes);
    }

    [Fact]
    public async Task DownloadArtwork_NothingUsable_ReturnsNull()
    {
        var svc = new MetadataSearchService(new HttpClient(new RoutedHandler()), () => new AppSettings(), Array.Empty<IMetadataProvider>());
        Assert.Null(await svc.DownloadArtworkAsync(new MetadataCandidate { ArtworkUrl = new Uri("https://coverartarchive.org/release/x/front-1200") }));
    }

    // ── Composer (owner 10-08: "2forwOyNE" among the writers) ──

    [Fact]
    public void MusicBrainz_Composer_UsesTheSongwritingCredit_NotTheProducerAlias()
    {
        var c = new MetadataCandidate { Provider = ProviderNames.MusicBrainz, ProviderId = "2a79bef3-70f8-41a2-b385-c5ca29e819cd" };
        var full = MusicBrainzProvider.ApplyRecordingDetails(c, Fixture("musicbrainz_recording_2a79bef3.json"));
        var names = full.Composer.Split(ArtistCredit.JoinText);

        Assert.Contains("Dawoyne Lawson", names);  // credited as; artist name "2forwOyNE"
        Assert.Contains("Douglas Ford", names);    // credited as; artist name "Dougie F"
        Assert.Contains("Jack Harlow", names);     // no credit: the artist name
        Assert.DoesNotContain("2forwOyNE", names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void MusicBrainz_Composer_SamePersonTwice_KeepsOneEntryWithTheCreditedName()
    {
        const string json = """
        {"id":"r","relations":[{"type":"performance","target-type":"work","work":{"relations":[
          {"type":"composer","artist":{"id":"a1","name":"Alias"}},
          {"type":"writer","target-credit":"Legal Name","artist":{"id":"a1","name":"Alias"}},
          {"type":"writer","artist":{"id":"a2","name":"Other"}}
        ]}}]}
        """;
        var full = MusicBrainzProvider.ApplyRecordingDetails(new MetadataCandidate { ProviderId = "r" }, json);
        Assert.Equal(string.Join(ArtistCredit.JoinText, "Legal Name", "Other"), full.Composer);
    }

    // ── Apple album size (live 10-08: song rows keep a stale trackCount) ──

    [Fact]
    public void AppleMusic_ApplyCollection_TakesTheAlbumsTrackCount_NotTheStaleSongRow()
    {
        var rows = AppleMusicProvider.ParseSongs(Fixture("itunes_search_song_harlow.json"));
        var onDeluxe = rows.Single(s => s.ProviderId == "1618136805");
        Assert.Equal(15, onDeluxe.TrackCount); // Apple's row, although that album has 17

        Assert.Equal(17, AppleMusicProvider.ApplyCollection(onDeluxe, Fixture("itunes_lookup_1618136433.json")).TrackCount);
        var onClean = rows.Single(s => s.ProviderId == "1622624422");
        Assert.Equal(15, AppleMusicProvider.ApplyCollection(onClean, Fixture("itunes_lookup_1622624421.json")).TrackCount);
    }

    // ── Helpers ──

    private static ProviderStatus Status(MetadataSearchResult r, string name) => r.Providers.Single(p => p.Provider == name);

    private static MetadataSearchService Engine(RoutedHandler handler, AppSettings? settings = null)
    {
        var http = new HttpClient(handler);
        var fast = new RequestPacer(TimeSpan.Zero);
        var s = settings ?? new AppSettings();
        return new MetadataSearchService(http, () => s, new IMetadataProvider[]
        {
            new DeezerProvider(http, null, fast),
            new AppleMusicProvider(http, null, fast),
            new MusicBrainzProvider(http, null, fast),
        });
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Every recorded response, routed the way the live services answer.</summary>
    private static RoutedHandler AllFixtures()
    {
        var h = new RoutedHandler();
        string Q(Uri u) => Uri.UnescapeDataString(u.Query);
        // Deezer
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/search", _ => Json(Fixture("deezer_search_track.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/search/album", _ => Json(Fixture("deezer_search_album.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/track/isrc:GBDUW0000053", _ => Json(Fixture("deezer_track_isrc.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath.StartsWith("/track/isrc:", StringComparison.Ordinal), _ => Json(Fixture("deezer_isrc_nodata.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/track/3135553", _ => Json(Fixture("deezer_track_3135553.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/album/302127/tracks", _ => Json(Fixture("deezer_album_302127_tracks.json")));
        h.Route(u => u.Host == "api.deezer.com" && u.AbsolutePath == "/album/302127", _ => Json(Fixture("deezer_album_302127.json")));
        h.Route(u => u.Host == "api.deezer.com", _ => Json(Fixture("deezer_isrc_nodata.json"))); // unknown ids: Deezer's "no data"
        // Apple
        h.Route(u => u.Host == "itunes.apple.com" && u.AbsolutePath == "/search" && Q(u).Contains("entity=song", StringComparison.Ordinal), _ => Json(Fixture("itunes_search_song.json")));
        h.Route(u => u.Host == "itunes.apple.com" && u.AbsolutePath == "/search" && Q(u).Contains("entity=album", StringComparison.Ordinal), _ => Json(Fixture("itunes_search_album.json")));
        h.Route(u => u.Host == "itunes.apple.com" && u.AbsolutePath == "/lookup" && Q(u).Contains("id=697194953", StringComparison.Ordinal), _ => Json(Fixture("itunes_lookup_697194953.json")));
        h.Route(u => u.Host == "itunes.apple.com", _ => Json("""{"resultCount":0,"results":[]}"""));
        // MusicBrainz
        h.Route(u => u.Host == "musicbrainz.org" && u.AbsolutePath == "/ws/2/recording" && Q(u).Contains("query=isrc:", StringComparison.Ordinal), _ => Json(Fixture("musicbrainz_recording_search_isrc.json")));
        h.Route(u => u.Host == "musicbrainz.org" && u.AbsolutePath == "/ws/2/recording", _ => Json(Fixture("musicbrainz_recording_search.json")));
        h.Route(u => u.Host == "musicbrainz.org" && u.AbsolutePath == "/ws/2/recording/60fa767a-d85d-4991-82bc-4294e0b11ae7", _ => Json(Fixture("musicbrainz_recording_works.json")));
        h.Route(u => u.Host == "musicbrainz.org" && u.AbsolutePath == "/ws/2/release", _ => Json(Fixture("musicbrainz_release_search.json")));
        h.Route(u => u.Host == "musicbrainz.org" && u.AbsolutePath.StartsWith("/ws/2/release/", StringComparison.Ordinal), _ => Json(Fixture("musicbrainz_release_6cd30d99.json")));
        return h;
    }

    internal sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly List<(Func<Uri, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public List<(Uri Uri, TimeSpan At, string UserAgent)> Log { get; } = new();
        public IReadOnlyList<Uri> Requests { get { lock (Log) return Log.Select(l => l.Uri).ToList(); } }

        public void Route(Func<Uri, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((match, respond));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Log) Log.Add((request.RequestUri!, _clock.Elapsed, request.Headers.UserAgent.ToString()));
            foreach (var (match, respond) in _routes)
                if (match(request.RequestUri!)) return Task.FromResult(respond(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeProvider(string name, TimeSpan timeout, Func<CancellationToken, Task<IReadOnlyList<MetadataCandidate>>> run) : IMetadataProvider
    {
        public string Name => name;
        public TimeSpan Timeout => timeout;
        public bool IsEnabled(AppSettings settings) => true;
        public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MetadataQuery query, CancellationToken ct) => run(ct);
    }
}
