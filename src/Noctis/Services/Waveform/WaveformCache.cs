using System.Security.Cryptography;
using System.Text;
using Noctis.Helpers;

namespace Noctis.Services.Waveform;

/// <summary>
/// Identity of an audio file for waveform caching: the path plus the size and last-write
/// time, so an edited or replaced file (re-encode, tag rewrite that changes the size or
/// timestamp) gets a new key and never shows the old file's waveform.
/// </summary>
public readonly record struct WaveformFileIdentity(string Path, long Size, long LastWriteUtcTicks)
{
    /// <summary>Resolves a local file's identity; null when it does not exist or cannot be read.</summary>
    public static WaveformFileIdentity? TryResolve(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new WaveformFileIdentity(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Two-level waveform cache: a small in-memory LRU for the tracks around the current one
/// and small binary files under <c>&lt;data&gt;/cache/waveforms</c> (the folder Settings →
/// "Clear cache" empties). The disk side is bounded: after writes, the oldest files by
/// last-write time (a disk hit re-stamps its file, so this is least-recently-used) are
/// deleted until the folder is under 80% of <see cref="MaxBytes"/>.
/// Thread-safe; all disk work belongs on a background thread.
/// </summary>
public sealed class WaveformCache
{
    public const long DefaultMaxBytes = 32L * 1024 * 1024;
    public const int DefaultMemoryEntries = 8;

    /// <summary>Bump when the file layout or the reduction changes: old entries then miss
    /// (their key no longer matches) and age out through the size cap.</summary>
    internal const int FormatVersion = 1;
    internal const string Extension = ".nwf";
    private static readonly byte[] Magic = "NWF1"u8.ToArray();
    private const int HeaderBytes = 4 + 4 + 8 + 8 + 4;
    private const int MaxBuckets = 1 << 16;
    private const int TrimEveryWrites = 32;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly LinkedList<(string Key, WaveformData Data)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, WaveformData Data)>> _memory = new(StringComparer.Ordinal);
    private int _writesSinceTrim = TrimEveryWrites; // first write of the session trims

    public WaveformCache(string directory, long maxBytes = DefaultMaxBytes, int memoryEntries = DefaultMemoryEntries)
    {
        Directory = directory;
        MaxBytes = maxBytes;
        MemoryEntries = Math.Max(1, memoryEntries);
    }

    public static string DefaultDirectory => System.IO.Path.Combine(AppPaths.DataRoot, "cache", "waveforms");

    public string Directory { get; }
    public long MaxBytes { get; }
    public int MemoryEntries { get; }

    /// <summary>
    /// Stable cache key: SHA-256 over the normalized path (case-folded where the file
    /// system is case-insensitive), size, last-write ticks and the format version.
    /// </summary>
    public static string KeyFor(WaveformFileIdentity id)
    {
        var path = System.IO.Path.GetFullPath(id.Path);
        if (!OperatingSystem.IsLinux()) path = path.ToUpperInvariant();
        var text = $"{path}|{id.Size}|{id.LastWriteUtcTicks}|v{FormatVersion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal string FilePathFor(string key) => System.IO.Path.Combine(Directory, key + Extension);

    /// <summary>Memory-only lookup (no I/O).</summary>
    public bool TryGetMemory(WaveformFileIdentity id, out WaveformData data)
    {
        var key = KeyFor(id);
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                data = node.Value.Data;
                return true;
            }
        }
        data = null!;
        return false;
    }

    /// <summary>Memory, then disk. A disk hit is promoted into memory. Null on a miss or a
    /// corrupt/foreign file (which is deleted).</summary>
    public WaveformData? TryLoad(WaveformFileIdentity id)
    {
        if (TryGetMemory(id, out var hit)) return hit;

        var key = KeyFor(id);
        var file = FilePathFor(key);
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            var data = Parse(bytes, id);
            if (data == null)
            {
                TryDelete(file);
                return null;
            }
            TouchIfStale(file);
            Remember(key, data);
            return data;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.CacheRead", ex.Message);
            return null;
        }
    }

    /// <summary>Stores in memory and on disk (atomic temp-file + move), then trims.</summary>
    public void Store(WaveformFileIdentity id, WaveformData data)
    {
        var key = KeyFor(id);
        Remember(key, data);
        try
        {
            System.IO.Directory.CreateDirectory(Directory); // "Clear cache" may have removed it
            var file = FilePathFor(key);
            var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tmp, Serialize(data, id));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.CacheWrite", ex.Message);
            return;
        }

        bool trim;
        lock (_lock)
        {
            trim = ++_writesSinceTrim >= TrimEveryWrites;
            if (trim) _writesSinceTrim = 0;
        }
        if (trim) Trim();
    }

    /// <summary>
    /// Deletes the least recently used files until the folder is under 80% of the cap
    /// (hysteresis, so a full cache does not trim on every write). Also sweeps temp files
    /// orphaned by a crash mid-write.
    /// </summary>
    public void Trim()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var dir = new DirectoryInfo(Directory);
            var now = DateTime.UtcNow;
            foreach (var tmp in dir.EnumerateFiles("*" + Extension + ".tmp-*"))
            {
                if (now - tmp.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(tmp.FullName);
            }

            var files = dir.EnumerateFiles("*" + Extension).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= MaxBytes) return;

            var target = MaxBytes * 8 / 10;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                if (TryDelete(f.FullName)) total -= f.Length;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.CacheTrim", ex.Message);
        }
    }

    private void Remember(string key, WaveformData data)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _memory.Remove(key);
            }
            var node = _lru.AddFirst((key, data));
            _memory[key] = node;
            while (_lru.Count > MemoryEntries)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _memory.Remove(last.Value.Key);
            }
        }
    }

    internal static byte[] Serialize(WaveformData data, WaveformFileIdentity id)
    {
        var n = data.BucketCount;
        var bytes = new byte[HeaderBytes + 2 * n];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BitConverter.TryWriteBytes(span[4..], FormatVersion);
        BitConverter.TryWriteBytes(span[8..], id.Size);
        BitConverter.TryWriteBytes(span[16..], id.LastWriteUtcTicks);
        BitConverter.TryWriteBytes(span[24..], n);
        data.Peaks.CopyTo(span[HeaderBytes..]);
        data.Rms.CopyTo(span[(HeaderBytes + n)..]);
        return bytes;
    }

    /// <summary>Parses a cache file; null unless magic, version, source identity and
    /// length all check out (a hash collision or a stale file never renders).</summary>
    internal static WaveformData? Parse(byte[] bytes, WaveformFileIdentity id)
    {
        if (bytes.Length < HeaderBytes) return null;
        var span = bytes.AsSpan();
        if (!span[..4].SequenceEqual(Magic)) return null;
        if (BitConverter.ToInt32(span[4..]) != FormatVersion) return null;
        if (BitConverter.ToInt64(span[8..]) != id.Size) return null;
        if (BitConverter.ToInt64(span[16..]) != id.LastWriteUtcTicks) return null;
        var n = BitConverter.ToInt32(span[24..]);
        if (n <= 0 || n > MaxBuckets || bytes.Length != HeaderBytes + 2 * n) return null;
        return new WaveformData(span.Slice(HeaderBytes, n).ToArray(), span.Slice(HeaderBytes + n, n).ToArray());
    }

    private static void TouchIfStale(string file)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(file) > TouchInterval)
                File.SetLastWriteTimeUtc(file, now);
        }
        catch
        {
            // LRU order is a nicety; a read-only cache still serves hits.
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
