using System.Collections.Generic;
using System.IO;

namespace Noctis.Services;

/// <summary>
/// MP3 files that wedged LibVLC's mpg123 decoder on damaged data (see DecoderWedgeDetector)
/// or in which damaged data was found ahead of time (see Mp3FrameScan.FindDamage),
/// remembered across restarts so the next play opens them on FFmpeg's decoder from the
/// start and never reaches the damage on mpg123 again (Discord 11-03: the recovery worked,
/// but every play of the file still dropped out at the damaged spot). One line per file:
/// size, last-write ticks (UTC) and path, tab-separated; an entry whose file was replaced
/// (another size) no longer counts. A null store path keeps it in memory
/// (tests). Thread-safe.
/// </summary>
internal sealed class Mp3DecoderFallbackList
{
    private readonly string? _storePath;
    private readonly object _gate = new();
    private Dictionary<string, (long Bytes, long Ticks)>? _entries;

    public Mp3DecoderFallbackList(string? storePath) => _storePath = storePath;

    /// <summary>
    /// True when <paramref name="path"/> was added and the file is still the same one: the same
    /// size. The write time alone does not count, because the app rewrites tags in place (a
    /// star rating or a dislike is written into the file: LibraryService.QueueRatingTagWrites),
    /// which moved it and dropped the entry, so the next play hit the damage on mpg123 again.
    /// </summary>
    public bool Contains(string path)
    {
        (long Bytes, long Ticks) entry;
        lock (_gate)
        {
            if (!Load().TryGetValue(path, out entry)) return false;
        }
        return Stamp(path) is { } now && now.Bytes == entry.Bytes;
    }

    /// <summary>Remembers <paramref name="path"/> as it is now; false when it was already there.</summary>
    public bool Add(string path)
    {
        if (Stamp(path) is not { } stamp) return false;
        lock (_gate)
        {
            var entries = Load();
            if (entries.TryGetValue(path, out var old) && old == stamp) return false;
            entries[path] = stamp;
            if (_storePath == null) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                File.AppendAllText(_storePath, $"{stamp.Bytes}\t{stamp.Ticks}\t{path}\n");
            }
            catch { /* best effort: the session still uses the entry */ }
            return true;
        }
    }

    /// <summary>Forgets every entry and deletes the store (Settings › Reset Everything).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = new Dictionary<string, (long, long)>(StringComparer.FromComparison(Helpers.PathComparison.Comparison));
            if (_storePath == null) return;
            try { File.Delete(_storePath); }
            catch { /* best effort, as Add */ }
        }
    }

    private Dictionary<string, (long, long)> Load()
    {
        if (_entries != null) return _entries;
        _entries = new Dictionary<string, (long, long)>(StringComparer.FromComparison(Helpers.PathComparison.Comparison));
        if (_storePath == null) return _entries;
        try
        {
            if (!File.Exists(_storePath)) return _entries;
            foreach (var line in File.ReadLines(_storePath))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length == 3 && long.TryParse(parts[0], out var bytes) && long.TryParse(parts[1], out var ticks)
                    && parts[2].Length > 0)
                    _entries[parts[2]] = (bytes, ticks); // a later line for the same path wins
            }
        }
        catch { /* unreadable: start empty */ }
        return _entries;
    }

    private static (long Bytes, long Ticks)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch { return null; }
    }
}
