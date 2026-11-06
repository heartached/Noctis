using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #160 follow-up ("I don't see any synced plays"): the import only ran when Import was
/// pressed. "Keep play counts in sync" runs it in the background, reading only what was
/// scrobbled since the last sync and adding it to the saved whole-history totals.
/// </summary>
public class PlayCountImportSyncTests : IDisposable
{
    private const long T0 = 1_800_000_000;
    private static readonly long Day = 86_400;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "pcsync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Scrobble(long Ts, string Artist, string Title, string Album);

    // ── A fake Last.fm user.getRecentTracks honouring from (inclusive) / to (exclusive) ──

    private static HttpResponseMessage LastFmReply(HttpRequestMessage r, IEnumerable<Scrobble> all)
    {
        var q = HttpUtility.ParseQueryString(r.RequestUri!.Query);
        long? from = q["from"] is { } f ? long.Parse(f, CultureInfo.InvariantCulture) : null;
        var to = long.Parse(q["to"]!, CultureInfo.InvariantCulture);
        var hits = all.Where(s => (from == null || s.Ts >= from) && s.Ts < to).OrderByDescending(s => s.Ts).ToList();
        var items = hits.Select(s =>
            "{\"artist\":{\"#text\":\"" + s.Artist + "\"},\"name\":\"" + s.Title + "\",\"album\":{\"#text\":\"" + s.Album +
            "\"},\"date\":{\"uts\":\"" + s.Ts + "\",\"#text\":\"x\"}}");
        var body = "{\"recenttracks\":{\"track\":[" + string.Join(",", items) +
                   "],\"@attr\":{\"page\":\"1\",\"perPage\":\"200\",\"totalPages\":\"1\",\"total\":\"" + hits.Count + "\"}}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> respond, List<Uri>? requests = null)
        => new(new AnimatedArtworkChooserTests.StubHandler(r =>
        {
            lock (requests ?? new List<Uri>()) requests?.Add(r.RequestUri!);
            return Task.FromResult(respond(r));
        }));

    private static PlayCountSync Sync(HttpClient http, long now)
        => new(new PlayCountHistoryFetcher(http, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => DateTimeOffset.FromUnixTimeSeconds(now)));

    private static int Count(PlayCountSyncState s, string artist, string title)
        => s.Entries.Where(e => e.Artist == artist && e.Title == title).Sum(e => e.Count);

    [Fact]
    public async Task Sync_reads_everything_first_then_only_whats_new_without_counting_twice()
    {
        var scrobbles = new List<Scrobble>
        {
            new(T0 - 300, "Art", "A", "Al"), new(T0 - 200, "Art", "A", "Al"), new(T0 - 100, "Art", "A", "Al"),
            new(T0 - 20 * Day, "Art", "B", "Al"),
        };
        var requests = new List<Uri>();
        var http = Http(r => LastFmReply(r, scrobbles), requests);

        var first = await Sync(http, T0).RunAsync(PlayCountImportSource.LastFm, "me", null, null, null, null, CancellationToken.None);
        Assert.False(first.Incremental);
        Assert.Equal(4, first.NewListens);
        Assert.DoesNotContain("from=", requests.Single().Query);
        Assert.Equal(T0, first.State.SyncedTo);
        Assert.Equal(3, Count(first.State, "Art", "A"));
        Assert.Equal(3, first.State.Recent.Count); // B is older than the overlap window

        scrobbles.Add(new(T0 + 3600, "Art", "A", "Al"));
        scrobbles.Add(new(T0 + 7200, "Art", "C", "Al"));
        requests.Clear();
        var second = await Sync(http, T0 + Day).RunAsync(PlayCountImportSource.LastFm, "ME", null, null, first.State, null, CancellationToken.None);

        Assert.True(second.Incremental);
        Assert.Contains($"from={T0 - 14 * Day}&", requests.Single().Query); // reaches back over the overlap
        Assert.Contains($"to={T0 + Day}&", requests.Single().Query);
        Assert.Equal(2, second.NewListens); // the three A's already counted are skipped
        Assert.Equal(4, Count(second.State, "Art", "A"));
        Assert.Equal(1, Count(second.State, "Art", "B")); // kept from the first sync's totals
        Assert.Equal(1, Count(second.State, "Art", "C"));
        Assert.Equal(T0 + Day, second.State.SyncedTo);

        // Nothing new: the totals stay exactly the same.
        var third = await Sync(http, T0 + 2 * Day).RunAsync(PlayCountImportSource.LastFm, "me", null, null, second.State, null, CancellationToken.None);
        Assert.Equal(0, third.NewListens);
        Assert.Equal(second.State.Entries.Sum(e => e.Count), third.State.Entries.Sum(e => e.Count));
    }

    [Fact]
    public async Task Sync_counts_a_late_scrobble_dated_before_the_last_sync()
    {
        var scrobbles = new List<Scrobble> { new(T0 - 100, "Art", "A", "Al") };
        var http = Http(r => LastFmReply(r, scrobbles));
        var first = await Sync(http, T0).RunAsync(PlayCountImportSource.LastFm, "me", null, null, null, null, CancellationToken.None);

        // A phone's offline queue sends a listen from two days before the last sync.
        scrobbles.Add(new(T0 - 2 * Day, "Art", "D", "Al"));
        var second = await Sync(http, T0 + Day).RunAsync(PlayCountImportSource.LastFm, "me", null, null, first.State, null, CancellationToken.None);

        Assert.Equal(1, second.NewListens);
        Assert.Equal(1, Count(second.State, "Art", "D"));
        Assert.Equal(1, Count(second.State, "Art", "A"));
    }

    [Theory]
    [InlineData("someone-else", 1)]
    [InlineData("me", 0)] // old state version
    public async Task Sync_reads_everything_again_for_another_account_or_an_old_state(string user, int version)
    {
        var scrobbles = new List<Scrobble> { new(T0 - 100, "Art", "A", "Al"), new(T0 - 30 * Day, "Art", "A", "Al") };
        var requests = new List<Uri>();
        var http = Http(r => LastFmReply(r, scrobbles), requests);
        var previous = new PlayCountSyncState
        {
            Version = version, Source = PlayCountImportSource.LastFm, User = user, SyncedTo = T0 - Day,
            Entries = { new SourcePlayCount("Art", "A", "Al", 50) },
        };

        var run = await Sync(http, T0).RunAsync(PlayCountImportSource.LastFm, "me", null, null, previous, null, CancellationToken.None);

        Assert.False(run.Incremental);
        Assert.DoesNotContain("from=", requests.Single().Query);
        Assert.Equal(2, Count(run.State, "Art", "A")); // not 50 + anything
        Assert.Equal("me", run.State.User);
    }

    [Fact]
    public async Task Sync_ListenBrainz_incremental_stops_paging_once_past_the_window()
    {
        // 1500 listens an hour apart (62 days); pages of 1000 newest first.
        var now = T0;
        var listens = Enumerable.Range(1, 1500).Select(i => new Scrobble(now - i * 3600, "Art", "S" + (i % 7), "Al")).ToList();
        var requests = new List<Uri>();
        var http = Http(r =>
        {
            var q = HttpUtility.ParseQueryString(r.RequestUri!.Query);
            string body;
            if (r.RequestUri!.AbsolutePath.EndsWith("/listen-count", StringComparison.Ordinal))
                body = "{\"payload\":{\"count\":1500}}";
            else
            {
                var max = q["max_ts"] is { } m ? long.Parse(m, CultureInfo.InvariantCulture) : long.MaxValue;
                var page = listens.Where(l => l.Ts < max).OrderByDescending(l => l.Ts).Take(1000).Select(l =>
                    "{\"listened_at\":" + l.Ts + ",\"track_metadata\":{\"artist_name\":\"" + l.Artist + "\",\"track_name\":\"" +
                    l.Title + "\",\"release_name\":\"" + l.Album + "\"}}");
                body = "{\"payload\":{\"listens\":[" + string.Join(",", page) + "]}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }, requests);
        var previous = new PlayCountSyncState
        {
            Source = PlayCountImportSource.ListenBrainz, User = "me", ApiUrl = ListenBrainzService.DefaultApiUrl,
            SyncedTo = now - Day,
        };

        var run = await Sync(http, now).RunAsync(PlayCountImportSource.ListenBrainz, "me", null, null, previous, null, CancellationToken.None);

        Assert.True(run.Incremental);
        // listen-count + ONE page: it reached back past the 15-day window, so no second page.
        Assert.Equal(2, requests.Count);
        Assert.Contains($"max_ts={now}", requests[1].Query);
        var from = now - Day - 14 * Day;
        Assert.Equal(listens.Count(l => l.Ts >= from), run.NewListens);
        Assert.Equal(run.NewListens, run.State.Entries.Sum(e => e.Count));
    }

    [Fact]
    public async Task Sync_state_round_trips_and_a_broken_file_reads_as_none()
    {
        var path = PlayCountSync.StatePath(_root, PlayCountImportSource.LastFm);
        Assert.EndsWith(Path.Combine("cache", "playcount_sync_lastfm.json"), path);
        var state = new PlayCountSyncState
        {
            Source = PlayCountImportSource.LastFm, User = "me", SyncedTo = T0,
            Entries = { new SourcePlayCount("Art", "A", null, 3), new SourcePlayCount("Ärt", "Ü", "Al", 1) },
            Recent = { new PlayCountSyncListen(T0 - 5, "k") },
        };
        await PlayCountSync.SaveStateAsync(path, state);
        var back = await PlayCountSync.LoadStateAsync(path);
        Assert.NotNull(back);
        Assert.True(back!.Continues(PlayCountImportSource.LastFm, "ME", ""));
        Assert.Equal(state.Entries, back.Entries);
        Assert.Equal(state.Recent, back.Recent);

        await File.WriteAllTextAsync(path, "{ not json");
        Assert.Null(await PlayCountSync.LoadStateAsync(path));
        Assert.Null(await PlayCountSync.LoadStateAsync(Path.Combine(_root, "missing.json")));
    }

    // ── Settings › Integrations end to end ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), what);
    }

    // Awaited, never .Result: blocking the headless UI thread on an async load deadlocks it.
    private static async Task UntilSaved(PersistenceService persistence, Func<AppSettings, bool> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition(await persistence.LoadSettingsAsync())) return;
            await Task.Delay(10);
        }
        Assert.Fail(what);
    }

    [AvaloniaFact]
    public async Task Turning_sync_on_imports_then_later_syncs_add_only_new_plays_and_keep_local_counts()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var scrobbles = new List<Scrobble>
        {
            new(now - 3000, "Art", "Song A", "Al"), new(now - 2000, "art", "song a", "Al"), new(now - 1000, "Art", "Song A", "Al"),
            new(now - 900, "Art", "Song B", "Al"), new(now - 800, "Art", "Song B", "Al"),
        };
        var requests = new List<Uri>();
        var lib = new FakeLibraryService();
        var a = new Track { Title = "Song A", Artist = "Art", AlbumArtist = "Art", Album = "Al", FilePath = "C:/m/a.flac" };
        var b = new Track { Title = "Song B", Artist = "Art", AlbumArtist = "Art", Album = "Al", FilePath = "C:/m/b.flac", PlayCount = 10 };
        lib.TrackList.AddRange(new[] { a, b });
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var vm = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.PlayCountSyncDataRoot = Path.Combine(_root, "data");
        vm.PlayCountImportHttp = Http(r => LastFmReply(r, scrobbles), requests);
        vm.LastFmUsername = "tester";
        vm.IsLastFmConnected = true;
        Assert.False(vm.LastFmPlayCountSync); // off by default
        Assert.Equal(PlayCountImportMode.KeepHigher, vm.PlayCountImportMode);

        // Turning it on runs the first sync straight away: the whole history.
        vm.LastFmPlayCountSync = true;
        await Until(() => requests.Count == 1 && !vm.IsPlayCountImportRunning, "first sync ran");
        Assert.Equal(3, a.PlayCount);
        Assert.Equal(10, b.PlayCount); // played more in Noctis: kept
        Assert.Equal(Loc.T("Settings.PlayCountImport.Done", "1", "2", "0"), vm.LastFmImportStatus);
        Assert.True(File.Exists(PlayCountSync.StatePath(vm.PlayCountSyncDataRoot, PlayCountImportSource.LastFm)));
        await UntilSaved(persistence, s => s is { LastFmPlayCountSync: true, LastFmPlayCountSyncedUtc: not null },
            "toggle and sync time saved");

        // The next startup, even minutes later (GitHub #182: every startup): an incremental read
        // that finds nothing new and changes nothing.
        await vm.SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "startup");
        Assert.Equal(2, requests.Count);
        Assert.Contains("from=", requests[1].Query);
        Assert.Equal(3, a.PlayCount);
        Assert.Equal(Loc.T("Settings.PlayCountImport.Synced", "0", "0"), vm.LastFmImportStatus);

