using System.Security.Cryptography;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Hybrid stream cache + pinning implementation.
/// </summary>
public sealed class OfflineCacheService : IOfflineCacheService
{
    private readonly string _cacheDir;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, CacheEntry> _index = new();

    public OfflineCacheService(IPersistenceService persistence)
    {
        _cacheDir = Path.Combine(persistence.DataDirectory, "cache", "tracks");
        _indexPath = Path.Combine(persistence.DataDirectory, "cache", "index.json");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        LoadIndex();
    }

    public async Task<string?> ResolvePlaybackPathAsync(Track track, CancellationToken ct = default)
    {
        // Under the gate: PinAsync mutates _index concurrently, and a Dictionary
        // read during a resizing add is a torn read on the playback hot path.
        await _gate.WaitAsync(ct);
        try
        {
            if (_index.TryGetValue(track.Id, out var entry) && File.Exists(entry.Path))
                return entry.Path;
        }
        finally
        {
            _gate.Release();
        }

        if (track.SourceType == SourceType.Local && File.Exists(track.FilePath))
            return track.FilePath;

        return null;
    }

    public async Task PinAsync(Track track, Stream sourceStream, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(track.FilePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        var fileName = ComputeCacheName(track) + ext;
        var path = Path.Combine(_cacheDir, fileName);

        await _gate.WaitAsync(ct);
        try
        {
            // Copy to a temp file and move into place: File.Create on the final
            // path truncated an existing good cache file up front, so a failed or
            // cancelled copy left the index pointing at a zero-byte/corrupt file.
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                sourceStream.Position = 0;
                await sourceStream.CopyToAsync(fs, ct);
            }
            File.Move(tmp, path, overwrite: true);
            _index[track.Id] = new CacheEntry { Path = path, Pinned = true, UpdatedUtc = DateTime.UtcNow };
            await SaveIndexAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnpinAsync(Track track, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_index.TryGetValue(track.Id, out var entry))
            {
                // Delete the cached file, don't just clear the flag. Unpinning left the
                // file on disk, and because EnforceLimitsAsync only removes entries whose
                // file is already *missing*, nothing would ever reclaim it — so unpinning
                // freed exactly zero disk and the cache grew without bound.
                try
                {
                    if (!string.IsNullOrEmpty(entry.Path) && File.Exists(entry.Path))
                        File.Delete(entry.Path);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("OfflineCache", $"Could not delete '{entry.Path}': {ex.Message}");
                }

                _index.Remove(track.Id);
                await SaveIndexAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Prunes dead entries and evicts unpinned files, oldest-touched first, until the
    /// cache fits <paramref name="limitMb"/>.
    ///
    /// This previously only dropped index entries whose file was already gone — it never
    /// deleted anything and never applied a size cap, despite the name, so the cache
    /// directory grew without bound.
    /// </summary>
    public async Task EnforceLimitsAsync(int limitMb = 0, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var changed = false;

            // 1. Drop entries whose file is gone.
            foreach (var id in _index
                         .Where(kvp => !File.Exists(kvp.Value.Path))
                         .Select(kvp => kvp.Key)
                         .ToList())
            {
                _index.Remove(id);
                changed = true;
            }

            // 2. Evict unpinned entries, least-recently-updated first, until under budget.
            if (limitMb > 0)
            {
                var budget = (long)limitMb * 1024 * 1024;

                var sized = _index
                    .Select(kvp =>
                    {
                        long bytes = 0;
                        try { bytes = new FileInfo(kvp.Value.Path).Length; } catch { }
                        return (Id: kvp.Key, Entry: kvp.Value, Bytes: bytes);
                    })
                    .ToList();

                var total = sized.Sum(e => e.Bytes);

                foreach (var candidate in sized
                             .Where(e => !e.Entry.Pinned)
                             .OrderBy(e => e.Entry.UpdatedUtc))
                {
                    if (total <= budget) break;
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        if (File.Exists(candidate.Entry.Path))
                            File.Delete(candidate.Entry.Path);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("OfflineCache",
                            $"Could not evict '{candidate.Entry.Path}': {ex.Message}");
                        continue;
                    }

                    _index.Remove(candidate.Id);
                    total -= candidate.Bytes;
                    changed = true;
                }
            }

            if (changed)
                await SaveIndexAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ComputeCacheName(Track track)
    {
        var raw = $"{track.SourceType}:{track.SourceConnectionId}:{track.SourceTrackId}:{track.Id:N}";
        var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            var json = File.ReadAllText(_indexPath);
            var data = JsonSerializer.Deserialize<Dictionary<Guid, CacheEntry>>(json);
            if (data == null) return;
            foreach (var kvp in data)
                _index[kvp.Key] = kvp.Value;
        }
        catch
        {
            // Ignore cache index failures; cache is non-critical.
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        try
        {
            var temp = _indexPath + ".tmp";
            var json = JsonSerializer.Serialize(_index);
            await File.WriteAllTextAsync(temp, json, ct);
            File.Move(temp, _indexPath, true);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "OfflineCacheService.SaveIndex",
                $"Failed to save cache index: {ex.Message}");
        }
    }

    private sealed class CacheEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool Pinned { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}

