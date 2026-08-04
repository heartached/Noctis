using System.Collections.Concurrent;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Scans local folders for audio files, reads metadata via IMetadataService,
/// and maintains indexed collections of tracks, albums, and artists.
/// All data is persisted to JSON via IPersistenceService.
/// </summary>
public class LibraryService : ILibraryService
{
    private const int CurrentMetadataSchemaVersion = 7;
    // v3: album track order normalized (disc 0 → 1, missing track numbers last)
    private const int CurrentIndexCacheVersion = 3;
    // Throttle scan progress so a large library (tens of thousands of files)
    // doesn't post one UI update per file. Emit on the first file then every
    // Nth, which is frequent enough to read as "live" without flooding.
    private const int ProgressReportInterval = 32;
    // How often (ms) to surface scan-in-progress tracks to the library views so
    // they populate live instead of staying empty until the whole scan finishes.
    private const int ProgressivePublishMs = 1500;

    private readonly IMetadataService _metadata;
    private readonly IPersistenceService _persistence;
    private readonly ISqliteLibraryIndexService _sqliteIndex;
    private readonly IAuditTrailService _auditTrail;

    private List<Track> _tracks = new();
    private List<Album> _albums = new();
    private List<Artist> _artists = new();

    // Lookup tables for fast ID resolution
    private Dictionary<Guid, Track> _trackIndex = new();
    private Dictionary<Guid, Album> _albumIndex = new();

    // Lazy-built lookup from artist name → that artist's albums. Avoids an O(N)
    // LINQ scan in GetAlbumsByArtist (hot on AlbumDetail open and ArtistDetail).
    // Invalidated to null whenever _albums is reassigned; the next reader rebuilds.
    private volatile Dictionary<string, List<Album>>? _albumsByArtistIndex;
    private readonly object _albumsByArtistLock = new();

    // Active-scan handle for graceful-shutdown checkpointing. When _checkpointRequested
    // is set, a cancelled scan persists its partial progress (merged with the existing
    // library) instead of rolling back, so the next launch resumes where it left off.
    private CancellationTokenSource? _activeScanCts;
    private TaskCompletionSource? _scanFinished;
    private volatile bool _checkpointRequested;

    // Serializes scans. Two overlapping scans both drive _tracks, both Clear+Upsert the
    // SQLite index, and the second clobbers the first's _activeScanCts — so shutdown could
    // only cancel one of them. The startup auto-scan runs on a detached Task.Run, so
    // overlap with a user-triggered scan was reachable even though the settings VM
    // serializes its own calls.
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public IReadOnlyList<Track> Tracks => _tracks;
    public IReadOnlyList<Album> Albums => _albums;
    public IReadOnlyList<Artist> Artists => _artists;

    public event EventHandler? LibraryUpdated;
    public event EventHandler<int>? ScanProgress;
    public event EventHandler? FavoritesChanged;
    public event EventHandler<List<string>>? MusicFoldersChanged;

    /// <summary>
    /// Raised when a scan was abandoned because one or more configured music folders were
    /// unavailable (offline drive / unreachable share). Carries the missing root paths.
    /// The existing library is left untouched.
    /// </summary>
    public event EventHandler<string[]>? ScanAborted;

    public LibraryService(
        IMetadataService metadata,
        IPersistenceService persistence,
        ISqliteLibraryIndexService sqliteIndex,
        IAuditTrailService auditTrail)
    {
        _metadata = metadata;
        _persistence = persistence;
        _sqliteIndex = sqliteIndex;
        _auditTrail = auditTrail;
    }

