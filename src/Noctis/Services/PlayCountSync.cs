using System.Globalization;
using System.Text.Json;

namespace Noctis.Services;

// GitHub #160 follow-up: "Keep play counts in sync". The first sync (and every manual Import)
// reads the whole history; later syncs read only what was scrobbled since, and add it to the
// totals kept from the last run. The totals are always the account's WHOLE-history counts, so
// applying them with "keep the higher" never counts a Noctis play twice (Noctis's own plays are
// scrobbled, so they are already inside the totals).
//
// Late scrobbles: services accept listens dated in the past (Last.fm up to 14 days; offline
// phones and scrobble queues send them late), so each incremental read reaches back
// <see cref="Overlap"/> before the last sync and skips the listens it already counted, which
// are remembered (time + song) for that window.

/// <summary>One listen remembered for the overlap window: unix time and folded song key.</summary>
public sealed record PlayCountSyncListen(long Ts, string Key);

/// <summary>
/// What the last sync read, saved as JSON in cache/ (so Reset everything clears it): whose
/// history, up to when, the whole-history totals, and the listens inside the overlap window.
/// </summary>
public sealed class PlayCountSyncState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public PlayCountImportSource Source { get; set; }
    public string User { get; set; } = "";
    /// <summary>ListenBrainz server (normalised); empty for Last.fm.</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>History was read up to (excluding) this unix time.</summary>
    public long SyncedTo { get; set; }
    public List<SourcePlayCount> Entries { get; set; } = new();
    public List<PlayCountSyncListen> Recent { get; set; } = new();

    /// <summary>Whether these totals can be continued for this account (else: read it all again).</summary>
    public bool Continues(PlayCountImportSource source, string user, string apiUrl)
        => Version == CurrentVersion && SyncedTo > 0 && Source == source &&
           string.Equals(User, user, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(ApiUrl, apiUrl, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What one sync read.</summary>
public sealed record PlayCountSyncResult(PlayCountSyncState State, bool Incremental, int NewListens);

/// <summary>Runs one sync (full or incremental) and loads/saves its state file.</summary>
public sealed class PlayCountSync
{
    /// <summary>How far back an incremental read reaches before the last sync (late scrobbles).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly PlayCountHistoryFetcher _fetcher;

    public PlayCountSync(PlayCountHistoryFetcher fetcher) => _fetcher = fetcher;

    /// <summary>
    /// Reads the history (only what's new when <paramref name="previous"/> continues for this
    /// account, else all of it) and returns the new state with the whole-history totals.
    /// </summary>
    public async Task<PlayCountSyncResult> RunAsync(PlayCountImportSource source, string user,
        string? listenBrainzApiUrl, string? listenBrainzToken, PlayCountSyncState? previous,
        IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
    {
        var apiUrl = source == PlayCountImportSource.ListenBrainz
            ? (ListenBrainzService.NormalizeApiUrl(listenBrainzApiUrl) ?? ListenBrainzService.DefaultApiUrl)
            : "";
        var to = _fetcher.NowUnix();
        var overlap = (long)Overlap.TotalSeconds;
        var incremental = previous != null && previous.Continues(source, user, apiUrl);

        var totals = new Dictionary<(string Artist, string Title, string Album), int>(KeyComparer.Instance);
        var recent = new Dictionary<string, long>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal); // by the last sync
        long? from = null;
        if (incremental)
        {
            foreach (var e in previous!.Entries)
            {
                var k = (e.Artist, e.Title, e.Album ?? "");
                totals[k] = totals.TryGetValue(k, out var c) ? c + e.Count : e.Count;
            }
            foreach (var l in previous.Recent)
            {
                recent[l.Key] = l.Ts;
                counted.Add(l.Key);
            }
            from = Math.Max(0, Math.Min(previous.SyncedTo, to) - overlap);
        }

        var added = 0;
        await _fetcher.ReadListensAsync(source, user, listenBrainzApiUrl, listenBrainzToken, from, to,
            (ts, artist, title, album) =>
            {
                artist = artist.Trim();
                title = title.Trim();
                album = album.Trim();
                if (artist.Length == 0 || title.Length == 0) return false;
                var key = ListenKey(ts, artist, title, album);
                if (counted.Contains(key)) return false;
                if (ts >= to - overlap) recent[key] = ts;
                var k = (artist, title, album);
                totals[k] = totals.TryGetValue(k, out var c) ? c + 1 : 1;
                added++;
                return true;
            }, progress, ct);

        var state = new PlayCountSyncState
        {
            Source = source,
            User = user,
            ApiUrl = apiUrl,
            SyncedTo = to,
            Entries = totals.Select(kv => new SourcePlayCount(kv.Key.Artist, kv.Key.Title,
                kv.Key.Album.Length == 0 ? null : kv.Key.Album, kv.Value)).ToList(),
            Recent = recent.Where(kv => kv.Value >= to - overlap)
                .Select(kv => new PlayCountSyncListen(kv.Value, kv.Key)).ToList(),
        };
        return new PlayCountSyncResult(state, incremental, added);
    }

    internal static string ListenKey(long ts, string artist, string title, string album)
        => ts.ToString(CultureInfo.InvariantCulture) + "\u001f" + artist.ToLowerInvariant() + "\u001f" +
           title.ToLowerInvariant() + "\u001f" + album.ToLowerInvariant();

    /// <summary>cache/playcount_sync_{source}.json under the data folder.</summary>
    public static string StatePath(string dataRoot, PlayCountImportSource source)
        => Path.Combine(dataRoot, "cache", $"playcount_sync_{source.ToString().ToLowerInvariant()}.json");

    /// <summary>The saved state, or null (none yet, unreadable: the next sync reads everything).</summary>
    public static async Task<PlayCountSyncState?> LoadStateAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PlayCountSyncState>(fs, JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLog.Write("PlayCountImport", $"sync state unreadable, reading the whole history: {ex.Message}");
            return null;
        }
    }

    /// <summary>Written to a temp file and moved over, so a crash mid-write leaves the old one.</summary>
    public static async Task SaveStateAsync(string path, PlayCountSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, state, JsonOptions);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string, string, string) x, (string, string, string) y)
            => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string, string) k)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item3));
    }
}
