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

    public IReadOnlyList<Track> Tracks => _tracks;
    public IReadOnlyList<Album> Albums => _albums;
    public IReadOnlyList<Artist> Artists => _artists;

    public event EventHandler? LibraryUpdated;
    public event EventHandler<int>? ScanProgress;
    public event EventHandler? FavoritesChanged;

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
        }
    }

    private async Task ScanCoreAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        var settings = await _persistence.LoadSettingsAsync();
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
                if (!Directory.Exists(folder)) continue;

                // Enumerate recursively with folder rules, excluding removed files.
                // Stream files into processing as they're discovered (no up-front
                // ToList): on slow disks and network shares this overlaps directory
                // enumeration with metadata reads and starts reporting progress
                // immediately instead of after a long silent listing phase.
                // NoBuffering dispatches one file at a time so the count moves right away.
                var files = EnumerateAudioFiles(folder, excludedRoots, ignoredNames)
                    .Where(f => !excludedFiles.Contains(f));
                var partitioner = Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering);

                Parallel.ForEach(partitioner, options, filePath =>
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        Track? existing = null;

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
                            Interlocked.Increment(ref skippedCount);
                        }

                        ReportProgress(Interlocked.Increment(ref fileCount));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Skip files that can't be read (locked, permissions, I/O error)
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

        // Fast path: every existing track was found and unchanged, and no new or
        // modified files were detected. Skip the destructive rebuild/persist path —
        // previously we always wiped the SQLite index and rewrote library.json on
        // every launch, which the user saw as re-indexing even when nothing changed.
        if (changedCount == 0 && unchangedCount == originalTrackCount)
        {
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
        await _sqliteIndex.ClearAsync(ct);
        await _sqliteIndex.UpsertTracksAsync(_tracks, ct);

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
    public async Task PauseActiveScanForShutdownAsync(TimeSpan timeout)
    {
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
            await _sqliteIndex.ClearAsync();
            await _sqliteIndex.UpsertTracksAsync(_tracks);
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
    /// </summary>
    private async Task ExtractArtworkProgressivelyAsync(ConcurrentBag<Track> scanned, CancellationToken ct)
    {
        var artGroups = scanned
            .Where(t => t.SourceType == SourceType.Local)
            .GroupBy(t => t.AlbumId)
            .Where(g => !File.Exists(_persistence.GetArtworkPath(g.Key)))
            .ToList();
        if (artGroups.Count == 0) return;

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
                        if (artBytes != null)
                            _persistence.SaveArtwork(g.Key, artBytes);
                    });
            }, ct);
        }
        finally
        {
            pubCts.Cancel();
            try { await publish.ConfigureAwait(false); } catch { /* publisher already stopping */ }
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
        await _sqliteIndex.UpsertTracksAsync(_tracks, ct);
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

        await ExcludeFilePathsAndCleanFoldersAsync(removedTracks.Select(t => t.FilePath));
        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.DeleteTracksAsync(idSet);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
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
            track.Id = newId;
            if (oldId != newId) remap[oldId] = newId;
            changed = true;
        }

        if (!changed) return remap;

        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.ClearAsync(ct);
        await _sqliteIndex.UpsertTracksAsync(_tracks, ct);
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
        settings.ExcludedFilePaths = excluded.ToList();

        // Auto-remove folder locations that have zero remaining library tracks
        var remainingPaths = new HashSet<string>(_tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        settings.MusicFolders.RemoveAll(folder =>
        {
            if (string.IsNullOrWhiteSpace(folder)) return true;
            var normalized = TryNormalizePath(folder);
            if (string.IsNullOrWhiteSpace(normalized)) return true;
            return !remainingPaths.Any(fp => IsUnderRoot(NormalizePath(fp), normalized!));
        });

        await _persistence.SaveSettingsAsync(settings);
    }

    public async Task LoadAsync()
    {
        var tracks = await _persistence.LoadLibraryAsync();
        if (tracks != null && tracks.Count > 0)
        {
            _tracks = tracks;

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
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Background init failed: {ex.Message}");
                }
            });
        }
        else
        {
            await _sqliteIndex.InitializeAsync();
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

    public void NotifyFavoritesChanged()
    {
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        var changed = tracks.Where(t => t.Rating != rating).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.Rating = rating;
        await SaveAsync();
        QueueRatingTagWrites(changed);
    }

    public async Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked)
    {
        var changed = tracks.Where(t => t.IsDisliked != isDisliked).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.IsDisliked = isDisliked;
        await SaveAsync();
        QueueRatingTagWrites(changed);
    }

    public async Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until)
    {
        var changed = tracks.Where(t => t.SnoozedUntil != until).ToList();
        if (changed.Count == 0) return;

        foreach (var track in changed)
            track.SnoozedUntil = until;
        // Snooze is app-only state — no file tag write (unlike rating/dislike).
        await SaveAsync();
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

        await _sqliteIndex.ClearAsync(ct);
        await _sqliteIndex.UpsertTracksAsync(_tracks, ct);

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
    private async Task RebuildIndexesAsync(bool persistCache = true)
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
        if (persistCache)
            _ = SaveIndexCacheAsync();
    }

    /// <summary>
    /// Preserves user-managed state when metadata for a known file is refreshed.
    /// </summary>
    private static void CopyMutableTrackState(Track source, Track target)
    {
        target.IsFavorite = source.IsFavorite;
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

        if (settings.MetadataSchemaVersion >= CurrentMetadataSchemaVersion)
            return false;

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
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
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
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
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

        var changedCount = 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
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
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
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
        HashSet<string> ignoredNames)
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

            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
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
    private async Task SaveIndexCacheAsync()
    {
        try
        {
            var cache = new LibraryIndexCache
            {
                Version = CurrentIndexCacheVersion,
                TrackCount = _tracks.Count,
                TrackIdHash = ComputeTrackIdHash(_tracks),
                Artists = _artists.ToList()
            };

            foreach (var album in _albums)
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