    public async Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default)
    {
        // Register this scan so a graceful shutdown can cancel it and flush a
        // checkpoint (see PauseActiveScanForShutdownAsync). The linked source lets
        // shutdown cancel independently of the caller's own token.
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _checkpointRequested = false;
        _activeScanCts = linkedCts;
        _scanFinished = finished;
        try
        {
            await ScanCoreAsync(folders, linkedCts.Token);
        }
        finally
        {
            finished.TrySetResult();
            if (ReferenceEquals(_scanFinished, finished))
            {
                _scanFinished = null;
                _activeScanCts = null;
            }
            _scanGate.Release();
        }
    }

    private async Task ScanCoreAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        var settings = await _persistence.LoadSettingsAsync();
        // Refresh the artwork-toggle mirror from the persisted settings so even a
        // scan that starts before SettingsViewModel finishes loading honors it.
        MetadataService.UseEmbeddedArtwork = settings.UseEmbeddedArtwork;
        var includeRoots = BuildIncludeRoots(folders, settings).ToList();
        var excludedRoots = settings.FolderRules
            .Where(r => r.Enabled && !r.Include && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => TryNormalizePath(r.Path))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ignoredNames = new HashSet<string>(
            settings.IgnoredFolderNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        var excludedFiles = new HashSet<string>(
            settings.ExcludedFilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        var newTracks = new ConcurrentBag<Track>();
        // Roots that were configured but not present on disk this pass (unplugged external
        // drive, unreachable NAS, changed drive letter). Collected so the publish below can
        // refuse to treat "root is gone" as "root is empty" — see the guard after the scan.
        var unavailableRoots = new List<string>();
        // Directories whose listing failed mid-walk (permissions, transient I/O, a cloud
        // sync provider that isn't running yet). "Couldn't list" is not "empty": known
        // tracks under these are carried forward after the scan instead of being dropped.
        var failedDirs = new ConcurrentBag<string>();
        // Tracks which albums have already had a cover cached during this scan, so we
        // save each album's art exactly once (first track wins).
        var albumArtClaimed = new ConcurrentDictionary<Guid, bool>();
        var fileCount = 0;
        var unchangedCount = 0;
        var changedCount = 0;
        var skippedCount = 0;

        // Snapshot the current track index for read-only access during parallel scan
        var trackIndexSnapshot = _trackIndex;

        await _auditTrail.AppendAsync(new AuditEvent
        {
            EventType = "scan.started",
            EntityType = "library",
            EntityId = "local",
            Reason = "Library scan started",
            Details = new Dictionary<string, string>
            {
                ["includeRoots"] = includeRoots.Count.ToString(),
                ["excludedRoots"] = excludedRoots.Length.ToString()
            }
        }, ct);

        // Capture the pre-scan library so a cancelled scan can roll back the
        // progressive partial publishes below and honour "cancel = no change".
        var originalTracks = _tracks;
        var originalAlbums = _albums;
        var originalArtists = _artists;
        var originalTrackIndex = _trackIndex;
        var originalAlbumIndex = _albumIndex;
        var originalTrackCount = originalTracks.Count;
        var didPublishPartial = false;

        void RestoreOriginalLibrary()
        {
            if (!didPublishPartial) return;
            _tracks = originalTracks;
            _albums = originalAlbums;
            _artists = originalArtists;
            _trackIndex = originalTrackIndex;
            _albumIndex = originalAlbumIndex;
            lock (_albumsByArtistLock) { _albumsByArtistIndex = null; }
            LibraryUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Progressive publish: while the scan runs, periodically surface the
        // tracks found so far so the library views fill in live (Apple Music
        // style) instead of staying empty until the entire scan completes.
        // In-memory only — persistence happens once, in the final rebuild below.
        async Task RunProgressivePublishAsync(CancellationToken pubCt)
        {
            var lastCount = 0;
            try
            {
                while (!pubCt.IsCancellationRequested)
                {
                    await Task.Delay(ProgressivePublishMs, pubCt).ConfigureAwait(false);

                    var snapshot = newTracks.ToArray();
                    if (snapshot.Length == 0 || snapshot.Length == lastCount) continue;
                    lastCount = snapshot.Length;

                    _tracks = snapshot
                        .GroupBy(t => t.Id).Select(g => g.First())
                        .OrderBy(t => t.Artist).ThenBy(t => t.Album)
                        .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
                    await RebuildIndexesAsync(persistCache: false).ConfigureAwait(false);
                    didPublishPartial = true;
                    LibraryUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort; the final rebuild is authoritative */ }
        }

        using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var publishTask = RunProgressivePublishAsync(publishCts.Token);

        try
        {
        await Task.Run(() =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = GetScanParallelism(),
                CancellationToken = ct
            };

            void ReportProgress(int processed)
            {
                if (processed == 1 || processed % ProgressReportInterval == 0)
                    ScanProgress?.Invoke(this, processed);
            }

            foreach (var folder in includeRoots)
            {
                if (!Directory.Exists(folder))
                {
                    unavailableRoots.Add(folder);
                    continue;
                }

                // Enumerate recursively with folder rules, excluding removed files.
                // Stream files into processing as they're discovered (no up-front
                // ToList): on slow disks and network shares this overlaps directory
                // enumeration with metadata reads and starts reporting progress
                // immediately instead of after a long silent listing phase.
                // NoBuffering dispatches one file at a time so the count moves right away.
                var files = EnumerateAudioFiles(folder, excludedRoots, ignoredNames, failedDirs)
                    .Where(f => !excludedFiles.Contains(f));
                var partitioner = Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering);

                Parallel.ForEach(partitioner, options, filePath =>
                {
                    if (ct.IsCancellationRequested) return;

                    // Declared outside the try so the failure paths below can tell a
                    // known-but-unreadable file apart from an unreadable new file.
                    Track? existing = null;

                    try
                    {
                        // Skip files we already have that haven't changed
                        if (trackIndexSnapshot.TryGetValue(ComputeFileId(filePath), out existing))
                        {
                            var fi = new FileInfo(filePath);
                            if (fi.LastWriteTimeUtc == existing.LastModified && fi.Length == existing.FileSize)
                            {
                                newTracks.Add(existing);
                                Interlocked.Increment(ref unchangedCount);
                                ReportProgress(Interlocked.Increment(ref fileCount));
                                return;
                            }
                        }

                        // Read metadata (and the embedded cover, already in memory) for new/changed files
                        var track = _metadata.ReadTrackMetadata(filePath, out var embeddedArt);
                        if (track != null)
                        {
                            // Use file path hash as stable ID so rescans don't create duplicates
                            track.Id = ComputeFileId(filePath);

                            // Preserve user data (favorites, play count, etc.) from the
                            // old track when a file's metadata/size has changed on disk.
                            if (existing != null)
                                CopyMutableTrackState(existing, track);
                            else
                                track.SourceType = SourceType.Local;
                            newTracks.Add(track);
                            Interlocked.Increment(ref changedCount);

                            // Cache this album's cover live the first time we see it (zero extra
                            // I/O — the picture was already read above), so covers fill in with
                            // tracks during the scan. Folder-art fallback runs after the scan.
                            if (embeddedArt != null
                                && albumArtClaimed.TryAdd(track.AlbumId, true)
                                && !File.Exists(_persistence.GetArtworkPath(track.AlbumId)))
                            {
                                _persistence.SaveArtwork(track.AlbumId, embeddedArt);
                            }
                        }
                        else
                        {
                            // The file is on disk (it was just enumerated) but couldn't be
                            // parsed — locked, corrupt, or a cloud placeholder whose
                            // provider isn't running (ReadTrackMetadata swallows I/O errors
                            // and returns null). Dropping a KNOWN track here removed it
                            // from the library, and the save below made that permanent —
                            // keep the existing entry and let a later scan that can read
                            // the file refresh it.
                            if (existing != null)
                                newTracks.Add(existing);
                            Interlocked.Increment(ref skippedCount);
                        }

                        ReportProgress(Interlocked.Increment(ref fileCount));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Skip files that can't be read (locked, permissions, I/O error) —
                        // but keep the existing entry for a known file (see above).
                        if (existing != null)
                            newTracks.Add(existing);
                        Interlocked.Increment(ref skippedCount);
                        ReportProgress(Interlocked.Increment(ref fileCount));
                    }
                });
            }
        }, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is handled gracefully by the rollback below.
        }
        finally
        {
            publishCts.Cancel();
            try { await publishTask.ConfigureAwait(false); } catch { /* publisher already stopping */ }
        }

        // If scan was cancelled, either checkpoint the partial work (graceful
        // shutdown) or roll back to the pre-scan library ("cancel = no change").
        if (ct.IsCancellationRequested)
        {
            if (_checkpointRequested)
            {
                // Interrupted mid-enumeration: keep every already-known track and
                // overlay the freshly scanned ones so nothing is dropped. The next
                // scan resumes the remainder via the unchanged-file fast path.
                var merged = new Dictionary<Guid, Track>();
                foreach (var t in originalTracks) merged[t.Id] = t;
                foreach (var t in newTracks) merged[t.Id] = t;
                await PersistScanCheckpointAsync(merged.Values.ToList());
            }
            else
            {
                RestoreOriginalLibrary();
            }
            return;
        }

        // A configured root that isn't on disk right now is "unavailable", not "empty".
        // Without this guard the scan happily produced zero tracks for that root and the
        // publish below replaced the library with the remainder, then SaveAsync() wrote it
        // through — destroying every rating, favorite, play count and DateAdded for the
        // tracks that lived there. Unplugging an external drive was enough to trigger it,
        // and ScanOnStartup made it automatic. There is no backup to recover from, so the
        // only safe response is to abort the scan and keep what we already have.
        if (unavailableRoots.Count > 0)
        {
            var normalizedMissing = unavailableRoots
                .Select(TryNormalizePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToArray();

            var strandedTracks = normalizedMissing.Length == 0
                ? 0
                : originalTracks.Count(t =>
                {
                    var normalized = TryNormalizePath(t.FilePath);
                    return normalized != null &&
                           normalizedMissing.Any(root => IsUnderRoot(normalized, root));
                });

            if (strandedTracks > 0)
            {
                RestoreOriginalLibrary();
                var rootList = string.Join(", ", unavailableRoots);
                DebugLog.Write("Library",
                    $"Scan aborted: {unavailableRoots.Count} music folder(s) unavailable ({rootList}); " +
                    $"{strandedTracks} track(s) live there. Keeping the existing library rather than " +
                    "publishing a partial one.");
                await _auditTrail.AppendAsync(new AuditEvent
                {
                    EventType = "scan.aborted",
                    EntityType = "library",
                    EntityId = "local",
                    Reason = "One or more music folders were unavailable",
                    Details = new Dictionary<string, string>
                    {
                        ["unavailableRoots"] = rootList,
                        ["tracksUnderUnavailableRoots"] = strandedTracks.ToString(),
                        ["existingTrackCount"] = originalTrackCount.ToString()
                    }
                }, ct);
                ScanAborted?.Invoke(this, unavailableRoots.ToArray());
                return;
            }
        }

        // A directory that could not be LISTED is "unavailable", not "empty" — the same
        // principle as the missing-roots guard above, one level down. Losing a listing
        // (access denied, transient I/O, a cloud provider such as OneDrive that hasn't
        // started yet when the startup scan runs) made every track under that subtree
        // fall out of newTracks, and the publish + SaveAsync below removed those folders
        // from the library permanently while the files were still on disk. Carry the
        // known tracks under failed directories forward unchanged; a later scan that can
        // list them again reconciles normally, and a genuinely deleted folder (parent
        // listed fine, entry simply gone) still drops out exactly as before.
        if (!failedDirs.IsEmpty)
        {
            var scannedIds = new HashSet<Guid>(newTracks.Select(t => t.Id));
            var carried = SelectTracksUnderFailedDirectories(originalTracks, scannedIds, failedDirs);
            if (carried.Count > 0)
            {
                foreach (var t in carried)
                    newTracks.Add(t);

                var dirList = string.Join(", ", failedDirs.Distinct(StringComparer.OrdinalIgnoreCase));
                DebugLog.Write("Library",
                    $"Scan could not list {failedDirs.Count} folder(s) ({dirList}); keeping " +
                    $"{carried.Count} known track(s) under them rather than treating them as deleted.");
                await _auditTrail.AppendAsync(new AuditEvent
                {
                    EventType = "scan.subtreeUnavailable",
                    EntityType = "library",
                    EntityId = "local",
                    Reason = "One or more folders could not be enumerated; their tracks were kept",
                    Details = new Dictionary<string, string>
                    {
                        ["unlistableDirectories"] = dirList,
                        ["tracksCarriedForward"] = carried.Count.ToString()
                    }
                }, ct);
            }
        }

        // Belt-and-braces: never let a scan replace a populated library with nothing.
        // Any path that gets here with zero results and a non-empty library is a bug in
        // enumeration, not a user deleting their entire collection mid-scan.
        if (newTracks.IsEmpty && originalTrackCount > 0)
        {
            RestoreOriginalLibrary();
            DebugLog.Write("Library",
                $"Scan produced no tracks while {originalTrackCount} are already in the library — " +
                "keeping the existing library.");
            return;
        }

        // Fast path: every existing track was found and unchanged, and no new or
        // modified files were detected. Skip the destructive rebuild/persist path —
        // previously we always wiped the SQLite index and rewrote library.json on
        // every launch, which the user saw as re-indexing even when nothing changed.
        if (changedCount == 0 && unchangedCount == originalTrackCount)
        {
            // The progressive publisher may have left _tracks on a partial snapshot taken
            // before the last files were verified. Every other early exit rolls that back;
            // this one didn't — so the truncated set stayed live for the whole session, and
            // any later SaveAsync (a rating change, the per-play debounce, the shutdown
            // flush) wrote it through to library.json, silently deleting whichever folders
            // happened to be walked last. No-op when nothing was partially published.
            RestoreOriginalLibrary();
            await _auditTrail.AppendAsync(new AuditEvent
            {
                EventType = "scan.noop",
                EntityType = "library",
                EntityId = "local",
                Reason = "Library scan completed (no changes)",
                Details = new Dictionary<string, string>
                {
                    ["totalFilesProcessed"] = fileCount.ToString(),
                    ["unchanged"] = unchangedCount.ToString(),
                    ["skipped"] = skippedCount.ToString(),
                    ["finalTrackCount"] = originalTrackCount.ToString()
                }
            }, ct);

            // A no-change scan used to return without ever looking at artwork, so an
            // album whose cover was never cached (interrupted first scan, a file or
            // network mount that was unreadable during extraction) kept its
            // placeholder forever — unchanged files are skipped by mtime above, and
            // this fast path skipped the post-scan extraction pass entirely. Heal
            // those albums here; it costs one existence probe per album when there
            // is nothing to do.
            await BackfillMissingArtworkAsync(ct);
            return;
        }

        // Authoritative track set. Publish it now (cache write deferred to the final
        // rebuild) so every scanned track is on screen while album art is extracted
        // progressively below.
        // DistinctBy(Id) prevents duplicates from overlapping music folders
        // (e.g., user adds /Music and /Music/Rock — files in the overlap get scanned twice).
        _tracks = newTracks
            .GroupBy(t => t.Id).Select(g => g.First()) // deduplicate by track ID
            .OrderBy(t => t.Artist).ThenBy(t => t.Album)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        await RebuildIndexesAsync(persistCache: false);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);

        // Deterministic album-art extraction, published progressively so covers fill
        // into the views live instead of all at once at the end. Groups are complete
        // here (post-scan), and each cover comes from the album's lowest disc/track
        // representative — stable across scans.
        await ExtractArtworkProgressivelyAsync(newTracks, ct);
        if (ct.IsCancellationRequested)
        {
            if (_checkpointRequested)
                // Enumeration already completed (only artwork was interrupted), so
                // _tracks is the authoritative scanned set — persist it as the checkpoint.
                await PersistScanCheckpointAsync(_tracks.ToList());
            else
                RestoreOriginalLibrary();
            return;
        }

        // Final authoritative rebuild (persists the index cache and attaches all
        // extracted covers), then write through to disk.
        await RebuildIndexesAsync();

        // Persist to disk
        await SaveAsync();
        await _sqliteIndex.ReplaceAllAsync(_tracks, ct);

        await _auditTrail.AppendAsync(new AuditEvent
        {
            EventType = "scan.completed",
            EntityType = "library",
            EntityId = "local",
            Reason = "Library scan completed",
            Details = new Dictionary<string, string>
            {
                ["totalFilesProcessed"] = fileCount.ToString(),
                ["unchanged"] = unchangedCount.ToString(),
                ["changedOrNew"] = changedCount.ToString(),
                ["skipped"] = skippedCount.ToString(),
                ["finalTrackCount"] = _tracks.Count.ToString()
            }
        }, ct);

        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Cancels the in-flight scan and waits for it to flush a checkpoint of its
    /// partial progress, so quitting mid-scan doesn't waste the work (or, for a
    /// first scan of a new folder, lose all of it) — the next scan resumes.
    /// </summary>
    /// <summary>
    /// Cancelled when the app is shutting down. The startup metadata backfills take no
    /// token at all, so they kept hammering the disk (re-opening essentially every
    /// AAC/ALAC file with Parallel.ForEach) after the user had asked the app to quit.
    /// </summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    // Serializes "Merge Featured Artists" toggle passes: a newer flip cancels the
    // in-flight pass so rapid on/off flips can't interleave writes.
    private readonly object _mergeFeatApplyLock = new();
    private CancellationTokenSource? _mergeFeatApplyCts;

    public async Task PauseActiveScanForShutdownAsync(TimeSpan timeout)
    {
        // Stop the background backfills too, not just the scan.
        try { _shutdownCts.Cancel(); } catch { }

        var cts = _activeScanCts;
        var finished = _scanFinished;
        if (cts == null || finished == null) return;

        _checkpointRequested = true;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return; /* scan already finished */ }

        try { await finished.Task.WaitAsync(timeout); }
        catch (TimeoutException) { /* shutdown can't block forever; remainder re-scans next launch */ }
        catch { /* scan already completing */ }
    }

    /// <summary>
    /// Persists the given track set + indexes to disk and the SQLite mirror,
    /// ignoring cancellation. Used to checkpoint scan progress on shutdown so a
    /// re-scan resumes incrementally instead of starting over.
    /// </summary>
    private async Task PersistScanCheckpointAsync(List<Track> tracks)
    {
        _tracks = tracks
            .OrderBy(t => t.Artist).ThenBy(t => t.Album)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        await RebuildIndexesAsync();
        await SaveAsync();
        try
        {
            await _sqliteIndex.ReplaceAllAsync(_tracks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryService] Checkpoint SQLite sync failed: {ex.Message}");
        }
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Extracts album art for albums that don't yet have a cached cover, publishing
    /// in the background while it runs so covers fill into the views live during a
    /// scan rather than appearing all at once at the end. Deterministic: album
    /// groups are complete here (post-scan) and the cover is taken from each album's
    /// lowest disc/track representative. Static art and user-attached animated
    /// covers both surface through the LibraryUpdated notifications below.
    /// Albums that already have a cached cover cost one existence probe and no file
    /// reads. Returns the number of albums whose cover was extracted and cached.
    /// </summary>
    private async Task<int> ExtractArtworkProgressivelyAsync(IReadOnlyCollection<Track> scanned, CancellationToken ct)
    {
        var artGroups = scanned
            .Where(t => t.SourceType == SourceType.Local)
            .GroupBy(t => t.AlbumId)
            // SaveArtwork refuses the shared "Unknown Album" bucket, so probing its
            // representative would re-read the same file on every pass for nothing.
            .Where(g => g.Key != Track.UnknownAlbumBucketId)
            .Where(g => !File.Exists(_persistence.GetArtworkPath(g.Key)))
            .ToList();
        if (artGroups.Count == 0) return 0;

        // Publish loop: while art is extracted, periodically rebuild the indexes
        // (so newly saved covers attach to their albums) and notify the views.
        // Throttled by time so cover-heavy libraries don't flood the UI.
        using var pubCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task PublishLoopAsync()
        {
            try
            {
                while (!pubCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(ProgressivePublishMs, pubCts.Token).ConfigureAwait(false);
                    await RebuildIndexesAsync(persistCache: false).ConfigureAwait(false);
                    LibraryUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort; the final rebuild is authoritative */ }
        }
        var publish = PublishLoopAsync();

        var extracted = 0;
        try
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(artGroups,
                    new ParallelOptions { MaxDegreeOfParallelism = GetScanParallelism(), CancellationToken = ct },
                    g =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var rep = Album.SelectArtworkRepresentative(g.ToList());
                        if (rep == null) return;
                        var artBytes = _metadata.ExtractAlbumArt(rep.FilePath);
                        if (artBytes is { Length: > 0 })
                        {
                            _persistence.SaveArtwork(g.Key, artBytes);
                            Interlocked.Increment(ref extracted);
                        }
                    });
            }, ct);
        }
        finally
        {
            pubCts.Cancel();
            try { await publish.ConfigureAwait(false); } catch { /* publisher already stopping */ }
        }
        return extracted;
    }

    // Single-flight guard for BackfillMissingArtworkAsync: the startup heal, a
    // no-change scan and a settings-toggle flip can all request one concurrently,
    // and two passes at once would just read the same files twice.
    private int _artworkBackfillActive;

    /// <inheritdoc />
    public async Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _artworkBackfillActive, 1, 0) != 0)
            return 0;
        try
        {
            var tracks = _tracks;
            if (tracks.Count == 0) return 0;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
            var extracted = await ExtractArtworkProgressivelyAsync(tracks, linked.Token);
            if (extracted > 0 && !linked.Token.IsCancellationRequested)
            {
                // Persist the rebuilt indexes: the launch fast path restores each
                // album's ArtworkPath straight from the index cache without checking
                // disk, so an unpersisted heal would look lost again on next start.
                await RebuildIndexesAsync();
                LibraryUpdated?.Invoke(this, EventArgs.Empty);
            }
            return extracted;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _artworkBackfillActive, 0);
        }
    }

    public async Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var files = (filePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(TryNormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Where(File.Exists)
            .Where(p => MetadataService.SupportedExtensions.Contains(Path.GetExtension(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0) return;

        // Clear exclusions for files being explicitly re-imported
        var settings = await _persistence.LoadSettingsAsync();
        var excludedSet = new HashSet<string>(settings.ExcludedFilePaths, StringComparer.OrdinalIgnoreCase);
        if (excludedSet.Overlaps(files))
        {
            excludedSet.ExceptWith(files);
            settings.ExcludedFilePaths = excludedSet.ToList();
            await _persistence.SaveSettingsAsync(settings);
        }

        var trackById = _tracks.ToDictionary(t => t.Id);
        var changed = false;

        // Capture the pre-import library so a cancelled import can roll back the
        // progressive partial publishes below and honour "cancel = no change".
        var originalTracks = _tracks;
        var originalAlbums = _albums;
        var originalArtists = _artists;
        var originalTrackIndex = _trackIndex;
        var originalAlbumIndex = _albumIndex;
        var didPublishPartial = false;
        var imported = new ConcurrentBag<Track>();

        void RestoreOriginalLibrary()
        {
            if (!didPublishPartial) return;
            _tracks = originalTracks;
            _albums = originalAlbums;
            _artists = originalArtists;
            _trackIndex = originalTrackIndex;
            _albumIndex = originalAlbumIndex;
            lock (_albumsByArtistLock) { _albumsByArtistIndex = null; }
            LibraryUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Progressive publish: while the import runs, periodically surface the
        // tracks imported so far so large drops fill into the views live instead
        // of appearing all at once at the end (same pattern as ScanAsync).
        // In-memory only — persistence happens once, in the final rebuild below.
        async Task RunProgressivePublishAsync(CancellationToken pubCt)
        {
            var lastCount = 0;
            try
            {
                while (!pubCt.IsCancellationRequested)
                {
                    await Task.Delay(ProgressivePublishMs, pubCt).ConfigureAwait(false);

                    var snapshot = imported.ToArray();
                    if (snapshot.Length == 0 || snapshot.Length == lastCount) continue;
                    lastCount = snapshot.Length;

                    var merged = new Dictionary<Guid, Track>(originalTracks.Count + snapshot.Length);
                    foreach (var t in originalTracks) merged[t.Id] = t;
                    foreach (var t in snapshot) merged[t.Id] = t;
                    _tracks = merged.Values
                        .OrderBy(t => t.Artist).ThenBy(t => t.Album)
                        .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
                    await RebuildIndexesAsync(persistCache: false).ConfigureAwait(false);
                    didPublishPartial = true;
                    LibraryUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort; the final rebuild is authoritative */ }
        }

        using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var publishTask = RunProgressivePublishAsync(publishCts.Token);

        try
        {
        // Metadata/artwork reads are heavy file I/O; keep them off the caller's
        // (UI) thread so large drops don't freeze the window.
        await Task.Run(() =>
        {
        var processed = 0;
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(++processed);

            var trackId = ComputeFileId(filePath);
            trackById.TryGetValue(trackId, out var existing);

            FileInfo fi;
            try
            {
                fi = new FileInfo(filePath);
            }
            catch
            {
                continue;
            }

            if (existing != null &&
                fi.LastWriteTimeUtc == existing.LastModified &&
                fi.Length == existing.FileSize)
            {
                continue;
            }

            var track = _metadata.ReadTrackMetadata(filePath);
            if (track == null) continue;

            track.Id = trackId;
            if (existing != null)
                CopyMutableTrackState(existing, track);
            else
                track.SourceType = SourceType.Local;

            var artPath = _persistence.GetArtworkPath(track.AlbumId);
            if (!File.Exists(artPath))
            {
                var artBytes = _metadata.ExtractAlbumArt(filePath);
                if (artBytes != null)
                    _persistence.SaveArtwork(track.AlbumId, artBytes);
            }

            track.IsRecentImport = true;
            trackById[track.Id] = track;
            imported.Add(track);
            changed = true;
        }
        }, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is handled gracefully by the rollback below.
        }
        finally
        {
            publishCts.Cancel();
            try { await publishTask.ConfigureAwait(false); } catch { /* publisher already stopping */ }
        }

        if (ct.IsCancellationRequested)
        {
            RestoreOriginalLibrary();
            ct.ThrowIfCancellationRequested();
        }

        if (!changed) return;

        _tracks = trackById.Values
            .OrderBy(t => t.Artist).ThenBy(t => t.Album)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .ToList();

        await RebuildIndexesAsync();
        await SaveAsync();
        // Only the imported rows, not the whole library. This ran on every watcher batch,
        // so a single file appearing in a watched folder rewrote all ~100k rows.
        await _sqliteIndex.UpsertTracksAsync(imported, ct);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public Track? GetTrackById(Guid id)
    {
        _trackIndex.TryGetValue(id, out var track);
        return track;
    }

    public Album? GetAlbumById(Guid id)
    {
        _albumIndex.TryGetValue(id, out var album);
        return album;
    }

    public IReadOnlyList<Album> GetAlbumsByArtist(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Array.Empty<Album>();

        var index = _albumsByArtistIndex;
        if (index == null)
        {
            lock (_albumsByArtistLock)
            {
                index = _albumsByArtistIndex;
                if (index == null)
                {
                    var built = new Dictionary<string, List<Album>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in _albums)
                    {
                        if (!built.TryGetValue(a.Artist, out var list))
                        {
                            list = new List<Album>();
                            built[a.Artist] = list;
                        }
                        list.Add(a);
                    }
                    _albumsByArtistIndex = built;
                    index = built;
                }
            }
        }

        // Return a copy: the cached list is shared per-artist state and must not be
        // mutated (or aliased into mutation) by callers.
        return index.TryGetValue(artistName, out var albums) ? albums.ToArray() : Array.Empty<Album>();
    }

    public async Task RemoveTrackAsync(Guid id)
    {
        var track = GetTrackById(id);
        if (track == null) return;

        // Copy-and-swap, never mutate in place: Tracks is enumerated concurrently on
        // background threads (Home refresh, duplicate finder, watcher batches), so an
        // in-place Remove can throw "Collection was modified" under their feet.
        var updated = new List<Track>(_tracks);
        updated.Remove(track);
        _tracks = updated;
        DeleteOrphanedArtwork(new[] { track });
        await ExcludeFilePathsAndCleanFoldersAsync(new[] { track.FilePath });
        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.DeleteTracksAsync(new[] { id });
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveTracksAsync(IEnumerable<Guid> ids)
    {
        var idSet = new HashSet<Guid>(ids);
        // Copy-and-swap (see RemoveTrackAsync) — concurrent readers keep the old list.
        var current = _tracks;
        var removedTracks = current.Where(t => idSet.Contains(t.Id)).ToList();
        if (removedTracks.Count == 0) return;
        _tracks = current.Where(t => !idSet.Contains(t.Id)).ToList();

        DeleteOrphanedArtwork(removedTracks);
        await ExcludeFilePathsAndCleanFoldersAsync(removedTracks.Select(t => t.FilePath));
        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.DeleteTracksAsync(idSet);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Deletes cached covers for albums that no longer have any tracks after a
    /// removal. Cached art is never overwritten (every writer short-circuits on
    /// File.Exists) and was never invalidated either — so a wrong cover, once
    /// stamped, survived remove + re-import forever. With the orphan gone, the next
    /// import of that album re-extracts art from its actual files.
    /// </summary>
    private void DeleteOrphanedArtwork(IReadOnlyList<Track> removedTracks)
    {
        foreach (var albumId in SelectOrphanedAlbumIds(removedTracks, _tracks))
        {
            try
            {
                var artPath = _persistence.GetArtworkPath(albumId);
                if (File.Exists(artPath)) File.Delete(artPath);
            }
            catch { /* cache only — worst case the stale cover lingers as before */ }
        }
    }

    /// <summary>Album ids present in <paramref name="removedTracks"/> that no
    /// remaining track references.</summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static IEnumerable<Guid> SelectOrphanedAlbumIds(
        IReadOnlyList<Track> removedTracks, IReadOnlyList<Track> remainingTracks)
    {
        var live = new HashSet<Guid>(remainingTracks.Select(t => t.AlbumId));
        return removedTracks.Select(t => t.AlbumId).Distinct().Where(id => !live.Contains(id));
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(
        IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
    {
        var remap = new Dictionary<Guid, Guid>();
        if (moves == null || moves.Count == 0) return remap;

        var changed = false;
        foreach (var (oldPath, newPath) in moves)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) continue;

            var oldId = ComputeFileId(oldPath);
            if (!_trackIndex.TryGetValue(oldId, out var track)) continue;

            track.FilePath = newPath;
            try
            {
                var fi = new FileInfo(newPath);
                if (fi.Exists)
                {
                    track.LastModified = fi.LastWriteTimeUtc;
                    track.FileSize = fi.Length;
                }
            }
            catch { /* keep prior size/timestamp if the new file isn't readable yet */ }

            var newId = ComputeFileId(newPath);
            // Store-backed lyrics are keyed by track id; lift them into pending
            // values before the id changes so the save below re-files them
            // under the new id instead of orphaning them under the old one.
            track.PrepareLyricsForIdChange();
            track.Id = newId;
            if (oldId != newId) remap[oldId] = newId;
            changed = true;
        }

        if (!changed) return remap;

        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.ReplaceAllAsync(_tracks, ct);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
        return remap;
    }

    /// <summary>
    /// Adds removed file paths to the exclusion list and removes any MusicFolders
    /// entries that no longer contribute any tracks to the library.
    /// </summary>
    private async Task ExcludeFilePathsAndCleanFoldersAsync(IEnumerable<string> removedPaths)
    {
        var settings = await _persistence.LoadSettingsAsync();

        // Add to exclusion list
        var excluded = new HashSet<string>(settings.ExcludedFilePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in removedPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                excluded.Add(path);
        }

        // Prune stale exclusions. Nothing ever cleaned this list, so removing a large
        // folder left tens of thousands of absolute paths in settings.json permanently —
        // re-read and re-hashed on every scan and every subsequent removal. An entry is
        // only meaningful while its file exists and still sits under a configured root;
        // otherwise a plain rescan would never have re-imported it anyway.
        var configuredRoots = settings.MusicFolders
            .Select(TryNormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

        static bool UnderAnyRoot(string path, string[] roots)
        {
            var normalized = TryNormalizePath(path);
            return normalized != null && roots.Any(r => IsUnderRoot(normalized, r));
        }

        settings.ExcludedFilePaths = excluded
            .Where(p => UnderAnyRoot(p, configuredRoots) && File.Exists(p))
            .ToList();

        // Auto-remove folder locations that have zero remaining library tracks.
        // Normalize each remaining track path exactly once instead of calling
        // Path.GetFullPath per track per folder — that was 300k syscall-backed
        // normalizations for a 100k library with three roots, on every removal
        // (including every watcher batch that deletes a file).
        var normalizedRemaining = _tracks
            .Select(t => TryNormalizePath(t.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

        var removedFolders = settings.MusicFolders.RemoveAll(folder =>
        {
            if (string.IsNullOrWhiteSpace(folder)) return true;
            var normalized = TryNormalizePath(folder);
            if (string.IsNullOrWhiteSpace(normalized)) return true;

            // Only drop a root the user configured if it is genuinely gone from disk.
            // Removing the last tracks under a still-present folder used to silently
            // delete it from Settings and stop the watcher for it, with no message.
            if (Directory.Exists(normalized)) return false;

            return !normalizedRemaining.Any(fp => IsUnderRoot(fp, normalized!));
        });

        await _persistence.SaveSettingsAsync(settings);

        // This is the only place the library rewrites the user's folder list. Announcing
        // it directly means Settings no longer has to re-read and re-parse settings.json
        // (plus a DPAPI unprotect) on every LibraryUpdated — an event that also fires on
        // every scan, drop-import, removal and metadata write.
        if (removedFolders > 0)
            MusicFoldersChanged?.Invoke(this, settings.MusicFolders.ToList());
    }

    public async Task LoadAsync()
    {
        var tracks = await _persistence.LoadLibraryAsync();
        if (tracks != null && tracks.Count > 0)
        {
            _tracks = tracks;

            // Overlay journaled user state (ratings, favorites, play counts, ...)
            // on top of the JSON values before anything publishes or saves them.
            await OverlayUserStateFromJournalAsync();

            // Fast path: try restoring pre-computed indexes from cache
            var restored = await TryRestoreFromCacheAsync();
            if (!restored)
            {
                // Cache miss — full rebuild (LINQ grouping, File.Exists, sorting)
                await RebuildIndexesAsync();
            }

            LibraryUpdated?.Invoke(this, EventArgs.Empty);

            // Run slow tasks (SQLite, schema migration) in background to not block UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sqliteIndex.InitializeAsync();
                    var didBackfillMetadata = await EnsureMetadataSchemaUpToDateAsync();
                    await _sqliteIndex.MigrateFromJsonIfEmptyAsync(_tracks);
                    if (didBackfillMetadata)
                    {
                        await RebuildIndexesAsync();
                        await _sqliteIndex.UpsertTracksAsync(_tracks);
                        LibraryUpdated?.Invoke(this, EventArgs.Empty);
                    }

                    // Heal albums whose cover was never cached. Scans only extract
                    // art for new/changed files plus a one-shot post-scan pass, so
                    // any interruption or unreadable file left an album artless for
                    // good — rescans skip unchanged files by mtime and never retry.
                    // Cheap when every album already has art (one probe per album).
                    await BackfillMissingArtworkAsync(_shutdownCts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Background init failed: {ex.Message}");
                }
            });
        }
        else
        {
            // Unguarded, this was the one await on the startup critical path that could
            // throw straight out of MainWindow's async-void Loaded handler: a corrupt or
            // locked library.db (a WAL left by a killed process, a read-only profile)
            // raised SqliteException and killed the process behind an already-visible
            // empty window. The index is a rebuildable cache — never fatal.
            try
            {
                await _sqliteIndex.InitializeAsync();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Library", $"SQLite index init failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LibraryService] SQLite index init failed: {ex.Message}");
            }
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            await _persistence.SaveLibraryAsync(_tracks);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the app for a failed library save.
            // Library data remains in memory and will be retried on next save/shutdown.
            System.Diagnostics.Debug.WriteLine($"[LibraryService] Failed to save library: {ex.Message}");
        }
    }

    // False once the user-state journal in library.db proved unusable this session
    // (locked/corrupt file). Mutations then fall back to the pre-journal behavior —
    // a full library.json save — so nothing the user changes is ever lost.
    private bool _userStateJournalHealthy = true;

    /// <inheritdoc />
    public async Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks)
    {
        if (tracks.Count == 0) return;

        if (_userStateJournalHealthy)
        {
            try
            {
                // Journal the library's own instance where one exists: queue/history
                // can hold pre-reload Track objects whose *other* user-state fields
                // are stale, and a full-row upsert from such an instance would
                // silently revert them (SaveAsync has always persisted library
                // instances only — same rule here).
                var trackIndex = _trackIndex;
                var resolved = tracks
                    .Select(t => trackIndex.TryGetValue(t.Id, out var lib) ? lib : t);
                await _sqliteIndex.UpsertUserStateAsync(resolved);
                return;
            }
            catch (Exception ex)
            {
                _userStateJournalHealthy = false;
                DebugLog.Write("Library",
                    $"User-state journal write failed — falling back to full library saves: {ex.Message}");
            }
        }

        await SaveAsync();
    }

    /// <summary>
    /// Applies the library.db user-state journal on top of the JSON-loaded tracks
    /// (journal wins when a row exists), seeding the journal from the JSON values
    /// on first run. Journal rows whose Id is not currently in the library are
    /// deliberately left in place: a removed track that returns with the same Id
    /// gets its play counts and ratings back on the next load.
    /// </summary>
    private async Task OverlayUserStateFromJournalAsync()
    {
        try
        {
            await _sqliteIndex.InitializeAsync();
            var state = await _sqliteIndex.LoadUserStateAsync();
            if (state.Count == 0)
            {
                // First run after upgrade (or the user deleted library.db): the JSON
                // values are authoritative — copy them in so later journal-only
                // writes merge against a complete baseline.
                await _sqliteIndex.SeedUserStateIfEmptyAsync(_tracks);
                return;
            }

            foreach (var track in _tracks)
            {
                if (!state.TryGetValue(track.Id, out var s)) continue;
                track.PlayCount = s.PlayCount;
                track.LastPlayed = s.LastPlayed;
                track.Rating = s.Rating;
                track.IsDisliked = s.IsDisliked;
                track.SnoozedUntil = s.SnoozedUntil;
                track.SavedPositionMs = s.SavedPositionMs;
                track.IsFavorite = s.IsFavorite;
                // After IsFavorite: its setter stamps/clears FavoritedAt, and the
                // journaled timestamp must win over a fresh stamp.
                track.FavoritedAt = s.FavoritedAt;
            }
        }
        catch (Exception ex)
        {
            // Journal unusable (locked/corrupt library.db). The JSON values stay
            // live and every user-state save falls back to a full library.json
            // write for this session, so no rating or play count is ever lost.
            _userStateJournalHealthy = false;
            DebugLog.Write("Library",
                $"User-state journal unavailable — using library.json values: {ex.Message}");
        }
    }

    public void NotifyFavoritesChanged() => NotifyFavoritesChanged(null);

    /// <summary>
    /// Re-raises album favorite state for the albums containing <paramref name="changed"/>,
    /// or for every album when null.
    ///
    /// Album favorite state (heart overlay, context-menu items) is computed from tracks,
    /// so the owning albums must re-raise their derived properties. Doing it for *every*
    /// album meant two PropertyChanged raises per album on a single heart click — 20,000
    /// on a 10,000-album library, each causing realized tiles to re-evaluate
    /// Tracks.Any(t => t.IsFavorite).
    /// </summary>
    public void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed)
    {
        if (changed is { Count: > 0 })
        {
            var albumIds = new HashSet<Guid>(changed.Select(t => t.AlbumId));
            foreach (var album in _albums)
                if (albumIds.Contains(album.Id))
                    album.NotifyFavoriteStateChanged();
        }
        else
        {
            foreach (var album in _albums)
                album.NotifyFavoriteStateChanged();
        }

        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        var changed = tracks.Where(t => t.Rating != rating).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.Rating = rating;
        await SaveTrackUserStateAsync(changed);
        QueueRatingTagWrites(changed);
    }

    public async Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked)
    {
        var changed = tracks.Where(t => t.IsDisliked != isDisliked).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.IsDisliked = isDisliked;
        await SaveTrackUserStateAsync(changed);
        QueueRatingTagWrites(changed);
    }

    public async Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until)
    {
        var changed = tracks.Where(t => t.SnoozedUntil != until).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.SnoozedUntil = until;
        // Snooze is app-only state — no file tag write (unlike rating/dislike).
        await SaveTrackUserStateAsync(changed);
    }

    /// <summary>
    /// Persists rating tags to the audio files on a worker thread (best effort —
    /// the library JSON saved above is the source of truth if a file is locked/read-only).
    /// </summary>
    private void QueueRatingTagWrites(IReadOnlyList<Track> tracks)
    {
        var targets = tracks
            .Where(t => t.SourceType == SourceType.Local)
            .Select(t => (t.FilePath, t.Rating, t.IsDisliked))
            .ToList();
        if (targets.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var (path, rating, disliked) in targets)
                _metadata.WriteRating(path, rating, disliked);
        });
    }

    public void NotifyMetadataChanged()
    {
        _ = Task.Run(async () =>
        {
            await RebuildIndexesAsync();
            LibraryUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task ClearAsync()
    {
        // Copy-and-swap (see RemoveTrackAsync) — concurrent readers keep the old lists.
        _tracks = new List<Track>();
        _albums = new List<Album>();
        _artists = new List<Artist>();
        _trackIndex = new Dictionary<Guid, Track>();
        _albumIndex = new Dictionary<Guid, Album>();
        lock (_albumsByArtistLock) { _albumsByArtistIndex = null; }
        await _persistence.SaveLibraryAsync(_tracks);
        await _sqliteIndex.ClearAsync();
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        var persisted = await _persistence.LoadLibraryAsync();
        _tracks = persisted ?? new List<Track>();
        await RebuildIndexesAsync();

        await _sqliteIndex.ReplaceAllAsync(_tracks, ct);

        await _auditTrail.AppendAsync(new AuditEvent
        {
            EventType = "index.rebuild",
            EntityType = "library",
            EntityId = "local",
            Reason = "Manual index rebuild requested",
            Details = new Dictionary<string, string>
            {
                ["trackCount"] = _tracks.Count.ToString()
            }
        }, ct);

        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds album, artist, and track-ID indexes from the current track list.
    /// Heavy work (grouping, sorting, File.Exists) runs on a background thread.
    /// </summary>
    // Serializes index rebuilds. The four index fields are published as separate
    // assignments, and rebuilds are triggered concurrently from unrelated paths — the
    // scan's progressive publisher, the artwork publisher, and NotifyMetadataChanged's
    // unawaited Task.Run. Two overlapping runs that observed different _tracks could
    // interleave so _albums came from one and _albumIndex from the other, leaving
    // GetAlbumById returning an Album that is not in Albums. SaveIndexCacheAsync had the
    // same problem, and its output survives restarts because the cache validates.
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

    private async Task RebuildIndexesAsync(bool persistCache = true)
    {
        await _rebuildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RebuildIndexesCoreAsync(persistCache).ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private async Task RebuildIndexesCoreAsync(bool persistCache)
    {
        var tracks = _tracks;
        var persistence = _persistence;

        var (albums, artists, trackIndex, albumIndex) = await Task.Run(() =>
        {
            // Build track lookup dictionary
            var ti = new Dictionary<Guid, Track>(tracks.Count);
            foreach (var t in tracks)
                ti[t.Id] = t;

            // Pre-collect unique album IDs and batch-check artwork existence
            // using a directory listing instead of N individual File.Exists calls
            var albumIds = tracks.Select(t => t.AlbumId).Distinct().ToList();
            var artworkDir = Path.GetDirectoryName(persistence.GetArtworkPath(Guid.Empty));
            var existingArtFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (artworkDir != null && Directory.Exists(artworkDir))
            {
                foreach (var file in Directory.EnumerateFiles(artworkDir, "*.jpg"))
                    existingArtFiles.Add(file);
            }

            var artworkExists = new HashSet<Guid>();
            var artworkPaths = new Dictionary<Guid, string>(albumIds.Count);
            foreach (var albumId in albumIds)
            {
                var artPath = persistence.GetArtworkPath(albumId);
                artworkPaths[albumId] = artPath;
                if (existingArtFiles.Contains(artPath))
                    artworkExists.Add(albumId);
            }

            // Group tracks into albums
            var albs = tracks
                .GroupBy(t => t.AlbumId)
                .Select(g =>
                {
                    // Same normalization as the play-order sort (AlbumDetailViewModel.InAlbumOrder):
                    // disc 0 counts as disc 1 and missing track numbers sink to the end,
                    // so the displayed album order always matches the playback order.
                    var albumTracks = g
                        .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
                        .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
                        .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var first = albumTracks[0];
                    var hasArt = artworkExists.Contains(first.AlbumId);

                    return new Album
                    {
                        Id = first.AlbumId,
                        Name = first.Album,
                        Artist = !string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.AlbumArtist : first.Artist,
                        Year = first.Year,
                        Genre = first.Genre,
                        TrackCount = albumTracks.Count,
                        TotalDuration = TimeSpan.FromTicks(albumTracks.Sum(t => t.Duration.Ticks)),
                        ArtworkPath = hasArt ? artworkPaths[first.AlbumId] : null,
                        Tracks = albumTracks
                    };
                })
                .OrderBy(a => GetPrimaryArtist(a.Artist), StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Build album lookup dictionary
            var ai = new Dictionary<Guid, Album>(albs.Count);
            foreach (var a in albs)
                ai[a.Id] = a;

            // Aggregate artists by primary artist only. Collaboration credits remain
            // on Track.Artist for rows/tags, but feature combinations do not become
            // separate top-level artists.
            var artistBuckets = new Dictionary<string, (string Name, HashSet<Guid> TrackIds, HashSet<Guid> AlbumIds)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                var primaryArtist = track.PrimaryArtist;
                if (string.IsNullOrWhiteSpace(primaryArtist))
                    primaryArtist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist.Trim();

                if (!artistBuckets.TryGetValue(primaryArtist, out var bucket))
                {
                    bucket = (primaryArtist, new HashSet<Guid>(), new HashSet<Guid>());
                    artistBuckets[primaryArtist] = bucket;
                }
                bucket.TrackIds.Add(track.Id);
                bucket.AlbumIds.Add(track.AlbumId);
            }

            var arts = artistBuckets.Values
                .Select(b => new Artist
                {
                    Id = ComputeArtistId(b.Name),
                    Name = b.Name,
                    TrackCount = b.TrackIds.Count,
                    AlbumCount = b.AlbumIds.Count
                })
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Populate track artwork paths from their parent albums
            foreach (var a in albs)
                foreach (var t in a.Tracks)
                    t.AlbumArtworkPath = a.ArtworkPath;

            return (albs, arts, ti, ai);
        });

        _albums = albums;
        _artists = artists;
        _trackIndex = trackIndex;
        _albumIndex = albumIndex;
        // Invalidate under the lock so it can't race a concurrent rebuild in
        // GetAlbumsByArtist and leave a stale index behind; next reader rebuilds.
        lock (_albumsByArtistLock) { _albumsByArtistIndex = null; }

        // Persist the computed indexes so next startup can skip this rebuild.
        // Skipped during progressive scan publishes to avoid rewriting the cache
        // on every partial update; the final rebuild persists the authoritative set.
        //
        // Built from the snapshot this rebuild just produced rather than re-reading the
        // fields: re-reading let it persist an indexes.json whose track hash validated
        // against one track set while its album grouping came from another, and the
        // stale grouping then survived restarts because TryRestoreFromCacheAsync
        // accepted it.
        if (persistCache)
            _ = SaveIndexCacheAsync(tracks, albums, artists);
    }

    /// <summary>
    /// Preserves user-managed state when metadata for a known file is refreshed.
    /// </summary>
    private static void CopyMutableTrackState(Track source, Track target)
    {
        target.IsFavorite = source.IsFavorite;
        // After IsFavorite: its setter stamps a fresh FavoritedAt, and the original
        // timestamp must win — without this, a rescan of a changed file reset the
        // favorite date (scattering the Favorites grid) and dropped any snooze, and
        // the next user-state journal write then persisted those wrong values over
        // the journal's correct ones.
        target.FavoritedAt = source.FavoritedAt;
        target.SnoozedUntil = source.SnoozedUntil;
        target.PlayCount = source.PlayCount;
        target.LastPlayed = source.LastPlayed;
        target.Rating = source.Rating;
        target.IsDisliked = source.IsDisliked;
        target.OfflineState = source.OfflineState;
        target.SourceType = source.SourceType;
        target.SourceTrackId = source.SourceTrackId;
        target.SourceConnectionId = source.SourceConnectionId;
        target.SkipWhenShuffling = source.SkipWhenShuffling;
        target.RememberPlaybackPosition = source.RememberPlaybackPosition;
        target.MediaKind = source.MediaKind;
        target.StartTimeMs = source.StartTimeMs;
        target.StopTimeMs = source.StopTimeMs;
        target.VolumeAdjust = source.VolumeAdjust;
        target.EqPreset = source.EqPreset;
        target.SavedPositionMs = source.SavedPositionMs;
        target.DateAdded = source.DateAdded;

        // Analysis results are expensive to produce (a full ffmpeg decode per track) and
        // are not always round-trippable through file tags, so carry them across a
        // re-import rather than dropping them. Without this, writing the detected key back
        // to the file changed its mtime, the watcher re-imported it, the fresh tag read
        // returned nothing, and the backfill re-analyzed and rewrote the same file — for
        // every track, forever. Only inherit when the freshly-read tags didn't supply a
        // value, so a real tag edit still wins.
        if (target.Bpm <= 0 && source.Bpm > 0)
            target.Bpm = source.Bpm;
        if (string.IsNullOrWhiteSpace(target.MusicalKey) && !string.IsNullOrWhiteSpace(source.MusicalKey))
            target.MusicalKey = source.MusicalKey;
    }

    private static IEnumerable<string> BuildIncludeRoots(IEnumerable<string> folders, AppSettings settings)
    {
        var input = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(TryNormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!);

        var explicitIncludes = settings.FolderRules
            .Where(r => r.Enabled && r.Include && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => TryNormalizePath(r.Path))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!);

        var merged = explicitIncludes.Concat(input)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return merged.Count > 0 ? merged : input;
    }

    private async Task<bool> EnsureMetadataSchemaUpToDateAsync()
    {
        AppSettings settings;
        try
        {
            settings = await _persistence.LoadSettingsAsync();
        }
        catch
        {
            return false;
        }

        // Runs once per startup before any scan or migration touches metadata, so the
        // static enrichment toggle reflects the persisted setting from the first read.
        MetadataService.MergeFeaturedFromTitles = settings.MergeFeaturedFromTitles;
        MetadataService.UseEmbeddedArtwork = settings.UseEmbeddedArtwork;

        if (settings.MetadataSchemaVersion >= CurrentMetadataSchemaVersion)
            return false;

        // The backfills below re-open a large fraction of the library. Report progress
        // through the existing channel so a multi-minute pass on a big library isn't a
        // silent stall with no explanation.
        ScanProgress?.Invoke(this, 0);

        var didBackfillMetadata = false;
        if (settings.MetadataSchemaVersion < 2)
            didBackfillMetadata = await BackfillTrackMetadataAsync(_tracks);

        if (settings.MetadataSchemaVersion < 3)
            didBackfillMetadata |= await BackfillReleaseDateAndCopyrightAsync(_tracks);

        if (settings.MetadataSchemaVersion < 4)
            didBackfillMetadata |= BackfillArtistFromTitle(_tracks);

        // Re-run artist enrichment for tracks that were indexed before
        // the Navidrome connector started merging featured artists.
        if (settings.MetadataSchemaVersion < 5)
            didBackfillMetadata |= BackfillArtistFromTitle(_tracks);

        // v6: populate ReleaseType from tags + album-name heuristic for existing libraries.
        if (settings.MetadataSchemaVersion < 6)
            didBackfillMetadata |= await BackfillReleaseTypeAsync(_tracks);

        // v7: re-read ratings of 1/2/4 stars — earlier reads mistook the iTunes
        // advisory flag in Apple Music downloads for a star rating.
        if (settings.MetadataSchemaVersion < 7)
            didBackfillMetadata |= await BackfillAdvisoryMisreadRatingsAsync(_tracks);

        // Only advance the recorded schema version when the pass actually completed.
        // Cancelling at shutdown mid-backfill and still stamping it done would leave the
        // remaining tracks permanently un-backfilled.
        if (_shutdownCts.IsCancellationRequested)
            return didBackfillMetadata;

        settings.MetadataSchemaVersion = CurrentMetadataSchemaVersion;

        try
        {
            await _persistence.SaveSettingsAsync(settings);
        }
        catch
        {
            // Non-fatal: explicit backfill still applies for this session.
        }

        if (didBackfillMetadata)
            await SaveAsync();

        return didBackfillMetadata;
    }

    private async Task<bool> BackfillTrackMetadataAsync(List<Track> tracks)
    {
        var changedCount = 0;

        // Fast path: estimate missing bitrate from file size and duration
        // so we don't reopen every file just to fill this one field.
        foreach (var track in tracks)
        {
            if (track.Bitrate > 0)
                continue;

            var estimatedBitrate = EstimateBitrateKbps(track.FileSize, track.Duration);
            if (estimatedBitrate <= 0)
                continue;

            track.Bitrate = estimatedBitrate;
            changedCount++;
        }

        var candidates = tracks
            .Where(NeedsTrackMetadataBackfill)
            .ToList();

        if (candidates.Count == 0)
            return changedCount > 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    // Stop on shutdown instead of hammering the disk after the user quit.
                    CancellationToken = _shutdownCts.Token
                },
                track =>
                {
                    Track? refreshed = null;
                    var changed = false;
                    try
                    {
                        refreshed = _metadata.ReadTrackMetadata(track.FilePath);
                    }
                    catch
                    {
                        // Keep existing metadata if a file can't be read during migration.
                    }

                    if (refreshed?.IsExplicit == true)
                    {
                        if (!track.IsExplicit)
                        {
                            track.IsExplicit = true;
                            changed = true;
                        }
                    }

                    if (refreshed != null)
                    {
                        if (refreshed.SampleRate >= 8000 && track.SampleRate != refreshed.SampleRate)
                        {
                            track.SampleRate = refreshed.SampleRate;
                            changed = true;
                        }

                        if (refreshed.BitsPerSample > 0 && track.BitsPerSample != refreshed.BitsPerSample)
                        {
                            track.BitsPerSample = refreshed.BitsPerSample;
                            changed = true;
                        }

                        if (refreshed.Bitrate > 0 && track.Bitrate != refreshed.Bitrate)
                        {
                            track.Bitrate = refreshed.Bitrate;
                            changed = true;
                        }

                        if (!string.IsNullOrWhiteSpace(refreshed.Codec) &&
                            !string.Equals(track.Codec, refreshed.Codec, StringComparison.Ordinal))
                        {
                            track.Codec = refreshed.Codec;
                            changed = true;
                        }
                    }

                    if (track.Bitrate <= 0)
                    {
                        var estimatedBitrate = EstimateBitrateKbps(track.FileSize, track.Duration);
                        if (estimatedBitrate > 0)
                        {
                            track.Bitrate = estimatedBitrate;
                            changed = true;
                        }
                    }

                    if (changed)
                        Interlocked.Increment(ref changedCount);
                });
        });

        return changedCount > 0;
    }

    private static bool NeedsTrackMetadataBackfill(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            return false;

        if (track.SampleRate < 8000 || track.BitsPerSample <= 0)
            return true;

        if (string.IsNullOrWhiteSpace(track.Codec))
            return true;

        if (!track.IsExplicit)
        {
            var ext = Path.GetExtension(track.FilePath).ToLowerInvariant();
            if (ext is ".m4a" or ".mp4" or ".aac" or ".alac")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Populates ReleaseType + ReleaseTypeFromTag for tracks indexed before v6,
    /// by re-reading just the relevant tags. Skips already-overridden tracks
    /// and tracks whose file is no longer accessible.
    /// </summary>
    private async Task<bool> BackfillReleaseTypeAsync(List<Track> tracks)
    {
        var candidates = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath)
                        && !t.IsReleaseTypeOverridden
                        && !t.ReleaseTypeFromTag)
            .ToList();

        if (candidates.Count == 0)
            return false;

        var changedCount = 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    // Stop on shutdown instead of hammering the disk after the user quit.
                    CancellationToken = _shutdownCts.Token
                },
                track =>
                {
                    try
                    {
                        var refreshed = _metadata.ReadTrackMetadata(track.FilePath);
                        if (refreshed == null) return;
                        if (refreshed.ReleaseTypeFromTag || refreshed.IsReleaseTypeOverridden)
                        {
                            track.ReleaseType = refreshed.ReleaseType;
                            track.IsReleaseTypeOverridden = refreshed.IsReleaseTypeOverridden;
                            track.ReleaseTypeFromTag = refreshed.ReleaseTypeFromTag;
                            Interlocked.Increment(ref changedCount);
                        }
                    }
                    catch
                    {
                        // Non-fatal — backfill is best-effort.
                    }
                });
        });

        return changedCount > 0;
    }

    private async Task<bool> BackfillAdvisoryMisreadRatingsAsync(List<Track> tracks)
    {
        // Only 1/2/4 stars can be phantom advisory codes (1=explicit, 2=clean,
        // 4=legacy explicit). Genuine ratings set in Noctis were written to the
        // file tags on the 0-100 scale, so re-reading keeps them intact.
        var candidates = tracks
            .Where(t => t.Rating is 1 or 2 or 4
                        && !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath))
            .ToList();

        if (candidates.Count == 0)
            return false;

        var changedTracks = new ConcurrentBag<Track>();

        await Task.Run(() =>
        {
            Parallel.ForEach(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    // Stop on shutdown instead of hammering the disk after the user quit.
                    CancellationToken = _shutdownCts.Token
                },
                track =>
                {
                    try
                    {
                        var refreshed = _metadata.ReadTrackMetadata(track.FilePath);
                        if (refreshed == null) return;
                        if (refreshed.Rating != track.Rating)
                        {
                            track.Rating = refreshed.Rating;
                            changedTracks.Add(track);
                        }
                    }
                    catch
                    {
                        // Non-fatal — backfill is best-effort.
                    }
                });
        });

        if (changedTracks.IsEmpty)
            return false;

        // Keep the user-state journal in step: it wins over library.json on load,
        // so leaving the phantom ratings journaled would resurrect them every launch.
        await SaveTrackUserStateAsync(changedTracks.ToList());
        return true;
    }

    private async Task<bool> BackfillReleaseDateAndCopyrightAsync(List<Track> tracks)
    {
        var candidates = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath) &&
                        (string.IsNullOrWhiteSpace(t.ReleaseDate) || string.IsNullOrWhiteSpace(t.Copyright)))
            .ToList();

        if (candidates.Count == 0)
            return false;

        var changedCount = 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    // Stop on shutdown instead of hammering the disk after the user quit.
                    CancellationToken = _shutdownCts.Token
                },
                track =>
                {
                    try
                    {
                        var refreshed = _metadata.ReadTrackMetadata(track.FilePath);
                        if (refreshed == null) return;

                        var changed = false;

                        if (string.IsNullOrWhiteSpace(track.ReleaseDate) &&
                            !string.IsNullOrWhiteSpace(refreshed.ReleaseDate))
                        {
                            track.ReleaseDate = refreshed.ReleaseDate;
                            changed = true;
                        }

                        if (string.IsNullOrWhiteSpace(track.Copyright) &&
                            !string.IsNullOrWhiteSpace(refreshed.Copyright))
                        {
                            track.Copyright = refreshed.Copyright;
                            changed = true;
                        }

                        if (changed)
                            Interlocked.Increment(ref changedCount);
                    }
                    catch
                    {
                        // Non-fatal: skip tracks that can't be read.
                    }
                });
        });

        return changedCount > 0;
    }

    /// <summary>
    /// One-time migration: enrich Artist with featured artists from title
    /// so collaboration tracks always show the artist subtitle.
    /// </summary>
    private bool BackfillArtistFromTitle(List<Track> tracks)
    {
        var changedCount = 0;
        foreach (var track in tracks)
        {
            var enriched = MetadataService.EnrichArtistFromTitle(track.Artist, track.Title);
            if (!string.Equals(enriched, track.Artist, StringComparison.Ordinal))
            {
                track.Artist = enriched;
                changedCount++;
            }
        }
        return changedCount > 0;
    }

    /// <inheritdoc />
    public async Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        lock (_mergeFeatApplyLock)
        {
            _mergeFeatApplyCts?.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
            _mergeFeatApplyCts = cts;
        }

        try
        {
            var token = cts.Token;
            var snapshot = _tracks.ToList();
            var changed = new List<Track>();

            if (enabled)
            {
                // Merging is pure string work against the indexed titles — instant.
                foreach (var track in snapshot)
                {
                    if (token.IsCancellationRequested) break;
                    var enriched = MetadataService.EnrichArtistFromTitle(track.Artist, track.Title);
                    if (!string.Equals(enriched, track.Artist, StringComparison.Ordinal))
                    {
                        track.Artist = enriched;
                        changed.Add(track);
                    }
                }
            }
            else
            {
                // Un-merging needs the pre-merge artist, which only exists in the file
                // tags — but only collaboration tracks whose artist actually echoes a
                // featured name from the title can have been merged, so the re-read is
                // bounded to those. Server-backed sources (Navidrome/WebDav/Plex) have
                // no readable file; their tracks are rebuilt fresh on the next connector
                // sync, which honors the flipped toggle.
                var candidates = snapshot
                    .Where(t => t.SourceType is SourceType.Local or SourceType.Smb &&
                                MetadataService.MayHaveMergedFeaturedCredit(t.Artist, t.Title))
                    .ToList();

                if (candidates.Count > 0)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            Parallel.ForEach(
                                candidates,
                                new ParallelOptions
                                {
                                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                                    CancellationToken = token
                                },
                                track =>
                                {
                                    try
                                    {
                                        var refreshed = _metadata.ReadTrackMetadata(track.FilePath);
                                        if (refreshed == null) return;
                                        if (!string.Equals(refreshed.Artist, track.Artist, StringComparison.Ordinal))
                                        {
                                            track.Artist = refreshed.Artist;
                                            lock (changed) changed.Add(track);
                                        }
                                    }
                                    catch
                                    {
                                        // Non-fatal: keep the merged credit for unreadable files.
                                    }
                                });
                        }
                        catch (OperationCanceledException) { }
                    }, CancellationToken.None);
                }
            }

            if (changed.Count == 0)
                return 0;

            // Superseded or shutting down: skip persistence. A newer flip's pass will
            // re-cover these tracks (both directions are idempotent over the library),
            // and at shutdown the exit flush saves the JSON.
            if (token.IsCancellationRequested)
                return changed.Count;

            await RebuildIndexesAsync();
            await SaveAsync();
            try { await _sqliteIndex.UpsertTracksAsync(changed); }
            catch { /* JSON save above is authoritative; SQLite catches up on the next full sync */ }
            LibraryUpdated?.Invoke(this, EventArgs.Empty);
            return changed.Count;
        }
        finally
        {
            lock (_mergeFeatApplyLock)
            {
                if (_mergeFeatApplyCts == cts)
                    _mergeFeatApplyCts = null;
                cts.Dispose();
            }
        }
    }

    private static int EstimateBitrateKbps(long fileSizeBytes, TimeSpan duration)
    {
        if (fileSizeBytes <= 0 || duration.TotalSeconds <= 0)
            return 0;

        var estimated = (int)Math.Round((fileSizeBytes * 8d) / duration.TotalSeconds / 1000d);
        return estimated > 0 ? estimated : 0;
    }

    /// <summary>
    /// Degree of parallelism for the scan's file reads. Metadata reading is
    /// I/O-bound, so a single HDD or network share thrashes seeking once more than
    /// a handful of readers hit it at once — basing this on ProcessorCount (16-24
    /// on modern CPUs) hurts spinning/network volumes and barely helps SSDs.
    /// Capped low by default; NOCTIS_SCAN_THREADS overrides it for A/B testing.
    /// </summary>
    private static int GetScanParallelism()
    {
        var raw = Environment.GetEnvironmentVariable("NOCTIS_SCAN_THREADS");
        if (int.TryParse(raw, out var n) && n >= 1)
            return Math.Min(n, 64);
        return Math.Min(Environment.ProcessorCount, 8);
    }

    private static IEnumerable<string> EnumerateAudioFiles(
        string root,
        IReadOnlyCollection<string> excludedRoots,
        HashSet<string> ignoredNames,
        ConcurrentBag<string> failedDirs)
    {
        var stack = new Stack<string>();
        // Cycle guard keyed on the RESOLVED path: a junction/symlink pointing at
        // an ancestor re-enters the tree under an ever-growing logical path, so
        // the walked path alone never repeats and the DFS loops forever.
        var visited = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (IsUnderAnyRoot(current, excludedRoots)) continue;
            if (!visited.Add(ResolveRealPath(current))) continue;

            List<string> directories;
            List<string> files;
            try
            {
                // Materialized inside the try: the enumerables are lazy, so an I/O error
                // surfacing mid-listing (not just at open) would otherwise escape this
                // catch and abort the entire scan pipeline.
                directories = Directory.EnumerateDirectories(current).ToList();
                files = Directory.EnumerateFiles(current).ToList();
            }
            catch
            {
                // "Couldn't list" is not "doesn't exist". Silently skipping here made the
                // scan treat the whole subtree as deleted; record it so ScanCoreAsync can
                // keep the known tracks under it (see the failed-directories guard there).
                failedDirs.Add(current);
                continue;
            }

            foreach (var dir in directories)
            {
                var name = Path.GetFileName(dir);
                if (ignoredNames.Contains(name.ToLowerInvariant())) continue;
                if (IsUnderAnyRoot(dir, excludedRoots)) continue;
                stack.Push(dir);
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (MetadataService.SupportedExtensions.Contains(ext))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// Existing tracks that live under a directory whose listing failed this scan and
    /// that the scan did not see — i.e. tracks that would otherwise be dropped because
    /// their parent folder was unreachable, not because their file is gone.
    /// </summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static List<Track> SelectTracksUnderFailedDirectories(
        IReadOnlyList<Track> existingTracks,
        HashSet<Guid> scannedIds,
        IEnumerable<string> failedDirectories)
    {
        var failedPrefixes = failedDirectories
            .Select(TryNormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (failedPrefixes.Length == 0)
            return new List<Track>();

        return existingTracks
            .Where(t =>
            {
                if (scannedIds.Contains(t.Id)) return false;
                var normalized = TryNormalizePath(t.FilePath);
                return normalized != null && failedPrefixes.Any(p => IsUnderRoot(normalized, p));
            })
            .ToList();
    }

    // Symlinked/junctioned directories are followed (symlinked music libraries are
    // legitimate); resolving to the final target is what makes the visited-set
    // above detect a loop regardless of the logical path it was reached through.
    private static string ResolveRealPath(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            return info.FullName;
        }
        catch
        {
            return dir;
        }
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyCollection<string> roots)
    {
        var normalized = NormalizePath(path);
        foreach (var root in roots)
        {
            if (IsUnderRoot(normalized, root))
                return true;
        }
        return false;
    }

    private static bool IsUnderRoot(string normalizedPath, string root)
    {
        if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryNormalizePath(string path)
    {
        try
        {
            return NormalizePath(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes a hash of all track IDs for cache validation.
    /// Uses XOR which is order-independent — catches additions/removals.
    /// </summary>
    private static string ComputeTrackIdHash(List<Track> tracks)
    {
        long h0 = 0, h1 = 0;
        foreach (var t in tracks)
        {
            var bytes = t.Id.ToByteArray();
            h0 ^= BitConverter.ToInt64(bytes, 0);
            h1 ^= BitConverter.ToInt64(bytes, 8);
        }
        return $"{h0:X16}{h1:X16}";
    }

    /// <summary>
    /// Tries to restore album/artist indexes from the cached indexes.json.
    /// Returns true if cache was valid and indexes were restored successfully.
    /// </summary>
    private async Task<bool> TryRestoreFromCacheAsync()
    {
        try
        {
            var cache = await _persistence.LoadIndexCacheAsync();
            if (cache == null || cache.Version != CurrentIndexCacheVersion || cache.TrackCount != _tracks.Count)
                return false;

            // The ID-hash validation and full album reconstruction + sort are CPU-bound
            // over the whole library. This fast path runs on every launch; resuming the
            // LoadIndexCacheAsync await on the UI thread meant all of it ran there and
            // stalled startup. Offload to a worker (RebuildIndexesAsync already does the
            // same), capture the results into locals, and assign the indexes only after a
            // successful, non-stale rebuild. Track.AlbumArtworkPath is a plain property
            // (no change notification) and nothing reads these indexes until LoadAsync
            // raises LibraryUpdated, so the off-thread writes are safe.
            var tracks = _tracks;
            List<Album>? newAlbums = null;
            Dictionary<Guid, Track>? newTrackIndex = null;
            Dictionary<Guid, Album>? newAlbumIndex = null;

            var ok = await Task.Run(() =>
            {
                if (cache.TrackIdHash != ComputeTrackIdHash(tracks))
                    return false;

                // Cache is valid — restore indexes without expensive rebuild
                var trackIndex = new Dictionary<Guid, Track>(tracks.Count);
                foreach (var t in tracks)
                    trackIndex[t.Id] = t;

                var albums = new List<Album>(cache.Albums.Count);
                var albumIndex = new Dictionary<Guid, Album>(cache.Albums.Count);

                foreach (var entry in cache.Albums)
                {
                    var albumTracks = new List<Track>(entry.TrackIds.Count);
                    foreach (var tid in entry.TrackIds)
                    {
                        if (trackIndex.TryGetValue(tid, out var track))
                            albumTracks.Add(track);
                    }

                    // If tracks are missing, cache is stale
                    if (albumTracks.Count != entry.TrackCount)
                        return false;

                    var album = new Album
                    {
                        Id = entry.Id,
                        Name = entry.Name,
                        Artist = entry.Artist,
                        Year = entry.Year,
                        Genre = entry.Genre,
                        TrackCount = entry.TrackCount,
                        TotalDuration = TimeSpan.FromTicks(entry.TotalDurationTicks),
                        ArtworkPath = entry.ArtworkPath,
                        Tracks = albumTracks
                    };

                    albums.Add(album);
                    albumIndex[album.Id] = album;

                    // Populate track artwork paths
                    foreach (var t in albumTracks)
                        t.AlbumArtworkPath = album.ArtworkPath;
                }

                newAlbums = albums
                    .OrderBy(a => GetPrimaryArtist(a.Artist), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Year)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                newTrackIndex = trackIndex;
                newAlbumIndex = albumIndex;
                return true;
            });

            if (!ok)
                return false;

            _albums = newAlbums!;
            _artists = cache.Artists;
            _trackIndex = newTrackIndex!;
            _albumIndex = newAlbumIndex!;
            // Invalidate under the lock so it can't race a concurrent rebuild in
            // GetAlbumsByArtist and leave a stale index behind; next reader rebuilds.
            lock (_albumsByArtistLock) { _albumsByArtistIndex = null; }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the current album/artist indexes to cache for fast restore on next startup.
    /// </summary>
    /// <summary>
    /// Persists a coherent index snapshot. The three collections must come from the same
    /// rebuild — see the note at the call site.
    /// </summary>
    private async Task SaveIndexCacheAsync(
        List<Track> tracks, List<Album> albums, List<Artist> artists)
    {
        try
        {
            var cache = new LibraryIndexCache
            {
                Version = CurrentIndexCacheVersion,
                TrackCount = tracks.Count,
                TrackIdHash = ComputeTrackIdHash(tracks),
                Artists = artists.ToList()
            };

            foreach (var album in albums)
            {
                cache.Albums.Add(new CachedAlbumEntry
                {
                    Id = album.Id,
                    Name = album.Name,
                    Artist = album.Artist,
                    Year = album.Year,
                    Genre = album.Genre,
                    TrackCount = album.TrackCount,
                    TotalDurationTicks = album.TotalDuration.Ticks,
                    ArtworkPath = album.ArtworkPath,
                    TrackIds = album.Tracks.Select(t => t.Id).ToList()
                });
            }

            await _persistence.SaveIndexCacheAsync(cache);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryService] Failed to save index cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a deterministic GUID from a file path so that
    /// rescanning the same file always produces the same track ID.
    /// </summary>
    private static Guid ComputeFileId(string filePath)
    {
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash);
    }

    /// <summary>Returns the first artist token for sorting (e.g. "Bad Bunny" from "Bad Bunny & J Balvin").</summary>
    private static string GetPrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return string.Empty;
        return Track.GetPrimaryArtist(artist);
    }

    private static Guid ComputeArtistId(string artistName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }
}
