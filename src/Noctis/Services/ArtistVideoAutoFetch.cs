using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// Settings › Artwork › Artist videos: the Apple Music motion video behind an artist page's
/// header, fetched when the page opens (and only while Animated Artwork is on too). Kept in the
/// album loops' cache folder (<see cref="AnimatedArtworkCache.ArtistVideoPathFor"/>), so the
/// same size cap, least-recently-opened eviction and Clear apply and the cache never grows past
/// the user's cap. Lookups queue behind the album page's fetch and share its request spacing
/// (one at a time, 2 s apart). An artist with no exact-name match or no video is not asked
/// about again for <see cref="AnimatedArtworkAutoFetcher.MissLifetime"/>; an artist whose video
/// the user removed is never fetched again. Keyed by the library's artist id; the Apple artist
/// id each one resolved to is remembered, so a revisit needs no search.
/// </summary>
public sealed class ArtistVideoAutoFetcher
{
    /// <summary>The lookup: artist name, a known Apple id (0 = search), the shared gate.</summary>
    internal delegate Task<ArtistVideoLookup> LookupFunc(string artistName, long knownArtistId, PoliteGate gate, CancellationToken ct);

    /// <summary>The download of a picked variant to a part file.</summary>
    internal delegate Task<bool> DownloadFunc(ITunesArtworkService.AnimatedArtworkVariant variant, string partPath, CancellationToken ct);

    private readonly LookupFunc _lookup;
    private readonly DownloadFunc _download;
    private readonly BulkRunLedger _misses;
    private readonly PoliteGate _gate;
    private readonly SemaphoreSlim _one;
    private readonly string? _indexPath;
    private readonly object _indexLock = new();
    private Dictionary<Guid, long>? _index;

    /// <summary>The cache files; its block list holds the artists not to fetch for.</summary>
    public AnimatedArtworkCache Cache { get; }

    internal ArtistVideoAutoFetcher(
        LookupFunc lookup, DownloadFunc download, AnimatedArtworkCache cache, string? missLedgerPath, string? indexPath,
        PoliteGate gate, SemaphoreSlim one, Func<DateTime>? clock = null)
    {
        _lookup = lookup;
        _download = download;
        Cache = cache;
        _indexPath = indexPath;
        _gate = gate;
        _one = one;
        _misses = new BulkRunLedger(missLedgerPath, clock) { EntryLifetime = AnimatedArtworkAutoFetcher.MissLifetime };
    }

    /// <summary>The app's wiring; every file under the data folder. Queues and paces with
    /// <paramref name="albums"/> (the album pages' fetcher) when given.</summary>
    public static ArtistVideoAutoFetcher For(ITunesArtworkService itunes, IPersistenceService persistence, AnimatedArtworkAutoFetcher? albums)
        => new(
            (name, id, gate, ct) => itunes.FindArtistVideoAsync(name, id, gate, ct),
            (variant, part, ct) => itunes.DownloadHlsVariantAsMp4Async(variant, part, ct),
            new AnimatedArtworkCache(persistence, Path.Combine(persistence.DataDirectory, BlockListFile)),
            Path.Combine(persistence.DataDirectory, MissLedgerFile),
            Path.Combine(persistence.DataDirectory, IndexFile),
            albums?.Gate ?? new PoliteGate(AnimatedArtworkAutoFetcher.RequestInterval),
            albums?.Turn ?? new SemaphoreSlim(1, 1));

    // Data folder files (Settings › Reset Everything deletes them).
    internal const string BlockListFile = "artist_video_no_autofetch.json";
    internal const string MissLedgerFile = "artist_video_autofetch_ledger.json";
    internal const string IndexFile = "artist_video_index.json";

    /// <summary>The Apple artist id this artist resolved to before, 0 when none.</summary>
    public long KnownAppleId(Guid artistId)
    {
        lock (_indexLock) return Index().TryGetValue(artistId, out var id) ? id : 0;
    }