        // A new scrobble (sent late, so dated before the first sync), then a sync as on
        // connecting: only the new play is read and added.
        scrobbles.Add(new(now - 10, "Art", "Song A", "Al"));
        await vm.SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "connected");
        Assert.Equal(3, requests.Count);
        Assert.Contains("from=", requests[2].Query);
        Assert.Equal(4, a.PlayCount);
        Assert.Equal(10, b.PlayCount);
        Assert.Equal(Loc.T("Settings.PlayCountImport.Synced", "1", "1"), vm.LastFmImportStatus);

        // "Use the imported count" is the one mode that may lower a count, and it is saved.
        vm.PlayCountImportMode = PlayCountImportMode.Replace;
        await vm.SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "startup");
        Assert.Equal(2, b.PlayCount);
        Assert.Equal(4, a.PlayCount);
        await UntilSaved(persistence, s => s.PlayCountImportMode == "Replace", "mode saved");

        // Off, or not connected: the background sync does nothing.
        vm.IsLastFmConnected = false;
        await vm.SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "startup");
        Assert.Equal(4, requests.Count); // the four syncs above, nothing since
    }

    [AvaloniaFact]
    public async Task Background_sync_is_skipped_while_another_sync_or_import_runs()
    {
        var requests = new List<Uri>();
        var release = new TaskCompletionSource();
        var lib = new FakeLibraryService();
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var vm = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.PlayCountSyncDataRoot = Path.Combine(_root, "data");
        vm.PlayCountImportHttp = new HttpClient(new AnimatedArtworkChooserTests.StubHandler(async r =>
        {
            lock (requests) requests.Add(r.RequestUri!);
            await release.Task; // the first read hangs until released
            return LastFmReply(r, Array.Empty<Scrobble>());
        }));
        vm.LastFmUsername = "tester";
        vm.IsLastFmConnected = true;
        vm.LastFmPlayCountSync = true; // starts sync #1, which hangs on its read
        await Until(() => vm.IsLastFmImportRunning && requests.Count == 1, "first sync reading");

        await vm.SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "startup");
        Assert.Single(requests); // skipped, not queued and not overlapping

        release.SetResult();
        await Until(() => !vm.IsPlayCountImportRunning, "first sync finished");
        Assert.Single(requests);
    }

    [Fact]
    public void Mode_setting_parses_and_falls_back_to_keep_higher()
    {
        Assert.Equal(PlayCountImportMode.Replace, SettingsViewModel.ParsePlayCountImportMode("Replace"));
        Assert.Equal(PlayCountImportMode.KeepHigher, SettingsViewModel.ParsePlayCountImportMode("zz"));
        Assert.Equal(PlayCountImportMode.KeepHigher, SettingsViewModel.ParsePlayCountImportMode("7"));
        Assert.Equal(PlayCountImportMode.KeepHigher, SettingsViewModel.ParsePlayCountImportMode(null));
        Assert.Equal("KeepHigher", new AppSettings().PlayCountImportMode);
        Assert.False(new AppSettings().LastFmPlayCountSync);
        Assert.False(new AppSettings().ListenBrainzPlayCountSync);
    }
}
