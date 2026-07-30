using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// Disk-backed store for the per-track lyric text that used to live inline on
/// <see cref="Models.Track.Lyrics"/> / <see cref="Models.Track.SyncedLyrics"/> in
/// library.json. On a measured real library those two strings were 56% of a
/// 46 MB library.json — all of it deserialized into always-resident RAM at
/// startup and re-serialized on every rating/play-count save. The store keeps
/// one small JSON file per track (&lt;data&gt;\lyrics_store\{trackId}.json holding
/// both the plain and synced text), loaded lazily on first read.
///
/// This is deliberately NOT the %APPDATA% lyrics_cache directory the lyrics
/// page writes ({id}.lrc / {id}.lyricsfile): that directory is an evictable
/// cache of ONLINE-fetched payloads and sits at priority 5 in the local lyric
/// probe, below embedded tags (priority 4). This store IS the embedded-tag
/// field — authoritative user/tag data that must never be evicted and must
/// keep its priority-4 slot — so unifying the two would silently reshuffle
/// probe priorities and RemoveLyrics semantics.
///
/// A small LRU keeps the most recently read entries in memory so the multiple
/// consecutive reads the lyrics pipeline does per track change hit disk once;
/// the bound means lyrics no longer accumulate for every track ever played.
/// Reads are synchronous by design — Track's property getters are sync and
/// have too many consumers to make async; a one-off ~10 KB read is cheap and
/// the hot path (the lyrics page probe) already runs it off the UI thread.
/// </summary>
public sealed class LyricsStore
{
    /// <summary>Both lyric fields for one track. Fields are never null.</summary>
    public readonly record struct LyricsPair(string Plain, string Synced)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Plain) && string.IsNullOrEmpty(Synced);
        public static readonly LyricsPair Empty = new(string.Empty, string.Empty);
    }

    // On-disk shape. Property names match the old library.json field names so
    // a store file is self-describing next to the format it replaced.
    private sealed class StoredLyrics
    {
        public string? Lyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int CacheCapacity = 16;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LinkedListNode<KeyValuePair<Guid, LyricsPair>>> _cache = new();
    private readonly LinkedList<KeyValuePair<Guid, LyricsPair>> _lru = new();

    public LyricsStore(string directory)
    {
        _directory = directory;
    }

    public string GetPath(Guid trackId) => Path.Combine(_directory, $"{trackId}.json");

    public bool Exists(Guid trackId) => File.Exists(GetPath(trackId));

    /// <summary>Reads both lyric fields for a track; empty pair when none stored.</summary>
    public LyricsPair Read(Guid trackId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(trackId, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
        }

        var pair = ReadFromDisk(trackId);
        lock (_gate) Insert(trackId, pair);
        return pair;
    }

    /// <summary>
    /// Writes both lyric fields for a track. An all-empty pair deletes the file.
    /// Uses temp-then-rename so a torn write can never land at the final name —
    /// migration's write-if-absent check depends on a present file being complete.
    /// </summary>
    public void Write(Guid trackId, string plain, string synced)
    {
        plain ??= string.Empty;
        synced ??= string.Empty;
        var path = GetPath(trackId);

        if (plain.Length == 0 && synced.Length == 0)
        {
            try { File.Delete(path); } catch { /* absent or locked — cache still cleared */ }
            lock (_gate) Insert(trackId, LyricsPair.Empty);
            return;
        }

        Directory.CreateDirectory(_directory);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath,
            JsonSerializer.Serialize(new StoredLyrics { Lyrics = plain, SyncedLyrics = synced }, FileJson));
        File.Move(tempPath, path, overwrite: true);
        lock (_gate) Insert(trackId, new LyricsPair(plain, synced));
    }

    /// <summary>
    /// Migration write: persists the pair only when no store file exists yet.
    /// An existing file always wins — after a crash mid-migration the old
    /// library.json still carries lyrics, and blindly re-writing them could
    /// clobber a newer in-store edit made before the crash.
    /// </summary>
    public bool WriteIfAbsent(Guid trackId, string plain, string synced)
    {
        if (string.IsNullOrEmpty(plain) && string.IsNullOrEmpty(synced)) return false;
        if (Exists(trackId)) return false;
        Write(trackId, plain, synced);
        return true;
    }

    private LyricsPair ReadFromDisk(Guid trackId)
    {
        try
        {
            var path = GetPath(trackId);
            if (!File.Exists(path)) return LyricsPair.Empty;
            var stored = JsonSerializer.Deserialize<StoredLyrics>(File.ReadAllText(path), FileJson);
            return stored is null
                ? LyricsPair.Empty
                : new LyricsPair(stored.Lyrics ?? string.Empty, stored.SyncedLyrics ?? string.Empty);
        }
        catch
        {
            // Unreadable/corrupt entry: behave as "no lyrics stored" rather than
            // failing every consumer of the property getter.
            return LyricsPair.Empty;
        }
    }

    private void Insert(Guid trackId, LyricsPair pair)
    {
        if (_cache.TryGetValue(trackId, out var existing))
        {
            _lru.Remove(existing);
            _cache.Remove(trackId);
        }

        var node = _lru.AddFirst(new KeyValuePair<Guid, LyricsPair>(trackId, pair));
        _cache[trackId] = node;

        while (_cache.Count > CacheCapacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
        }
    }
}
