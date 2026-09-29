using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// Desktop songs' lyrics for the lyrics page. Each song's answer from getNoctisLyrics is saved as
/// <c>&lt;account dir&gt;/lyrics/&lt;32 hex&gt;.json</c> (no-backup, written atomically), so a
/// downloaded song keeps its lyrics with the desktop off. A saved answer is current until the
/// next sync; after that the first lookup asks the desktop once more. Downloaded songs with
/// nothing saved are fetched after each sync and each download batch.
/// </summary>
public sealed partial class NoctisAccountService : IRemoteLyricsSource
{
    private const int LyricsMemoryEntries = 8;
    private const int LyricsPrefetchConcurrency = 2;
    /// <summary>Largest saved-lyrics file read back: five capped texts plus JSON escaping.</summary>
    private const long MaxLyricsFileBytes = 16L * 1024 * 1024;
    /// <summary>After the desktop fails to answer, lookups use saved copies only for this long,
    /// so a phone away from home does not wait out a timeout on every track change.</summary>
    private static readonly TimeSpan LyricsQuietAfterFailure = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions LyricsJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The phone's own file, never HTML: keep lyrics in any script readable and small.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record LyricsEntry(Guid Id, DateTime FetchedUtc, RemoteLyrics Lyrics);

    private sealed class LyricsFileJson
    {
        public DateTime FetchedUtc { get; set; }
        public string? Ttml { get; set; }
        public string? Elrc { get; set; }
        public string? Lrc { get; set; }
        public string? Synced { get; set; }
        public string? Plain { get; set; }
    }

    // _lyricsGate guards _lyricsRecent and _lyricsQuietUntil. Never taken with _gate held.
    private readonly object _lyricsGate = new();
    private readonly LinkedList<LyricsEntry> _lyricsRecent = new();
    private DateTime _lyricsQuietUntil;
    private readonly SemaphoreSlim _lyricsPrefetch = new(1, 1);

    private string LyricsDir => Path.Combine(_store.Directory, "lyrics");