    /// <summary>The artist's cached video (marked used), or null. No network.</summary>
    public string? CachedPath(Guid artistId)
    {
        if (Cache.IsBlocked(artistId)) return null;
        var appleId = KnownAppleId(artistId);
        if (appleId <= 0) return null;
        var path = Cache.ArtistVideoPathFor(appleId);
        if (!File.Exists(path)) return null;
        Cache.TouchFile(path);
        return path;
    }

    /// <summary>
    /// The artist's video: the cached one, else looked up and downloaded now. Null when the
    /// artist is blocked, was a miss recently, has no exact match or no video, or anything
    /// failed. Never throws except for cancellation.
    /// </summary>
    public async Task<string?> FetchAsync(Guid artistId, string artistName, long capBytes, CancellationToken ct)
    {
        if (CachedPath(artistId) is { } cached) return cached;
        if (Cache.IsBlocked(artistId) || _misses.CheckedRecently(artistId, 0)) return null;

        await _one.WaitAsync(ct).ConfigureAwait(false);
        string? part = null;
        try
        {
            // Another visit may have fetched (or removed) it while this one waited.
            if (CachedPath(artistId) is { } now) return now;
            if (Cache.IsBlocked(artistId) || _misses.CheckedRecently(artistId, 0)) return null;

            ArtistVideoLookup found;
            try
            {
                found = await _lookup(artistName, KnownAppleId(artistId), _gate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DebugLog.Write(ITunesArtworkService.LogSource, $"artist video: {artistName}: {ex.Message}");
                return null;
            }

            if (found.ArtistId > 0) Remember(artistId, found.ArtistId);
            if (found.Outcome != ArtistVideoOutcome.Found || found.Variant is null)
            {
                // Offline is not a miss: asked again on the next visit.
                if (found.Outcome is ArtistVideoOutcome.NoArtist or ArtistVideoOutcome.NoVideo)
                {
                    _misses.Record(artistId, 0);
                    _misses.Save();
                }
                return null;
            }

            Directory.CreateDirectory(Cache.Folder);
            var path = Cache.ArtistVideoPathFor(found.ArtistId);
            part = path + ".part";
            await _gate.WaitTurnAsync(ct).ConfigureAwait(false);
            if (!await _download(found.Variant, part, ct).ConfigureAwait(false)) return null;

            // Removed (and blocked) on the page while the download ran.
            if (Cache.IsBlocked(artistId)) return null;
            Cache.CommitFile(path, part);
            Cache.Trim(capBytes);
            return File.Exists(path) ? path : null;
        }
        finally
        {
            try { if (part != null && File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            _one.Release();
        }
    }

    /// <summary>
    /// The page's Remove Artist Video: never fetched again for this artist, and the cached
    /// file deleted (retried briefly: the hero may still be letting go of it).
    /// </summary>
    public async Task RemoveAsync(Guid artistId)
    {
        Cache.Block(artistId);
        var appleId = KnownAppleId(artistId);
        if (appleId <= 0) return;
        var path = Cache.ArtistVideoPathFor(appleId);
        for (var attempt = 0; attempt < 20 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private void Remember(Guid artistId, long appleId)
    {
        lock (_indexLock)
        {
            var index = Index();
            if (index.TryGetValue(artistId, out var had) && had == appleId) return;
            index[artistId] = appleId;
            if (_indexPath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
                var tmp = _indexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(index));
                File.Move(tmp, _indexPath, overwrite: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write(ITunesArtworkService.LogSource, $"artist video: index not saved: {ex.Message}");
            }
        }
    }

    private Dictionary<Guid, long> Index()
    {
        if (_index != null) return _index;
        _index = new Dictionary<Guid, long>();
        try
        {
            if (_indexPath != null && File.Exists(_indexPath))
                _index = JsonSerializer.Deserialize<Dictionary<Guid, long>>(File.ReadAllText(_indexPath)) ?? new();
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"artist video: index unreadable: {ex.Message}");
        }
        return _index;
    }
}
