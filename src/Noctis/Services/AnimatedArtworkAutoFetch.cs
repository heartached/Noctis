using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// The animated covers fetched on opening an album (GitHub #167: "like Apple Music: fetch on
/// view, keep a temporary cache"), in <c>animated_covers/cache</c> under the data folder. Size
/// capped: past the cap the least recently opened loops go first (a file's write time is its
/// last use: set when it is saved and each time its album is opened). Covers the user saved —
/// picked in the editor or by Find Animated Artwork — live one folder up and are never counted
/// or evicted. Also keeps the albums the user said not to fetch for again.
/// </summary>
public sealed class AnimatedArtworkCache
{
    private readonly IPersistenceService _persistence;
    private readonly string? _blockListPath;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private HashSet<Guid>? _blocked;

    /// <param name="blockListPath">JSON list of albums never to fetch for; null keeps it in memory.</param>
    public AnimatedArtworkCache(IPersistenceService persistence, string? blockListPath, Func<DateTime>? now = null)
    {
        _persistence = persistence;
        _blockListPath = blockListPath;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Folder => Path.GetDirectoryName(PathFor(Guid.Empty)) ?? string.Empty;

    public string PathFor(Guid albumId) => _persistence.GetCachedAnimatedCoverPath(albumId);

    /// <summary>Where a download lands until it is complete; not an .mp4, so nothing plays it half-written.</summary>
    internal string PartPathFor(Guid albumId) => PathFor(albumId) + ".part";

    /// <summary>Marks the album's cached loop as just used, so eviction takes it last.</summary>
    public void Touch(Guid albumId)
    {
        var path = PathFor(albumId);
        try { if (File.Exists(path)) File.SetLastWriteTimeUtc(path, _now()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Moves a finished download into the cache as the album's loop.</summary>
    internal string Commit(Guid albumId, string partPath)
    {
        var path = PathFor(albumId);
        File.Move(partPath, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, _now());
        return path;
    }

    private IEnumerable<FileInfo> Loops()
    {
        var dir = new DirectoryInfo(Folder);
        return dir.Exists ? dir.EnumerateFiles("*.mp4") : Enumerable.Empty<FileInfo>();
    }

    /// <summary>Bytes the cached loops take up.</summary>
    public long SizeBytes()
    {
        try { return Loops().Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Deletes the least recently used loops until the cache fits in <paramref name="capBytes"/>.
    /// A loop that is playing right now (held open) is skipped and the next one goes instead.
    /// Returns how many were deleted.
    /// </summary>
    public int Trim(long capBytes)
    {
        List<FileInfo> files;
        try { files = Loops().OrderBy(f => f.LastWriteTimeUtc).ToList(); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        var total = files.Sum(f => f.Length);
        var deleted = 0;
        foreach (var f in files)
        {
            if (total <= Math.Max(0, capBytes)) break;
            // Length before Delete: a deleted FileInfo re-reads its state and throws.
            var length = f.Length;
            try
            {
                f.Delete();
                total -= length;
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    /// <summary>Settings › Clear: every cached loop and any download left half-done.</summary>
    public int Clear()
    {
        var deleted = Trim(0);
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (dir.Exists)
                foreach (var part in dir.EnumerateFiles("*.part"))
                    try { part.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return deleted;
    }

    // ── Albums not to fetch for ("Remove Animated Artwork" on the album page) ──

    public bool IsBlocked(Guid albumId)
    {
        lock (_gate) return Blocked().Contains(albumId);
    }

    public void Block(Guid albumId)
    {
        lock (_gate)
        {
            if (Blocked().Add(albumId)) SaveBlocked();
        }
    }

    public void Unblock(Guid albumId)
    {
        lock (_gate)
        {
            if (Blocked().Remove(albumId)) SaveBlocked();
        }
    }

    private HashSet<Guid> Blocked()
    {
        if (_blocked != null) return _blocked;
        _blocked = new HashSet<Guid>();
        try
        {
            if (_blockListPath != null && File.Exists(_blockListPath))
                _blocked = JsonSerializer.Deserialize<HashSet<Guid>>(File.ReadAllText(_blockListPath)) ?? new();
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"auto: block list unreadable: {ex.Message}");
        }
        return _blocked;
    }

    private void SaveBlocked()
    {
        if (_blockListPath == null || _blocked == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_blockListPath)!);
            var tmp = _blockListPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_blocked));
            File.Move(tmp, _blockListPath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"auto: block list not saved: {ex.Message}");
        }
    }
}

/// <summary>
/// Settings › Artwork › Fetch animated artwork when opening an album (GitHub #167). The album
/// page asks for its loop; this runs the same clear-matches-only lookup as Find Animated
/// Artwork (<see cref="AnimatedArtworkBulkService.FindAndDownloadAsync"/>) and keeps the result
/// in <see cref="AnimatedArtworkCache"/>. One lookup at a time, requests spaced out like the
/// bulk run, cancelled when the page is left; an album with no clear match is not asked about
/// again for <see cref="MissLifetime"/>, and an album the user removed a loop from never.
/// </summary>
public sealed class AnimatedArtworkAutoFetcher
{
    /// <summary>How long "the store had no clear match with a loop" is remembered.</summary>
    internal static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

    /// <summary>Spacing between two requests to Apple (the bulk run's 1.5 s, a little more).</summary>
    internal static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(2);

    private readonly AnimatedArtworkBulkService _lookup;
    private readonly IAnimatedCoverService _covers;
    private readonly BulkRunLedger _misses;
    private readonly PoliteGate _gate;
    private readonly SemaphoreSlim _one = new(1, 1);

    public AnimatedArtworkCache Cache { get; }

    public AnimatedArtworkAutoFetcher(
        AnimatedArtworkBulkService lookup, IAnimatedCoverService covers, AnimatedArtworkCache cache, string? missLedgerPath,
        Func<DateTime>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _lookup = lookup;
        _covers = covers;
        Cache = cache;
        _misses = new BulkRunLedger(missLedgerPath, clock) { EntryLifetime = MissLifetime };
        _gate = new PoliteGate(RequestInterval, clock, delay);
    }

    /// <summary>The app's wiring; every file under the data folder.</summary>
    public static AnimatedArtworkAutoFetcher For(ITunesArtworkService itunes, IPersistenceService persistence)
    {
        var covers = new AnimatedCoverService(persistence);
        return new AnimatedArtworkAutoFetcher(
            AnimatedArtworkBulkService.For(itunes, covers, ledgerPath: null),
            covers,
            new AnimatedArtworkCache(persistence, Path.Combine(persistence.DataDirectory, "animated_artwork_no_autofetch.json")),
            Path.Combine(persistence.DataDirectory, "animated_artwork_autofetch_ledger.json"));
    }

    /// <summary>
    /// Finds and caches the album's loop. Returns its path when one was fetched now; null when
    /// the album already has a cover (a cached one is marked used), is blocked, was a miss
    /// recently, or has no clear match. Never throws except for cancellation.
    /// </summary>
    public Task<string?> FetchAsync(IReadOnlyList<Track> albumTracks, long capBytes, CancellationToken ct)
        => FetchAsync(albumTracks, capBytes, preferMax: false, ct);

    /// <param name="preferMax">GitHub #185 (Settings › Artwork › Animated Artwork Quality):
    /// fetch the 2160p loop, or the 1080p one when the album has no 2160p loop.</param>
    public async Task<string?> FetchAsync(IReadOnlyList<Track> albumTracks, long capBytes, bool preferMax, CancellationToken ct)
    {
        var album = AnimatedArtworkBulkService.GroupAlbums(albumTracks).FirstOrDefault();
        if (album is null) return null;
        var rep = album[0];
        var id = rep.AlbumId;

        if (HasCover(rep)) return null;
        if (Cache.IsBlocked(id) || _misses.CheckedRecently(id, 0)) return null;

        await _one.WaitAsync(ct).ConfigureAwait(false);
        var part = Cache.PartPathFor(id);
        try
        {
            // Another page (or a second visit) may have fetched it while this one waited.
            if (HasCover(rep) || Cache.IsBlocked(id) || _misses.CheckedRecently(id, 0)) return null;

            Directory.CreateDirectory(Cache.Folder);
            AnimatedArtworkLookup outcome;
            try
            {
                outcome = await _lookup.FindAndDownloadAsync(album, _gate, part, preferMax, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DebugLog.Write(ITunesArtworkService.LogSource, $"auto: {rep.Album}: {ex.Message}");
                return null;
            }

            if (outcome != AnimatedArtworkLookup.Saved)
            {
                // No candidates at all is likelier "offline" than "no such album": not remembered.
                if (outcome == AnimatedArtworkLookup.NoMatch)
                {
                    _misses.Record(id, 0);
                    _misses.Save();
                }
                return null;
            }

            // Removed (and blocked) on the page while the download ran.
            if (Cache.IsBlocked(id)) return null;
            var path = Cache.Commit(id, part);
            Cache.Trim(capBytes);
            return File.Exists(path) ? path : null;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            _one.Release();
        }
    }

    private bool HasCover(Track rep)
    {
        var existing = _covers.Resolve(rep);
        if (existing is null) return false;
        if (string.Equals(existing, Cache.PathFor(rep.AlbumId), StringComparison.OrdinalIgnoreCase))
            Cache.Touch(rep.AlbumId);
        return true;
    }
}
