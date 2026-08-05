using System.Collections.Concurrent;

namespace NoctisCoverProxy;

/// <summary>
/// Thread-safe in-memory store for published cover art images.
/// Each image expires after a configurable TTL (default 60 s).
/// </summary>
public sealed class CoverArtStore : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);
    private readonly Timer _cleanupTimer;

    // Aggregate bounds: /ws accepts unauthenticated connections, so without caps
    // many connections x many content_ids could grow the store without limit.
    // A legitimate client publishes one now-playing cover at a time, so these
    // are generous. Checks are best-effort under concurrency (a racing pair of
    // puts can overshoot slightly); they bound the store, not account exactly.
    private const int MaxEntriesPerClient = 16;
    private const int MaxTotalEntries = 4096;
    private const long MaxTotalBytes = 128 * 1024 * 1024;

    private sealed record CacheEntry(byte[] JpegBytes, string ContentType, DateTime ExpiresAt);

    public CoverArtStore()
    {
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Stores the image; false when a store limit would be exceeded.</summary>
    public bool Put(string key, byte[] jpegBytes)
    {
        // Replacing an existing entry (re-publish of the same content_id) is
        // always allowed — it does not grow the entry count.
        if (!_entries.ContainsKey(key))
        {
            var clientPrefix = key[..(key.IndexOf('/') + 1)];
            var clientCount = 0;
            var totalEntries = 0;
            long totalBytes = 0;
            foreach (var kvp in _entries)
            {
                totalEntries++;
                totalBytes += kvp.Value.JpegBytes.Length;
                if (kvp.Key.StartsWith(clientPrefix, StringComparison.Ordinal))
                    clientCount++;
            }

            if (clientCount >= MaxEntriesPerClient ||
                totalEntries >= MaxTotalEntries ||
                totalBytes + jpegBytes.Length > MaxTotalBytes)
                return false;
        }

        _entries[key] = new CacheEntry(jpegBytes, "image/jpeg", DateTime.UtcNow + _ttl);
        return true;
    }

    public (byte[] Bytes, string ContentType)? Get(string key)
    {
        // No refresh-on-access: only the publisher (via a re-publish) may extend
        // an entry's life. A third party polling /art must not be able to keep
        // content alive on this server indefinitely.
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return (entry.JpegBytes, entry.ContentType);

        return null;
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);

    public void RemoveByPrefix(string prefix)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                _entries.TryRemove(kvp.Key, out _);
        }
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.ExpiresAt < now)
                _entries.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