    /// <inheritdoc />
    public RemoteLyrics? GetLyrics(Guid trackId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // Sync over async on purpose: the lyrics loader's background thread, never the UI's.
            // Every await below is ConfigureAwait(false), so nothing needs this thread back.
            return GetLyricsAsync(trackId, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"lyrics lookup failed ({ex.GetType().Name}); using the saved copy");
            return NullIfEmpty(SavedLyrics(trackId)?.Lyrics);
        }
    }

    /// <inheritdoc />
    public string? DownloadedPath(Guid trackId) =>
        _downloaded.TryGetValue(trackId, out var file) && File.Exists(file.Path) ? file.Path : null;

    /// <summary>
    /// The saved copy when current; else the desktop's answer (saved), else the saved copy however
    /// old. Never throws for a server, network or timeout failure.
    /// </summary>
    internal async Task<RemoteLyrics?> GetLyricsAsync(Guid trackId, CancellationToken ct)
    {
        if (trackId == Guid.Empty) return null;
        var saved = SavedLyrics(trackId);
        if (saved is not null && IsCurrent(saved.FetchedUtc)) return NullIfEmpty(saved.Lyrics);

        NoctisServerClient client;
        lock (_gate)
        {
            if (_account is null) return NullIfEmpty(saved?.Lyrics);
            client = GetClientLocked();
        }
        lock (_lyricsGate)
        {
            if (UtcNow < _lyricsQuietUntil) return NullIfEmpty(saved?.Lyrics);
        }

        try
        {
            var fresh = await client.GetLyricsAsync(trackId, ct).ConfigureAwait(false);
            SaveLyrics(trackId, fresh);
            return NullIfEmpty(fresh);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
        {
            // The desktop answered, with nothing to give: a song it no longer has, or a Noctis too
            // old for getNoctisLyrics. Keep what is saved and do not ask again before the next sync.
            var keep = saved?.Lyrics ?? RemoteLyrics.Empty;
            SaveLyrics(trackId, keep);
            return NullIfEmpty(keep);
        }
        catch (Exception ex) when (ex is NoctisServerException or OperationCanceledException or HttpRequestException or IOException)
        {
            // Unreachable, too slow, certificate changed, key revoked: the saved copy for now (the
            // next sync deals with a revoked key or a changed certificate).
            lock (_lyricsGate) _lyricsQuietUntil = UtcNow + LyricsQuietAfterFailure;
            DebugLog.Write("Account", $"lyrics: the desktop did not answer ({(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}); using the saved copy");
            return NullIfEmpty(saved?.Lyrics);
        }
    }

    /// <summary>Fetched since the last sync (or never synced), and not stamped in the future.</summary>
    private bool IsCurrent(DateTime fetchedUtc)
    {
        if (fetchedUtc > UtcNow + TimeSpan.FromMinutes(5)) return false; // the clock went back: ask again
        var lastSync = _account?.LastSyncUtc;
        return lastSync is null || fetchedUtc >= lastSync.Value;
    }

    private static RemoteLyrics? NullIfEmpty(RemoteLyrics? lyrics) => lyrics is null || lyrics.IsEmpty ? null : lyrics;

    /// <summary>
    /// Downloaded desktop songs with no saved lyrics get them now (one run at a time), so they show
    /// offline even if they were never played online. Best effort: a desktop that stops answering
    /// ends the run (lookups go quiet), anything else skips the song.
    /// </summary>
    internal async Task PrefetchDownloadedLyricsAsync(CancellationToken ct)
    {
        if (_account is null) return;
        await _lyricsPrefetch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var due = _downloaded.Keys.Where(id => !File.Exists(LyricsPath(id))).ToList();
            if (due.Count == 0) return;
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = LyricsPrefetchConcurrency, CancellationToken = ct },
                async (id, token) => await GetLyricsAsync(id, token).ConfigureAwait(false)).ConfigureAwait(false);
            DebugLog.Write("Account", $"lyrics: {due.Count(id => File.Exists(LyricsPath(id)))} of {due.Count} downloaded songs saved");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"lyrics prefetch failed ({ex.GetType().Name})");
        }
        finally
        {
            _lyricsPrefetch.Release();
        }
    }

    /// <summary>The newest saved copy: the in-memory few first, then the file.</summary>
    private LyricsEntry? SavedLyrics(Guid id)
    {
        lock (_lyricsGate)
        {
            for (var node = _lyricsRecent.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != id) continue;
                _lyricsRecent.Remove(node);
                _lyricsRecent.AddFirst(node);
                return node.Value;
            }
        }
        var entry = ReadSavedLyrics(id);
        if (entry is not null) Remember(entry);
        return entry;
    }

    private void Remember(LyricsEntry entry)
    {
        lock (_lyricsGate)
        {
            for (var node = _lyricsRecent.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != entry.Id) continue;
                // A slower reader must not put back an older copy over a fresh fetch.
                if (node.Value.FetchedUtc > entry.FetchedUtc) return;
                _lyricsRecent.Remove(node);
                break;
            }
            _lyricsRecent.AddFirst(entry);
            while (_lyricsRecent.Count > LyricsMemoryEntries) _lyricsRecent.RemoveLast();
        }
    }

    private void SaveLyrics(Guid id, RemoteLyrics lyrics)
    {
        var entry = new LyricsEntry(id, UtcNow, lyrics.Cleaned());
        Remember(entry);
        if (_account is null) return; // signed out meanwhile: nothing is kept for the old account
        var path = LyricsPath(id);
        var tmp = Path.Combine(LyricsDir, $"{id:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(LyricsDir);
            var json = JsonSerializer.SerializeToUtf8Bytes(new LyricsFileJson
            {
                FetchedUtc = entry.FetchedUtc,
                Ttml = entry.Lyrics.Ttml,
                Elrc = entry.Lyrics.Elrc,
                Lrc = entry.Lyrics.Lrc,
                Synced = entry.Lyrics.Synced,
                Plain = entry.Lyrics.Plain,
            }, LyricsJson);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(json);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            NoctisServerClient.TryDelete(tmp);
            DebugLog.Write("Account", $"lyrics save failed ({ex.GetType().Name})");
        }
    }

    private LyricsEntry? ReadSavedLyrics(Guid id)
    {
        try
        {
            var path = LyricsPath(id);
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxLyricsFileBytes) return null;
            var file = JsonSerializer.Deserialize<LyricsFileJson>(fs, LyricsJson);
            if (file is null) return null;
            var fetched = file.FetchedUtc.Kind == DateTimeKind.Local ? file.FetchedUtc.ToUniversalTime() : DateTime.SpecifyKind(file.FetchedUtc, DateTimeKind.Utc);
            return new LyricsEntry(id, fetched, new RemoteLyrics(file.Ttml, file.Elrc, file.Lrc, file.Synced, file.Plain).Cleaned());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            DebugLog.Write("Account", $"saved lyrics unreadable ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>"&lt;lyrics dir&gt;/&lt;32 lowercase hex&gt;.json": the name comes from the Guid, never from the server.</summary>
    private string LyricsPath(Guid id)
    {
        var hex = id.ToString("N");
        if (!NoctisRemoteIds.IsLowerHex32(hex)) throw new InvalidOperationException("Unexpected track id form.");
        return Path.Combine(LyricsDir, hex + ".json");
    }

    /// <summary>Sign-out: the old account's lyrics go with it.</summary>
    private void DeleteSavedLyrics()
    {
        lock (_lyricsGate) _lyricsRecent.Clear();
        try
        {
            if (Directory.Exists(LyricsDir)) Directory.Delete(LyricsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Account", $"removing saved lyrics failed ({ex.GetType().Name})");
        }
    }
}
