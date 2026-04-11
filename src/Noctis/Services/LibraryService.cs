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
    private const int CurrentMetadataSchemaVersion = 5;

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

        await Task.Run(() =>
        {
            // Track which album IDs have had art extraction attempted to avoid redundant work
            var artExtracted = new ConcurrentDictionary<Guid, byte>();

            foreach (var folder in includeRoots)
            {
                if (!Directory.Exists(folder)) continue;

                // Enumerate recursively with folder rules, excluding removed files.
                var files = EnumerateAudioFiles(folder, excludedRoots, ignoredNames)
                    .Where(f => !excludedFiles.Contains(f))
                    .ToList();

                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                };

                Parallel.ForEach(files, options, filePath =>
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
                                Interlocked.Increment(ref fileCount);
                                ScanProgress?.Invoke(this, fileCount);
                                return;
                            }
                        }

                        // Read metadata for new or changed files
                        var track = _metadata.ReadTrackMetadata(filePath);
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

                            // Extract and cache album artwork if we don't have it yet
                            var artPath = _persistence.GetArtworkPath(track.AlbumId);
                            if (!File.Exists(artPath) && artExtracted.TryAdd(track.AlbumId, 0))
                            {
                                var artBytes = _metadata.ExtractAlbumArt(filePath);
                                if (artBytes != null)
                                {
                                    _persistence.SaveArtwork(track.AlbumId, artBytes);
                                }
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref skippedCount);
                        }

                        Interlocked.Increment(ref fileCount);
                        ScanProgress?.Invoke(this, fileCount);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Skip files that can't be read (locked, permissions, I/O error)
                        Interlocked.Increment(ref skippedCount);
                        Interlocked.Increment(ref fileCount);
                        ScanProgress?.Invoke(this, fileCount);
                    }
                });
            }
        }, ct);

        // If scan was cancelled, don't replace the library with partial data
        if (ct.IsCancellationRequested) return;

        // Rebuild the library from scanned tracks.
        // DistinctBy(Id) prevents duplicates from overlapping music folders
        // (e.g., user adds /Music and /Music/Rock — files in the overlap get scanned twice).
        _tracks = newTracks
            .GroupBy(t => t.Id).Select(g => g.First()) // deduplicate by track ID
            .OrderBy(t => t.Artist).ThenBy(t => t.Album)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
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

    public async Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
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

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

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
            changed = true;
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
        return _albums.Where(a =>
            a.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task RemoveTrackAsync(Guid id)
    {
        var track = GetTrackById(id);
        if (track == null) return;

        _tracks.Remove(track);
        await ExcludeFilePathsAndCleanFoldersAsync(new[] { track.FilePath });
        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.DeleteTracksAsync(new[] { id });
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveTracksAsync(IEnumerable<Guid> ids)
    {
        var idSet = new HashSet<Guid>(ids);
        var removedTracks = _tracks.Where(t => idSet.Contains(t.Id)).ToList();
        var removed = _tracks.RemoveAll(t => idSet.Contains(t.Id));
        if (removed == 0) return;

        await ExcludeFilePathsAndCleanFoldersAsync(removedTracks.Select(t => t.FilePath));
        await RebuildIndexesAsync();
        await SaveAsync();
        await _sqliteIndex.DeleteTracksAsync(idSet);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
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
            return !remainingPaths.Any(fp => IsUnderAnyRoot(fp, new[] { normalized! }));
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
        _tracks.Clear();
        _albums.Clear();
        _artists.Clear();
        _trackIndex.Clear();
        _albumIndex.Clear();
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
    private async Task RebuildIndexesAsync()
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
                    var albumTracks = g.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
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

            // Aggregate artists — split multi-artist strings so each
            // individual collaborator gets their own entry in the Artists list.
            // Also create a combined entry for the exact multi-artist group
            // (e.g., "Bad Bunny, Prince Royce & J Balvin") so users can view
            // albums by that specific collaboration.
            var artistBuckets = new Dictionary<string, (string Name, HashSet<Guid> TrackIds, HashSet<Guid> AlbumIds)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                var tokens = Track.ParseArtistTokens(track.Artist);
                // If parsing yields nothing (unlikely), keep the raw string
                if (tokens.Length == 0)
                    tokens = new[] { track.Artist };

                foreach (var token in tokens)
                {
                    if (!artistBuckets.TryGetValue(token, out var bucket))
                    {
                        bucket = (token, new HashSet<Guid>(), new HashSet<Guid>());
                        artistBuckets[token] = bucket;
                    }
                    bucket.TrackIds.Add(track.Id);
                    bucket.AlbumIds.Add(track.AlbumId);
                }

                // Create a combined artist entry when the track has multiple artists
                if (tokens.Length > 1)
                {
                    var combinedKey = track.Artist;
                    if (!artistBuckets.TryGetValue(combinedKey, out var combined))
                    {
                        combined = (combinedKey, new HashSet<Guid>(), new HashSet<Guid>());
                        artistBuckets[combinedKey] = combined;
                    }
                    combined.TrackIds.Add(track.Id);
                    combined.AlbumIds.Add(track.AlbumId);
                }
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

        // Persist the computed indexes so next startup can skip this rebuild
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

    private static IEnumerable<string> EnumerateAudioFiles(
        string root,
        IReadOnlyCollection<string> excludedRoots,
        HashSet<string> ignoredNames)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (IsUnderAnyRoot(current, excludedRoots)) continue;

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

    private static bool IsUnderAnyRoot(string path, IReadOnlyCollection<string> roots)
    {
        var normalized = NormalizePath(path);
        foreach (var root in roots)
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
            if (cache == null || cache.TrackCount != _tracks.Count)
                return false;

            var currentHash = ComputeTrackIdHash(_tracks);
            if (cache.TrackIdHash != currentHash)
                return false;

            // Cache is valid — restore indexes without expensive rebuild
            var trackIndex = new Dictionary<Guid, Track>(_tracks.Count);
            foreach (var t in _tracks)
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

            _albums = albums
                .OrderBy(a => GetPrimaryArtist(a.Artist), StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _artists = cache.Artists;
            _trackIndex = trackIndex;
            _albumIndex = albumIndex;

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
        var tokens = Track.ParseArtistTokens(artist);
        return tokens.Length > 0 ? tokens[0] : artist;
    }

    private static Guid ComputeArtistId(string artistName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }
}
