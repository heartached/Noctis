using System.Diagnostics;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #160 end to end against the REAL services: a public account's full history is read
/// with <see cref="PlayCountHistoryFetcher"/>, a small library of MP3s tagged after that
/// account's most played songs is scanned, the counts are matched, applied and saved, then the
/// library is loaded again (restart) and rescanned to show the counts stay. Off by default
/// (network, minutes for a big account); set NOCTIS_LIVE_PLAYCOUNT=1 to run it, optionally
/// NOCTIS_LIVE_LASTFM_USER / NOCTIS_LIVE_LISTENBRAINZ_USER and NOCTIS_LIVE_PLAYCOUNT_DIR.
/// </summary>
public class PlayCountImportLiveProbeTests
{
    private readonly ITestOutputHelper _out;

    public PlayCountImportLiveProbeTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("NOCTIS_LIVE_PLAYCOUNT") == "1";

    [Fact]
    public Task LastFm_real_history_lands_in_the_library_and_survives_restart_and_rescan()
        => Enabled ? RunAsync(PlayCountImportSource.LastFm,
            Environment.GetEnvironmentVariable("NOCTIS_LIVE_LASTFM_USER") ?? "joanofarctan") : Task.CompletedTask;

    [Fact]
    public Task ListenBrainz_real_history_lands_in_the_library_and_survives_restart_and_rescan()
        => Enabled ? RunAsync(PlayCountImportSource.ListenBrainz,
            Environment.GetEnvironmentVariable("NOCTIS_LIVE_LISTENBRAINZ_USER") ?? "lucifer") : Task.CompletedTask;

    /// <summary>The background sync's incremental read against the real Last.fm: only the
    /// window since the last sync (minus the overlap) is read, it is added to the saved totals,
    /// and a second sync straight after adds nothing.</summary>
    [Fact]
    public async Task LastFm_real_incremental_sync_reads_only_the_window_and_never_twice()
    {
        if (!Enabled) return;
        var user = Environment.GetEnvironmentVariable("NOCTIS_LIVE_LASTFM_ACTIVE_USER") ?? "RJ";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis-Tests/1.0");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var previous = new PlayCountSyncState
        {
            Source = PlayCountImportSource.LastFm, User = user, SyncedTo = now - 7 * 86_400,
            Entries = { new SourcePlayCount("Saved Artist", "Saved Song", null, 1000) },
        };
        var from = previous.SyncedTo - (long)PlayCountSync.Overlap.TotalSeconds;

        // What Last.fm itself says is in that window.
        var probeUrl = $"{LastFmApi.ApiBase}?method=user.getrecenttracks&user={user}&limit=1&from={from}&to={now}&api_key={LastFmApi.ApiKey}&format=json";
        using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(probeUrl));
        var windowTotal = int.Parse(doc.RootElement.GetProperty("recenttracks").GetProperty("@attr").GetProperty("total").GetString()!);

        var sync = new PlayCountSync(new PlayCountHistoryFetcher(http, now: () => DateTimeOffset.FromUnixTimeSeconds(now)));
        var sw = Stopwatch.StartNew();
        var first = await sync.RunAsync(PlayCountImportSource.LastFm, user, null, null, previous, null, CancellationToken.None);
        Log($"LastFm '{user}' incremental: window {from}..{now} (21 days) service total {windowTotal}, " +
            $"read {first.NewListens} in {sw.Elapsed.TotalSeconds:F1} s, totals {first.State.Entries.Sum(e => e.Count)} " +
            $"(1000 saved + new), {first.State.Recent.Count} listens remembered for the overlap");
        Assert.True(first.Incremental);
        Assert.Equal(windowTotal, first.NewListens);
        Assert.Equal(1000 + windowTotal, first.State.Entries.Sum(e => e.Count));

        var again = await new PlayCountSync(new PlayCountHistoryFetcher(http, now: () => DateTimeOffset.FromUnixTimeSeconds(now + 60)))
            .RunAsync(PlayCountImportSource.LastFm, user, null, null, first.State, null, CancellationToken.None);
        Log($"  second sync a minute later: {again.NewListens} new listens, totals {again.State.Entries.Sum(e => e.Count)}");
        Assert.InRange(again.NewListens, 0, 1); // only a scrobble made in that very minute
    }

    private async Task RunAsync(PlayCountImportSource source, string user)
    {
        var baseDir = Environment.GetEnvironmentVariable("NOCTIS_LIVE_PLAYCOUNT_DIR") ?? Path.Combine(Path.GetTempPath(), "NoctisTests");
        // Full, OS-separator path: the library stores normalised paths and the lookups below compare them.
        var root = Path.GetFullPath(Path.Combine(baseDir, $"{source}-{DateTime.Now:yyyyMMdd-HHmmss}"));
        var music = Path.Combine(root, "music");
        Directory.CreateDirectory(music);

        // ── 1. Read the whole history from the real service ──
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis-Tests/1.0");
        var fetcher = new PlayCountHistoryFetcher(http);
        var last = default(PlayCountFetchProgress);
        var pages = 0;
        var sw = Stopwatch.StartNew();
        // NOCTIS_LIVE_PLAYCOUNT_REUSE=1 re-reads the history saved by an earlier run (iterating on
        // the library half without paging the whole account again).
        var saved = Path.Combine(baseDir, $"{source}-{user}-history.json");
        List<SourcePlayCount> history;
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_PLAYCOUNT_REUSE") == "1" && File.Exists(saved))
        {
            history = System.Text.Json.JsonSerializer.Deserialize<List<SourcePlayCount>>(File.ReadAllText(saved))!;
            last = new PlayCountFetchProgress(history.Sum(h => h.Count), history.Sum(h => h.Count));
            Log($"(history re-read from {saved})");
        }
        else
        {
            history = await fetcher.FetchAsync(source, user, null, null,
                new SyncProgress<PlayCountFetchProgress>(p => { last = p; pages++; }), CancellationToken.None);
            File.WriteAllText(saved, System.Text.Json.JsonSerializer.Serialize(history));
        }
        sw.Stop();
        var plays = history.Sum(h => h.Count);
        Log($"{source} user '{user}': {pages} pages in {sw.Elapsed.TotalSeconds:F0} s, {plays:N0} listens read " +
            $"(service total {last.Total:N0}), {history.Count:N0} distinct artist/title/album entries");
        Assert.True(plays > 0);
        if (last.Total > 0) Assert.InRange(plays, (int)(last.Total * 0.98), last.Total); // blank artist/title skipped

        // Top songs by artist+title (summed over albums), their most played album.
        var top = history
            .GroupBy(h => (h.Artist.ToLowerInvariant(), h.Title.ToLowerInvariant()))
            .Select(g => (Artist: g.First().Artist, Title: g.First().Title,
                          Album: g.Where(x => x.Album != null).OrderByDescending(x => x.Count).FirstOrDefault()?.Album ?? "Single",
                          Plays: g.Sum(x => x.Count)))
            .Where(s => s.Title.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .OrderByDescending(s => s.Plays)
            .Take(6)
            .ToList();
        foreach (var s in top) Log($"  top: {s.Artist} - {s.Title} [{s.Album}] = {s.Plays}");

        // ── 2. A small library tagged after them, with the tag variants real files carry ──
        var files = new List<(string Path, string Expect, int NaivePlays)>();
        for (var i = 0; i < top.Count; i++)
        {
            var s = top[i];
            var (artist, title, label) = i switch
            {
                1 => (s.Artist, s.Title.ToUpperInvariant(), "UPPER-CASE title"),
                2 => (s.Artist + " feat. Somebody Else", s.Title, "feat. in artist"),
                3 => (s.Artist, s.Title + " (2011 Remaster)", "remaster suffix"),
                _ => (s.Artist, s.Title, "exact tags"),
            };
            files.Add((WriteMp3(music, $"{i:00}", artist, title, s.Album), $"{label}: {artist} - {title}", s.Plays));
        }
        var unscrobbled = WriteMp3(music, "99", "Nobody Ever", "Never Scrobbled Song", "Nowhere");

        var data = Path.Combine(root, "data");
        var persistence = new PersistenceService(data);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await library.ScanAsync(new[] { music });
        if (library.Tracks.Count != files.Count + 1) Assert.Fail(ScanMissReport.Describe(library, music));

        // A song already played MORE in Noctis than the service knows: "keep the higher" keeps it.
        var localHigh = library.Tracks.Single(t => t.FilePath == files[0].Path);
        localHigh.PlayCount = files[0].NaivePlays + 50;
        var localLow = library.Tracks.Single(t => t.FilePath == files[5 % files.Count].Path);
        localLow.PlayCount = 1;
        await library.SaveTrackUserStateAsync(new[] { localHigh, localLow });
        var before = library.Tracks.ToDictionary(t => t.FilePath, t => t.PlayCount);

        // ── 3. Match + apply exactly as SettingsViewModel.ImportPlayCountsAsync does ──
        var match = PlayCountImportMatcher.Match(library.Tracks, history);
        var changed = PlayCountImportMatcher.Apply(match, PlayCountImportMode.KeepHigher);
        await library.SaveTrackUserStateAsync(changed);
        Log($"matched {match.MatchedEntries:N0} of {match.SourceEntries:N0} entries ({match.MatchedPlays:N0} plays), " +
            $"{match.UnmatchedEntries:N0} entries not in the library, {changed.Count} songs changed");

        var after = new Dictionary<string, int>();
        foreach (var f in files)
        {
            var t = library.Tracks.Single(x => x.FilePath == f.Path);
            match.Counts.TryGetValue(t, out var imported);
            Log($"  {f.Expect}: service ~{f.NaivePlays}, matched {imported}, Track.PlayCount now {t.PlayCount}");
            after[f.Path] = t.PlayCount;
            Assert.True(imported >= f.NaivePlays, $"{f.Expect}: matched {imported} < {f.NaivePlays}");
            // "Keep the higher": the matcher may find more plays than the naive sum (feat./edition
            // variants of the same song), so the local +50 only wins when it is still higher.
            Assert.Equal(Math.Max(before[f.Path], imported), t.PlayCount);
        }
        Assert.Equal(0, library.Tracks.Single(t => t.FilePath == unscrobbled).PlayCount);

        // ── 4. Restart: a fresh LibraryService over the same data folder ──
        var reloaded = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await reloaded.LoadAsync();
        foreach (var f in files)
            Assert.Equal(after[f.Path], reloaded.Tracks.Single(x => x.FilePath == f.Path).PlayCount);
        Log("after restart (LoadAsync): every count read back the same");

        // ── 5. Rescan (files touched so their metadata is re-read) ──
        foreach (var f in files) File.SetLastWriteTimeUtc(f.Path, DateTime.UtcNow.AddMinutes(1));
        await reloaded.ScanAsync(new[] { music });
        foreach (var f in files)
            Assert.Equal(after[f.Path], reloaded.Tracks.Single(x => x.FilePath == f.Path).PlayCount);
        Log("after rescan of re-tagged-time files: every count kept");
        Log($"data folder: {root}");
    }

    private void Log(string line)
    {
        _out.WriteLine(line);
        var dir = Environment.GetEnvironmentVariable("NOCTIS_LIVE_PLAYCOUNT_DIR");
        if (dir != null) File.AppendAllText(Path.Combine(dir, "live-probe.log"), line + Environment.NewLine);
    }

    private static string WriteMp3(string dir, string name, string artist, string title, string album)
    {
        var path = Path.Combine(dir, name + ".mp3");
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title;
        f.Tag.Performers = new[] { artist };
        f.Tag.AlbumArtists = new[] { artist };
        f.Tag.Album = album;
        f.Save();
        return path;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
