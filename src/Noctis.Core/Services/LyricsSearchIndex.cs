using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Search songs by their lyrics (Discord, Cosmo 11-08: "include lyrics" / "lyrics only").
///
/// Lyric text is not in memory: the song's own lyrics live one file per track in
/// <see cref="LyricsStore"/> and online-fetched ones in lyrics_cache\{id}.lrc. Reading those per
/// keystroke would be thousands of file reads, so the first lyric search reads each song once
/// (off the UI thread) and keeps a search key per line — lower-cased, accents, punctuation and
/// spaces folded like <see cref="SearchText.Normalize"/>, LRC timestamps, word tags, voice
/// markers and [ar:] tags stripped — plus the cleaned lines for the result's snippet. Later
/// searches only re-read songs whose lyrics changed (<see cref="Track.LyricsEditVersion"/>, the
/// cache file's write time).
///
/// Indexed: the song's lyrics (embedded tags, the scanned/saved lyrics) and, for songs without
/// any, the online cache's .lrc — the same order the lyrics page probes them in. NOT indexed:
/// .lrc/.elrc/.ttml/.lyricsfile sidecars next to the audio files. They are not in the library
/// (the scan reads tags only) and finding them costs up to 12 existence checks per song in the
/// music folders (the lyrics page probe, three spellings × four extensions), which on a 20k-song
/// library on a NAS or spinning disk is far too slow for a search box.
/// </summary>
public sealed class LyricsSearchIndex
{
    /// <summary>Minimum query key length (letters/digits) for lyric matching: one or two letters
    /// occur in nearly every song's lyrics and would list the whole library.</summary>
    public const int MinQueryLength = 3;

    private sealed class Entry
    {
        public int Version = -1;
        public DateTime CacheStamp;
        /// <summary>Cleaned display lines and their search keys (same length, same order).</summary>
        public string[] Lines = Array.Empty<string>();
        public string[] Keys = Array.Empty<string>();
    }

    private static readonly Lazy<LyricsSearchIndex> SharedInstance =
        new(() => new LyricsSearchIndex(Path.Combine(AppPaths.DataRoot, "lyrics_cache")));

    /// <summary>The app's index (lyrics_cache under the data folder).</summary>
    public static LyricsSearchIndex Shared => SharedInstance.Value;

    private readonly string? _cacheDirectory;
    // Keyed by the Track object: a rescanned or re-added song is a new object with a fresh
    // edit version, and entries of removed songs go away with them.
    private readonly ConditionalWeakTable<Track, Entry> _entries = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<Guid, (string Path, DateTime Stamp)> _cacheFiles = new();
    private long _cacheListedAt;

    /// <summary>The cache folder is listed again at most this often (every keystroke searches).</summary>
    private static readonly TimeSpan CacheListInterval = TimeSpan.FromSeconds(5);

    public LyricsSearchIndex(string? cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// Brings the index up to date with <paramref name="tracks"/>: reads the songs not seen yet
    /// and those whose lyrics changed since. Call off the UI thread. Concurrent calls queue.
    /// </summary>
    public void Refresh(IReadOnlyList<Track> tracks, CancellationToken cancellation = default)
    {
        _refreshGate.Wait(cancellation);
        try
        {
            if (_cacheListedAt == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_cacheListedAt) > CacheListInterval)
            {
                _cacheFiles = ListCacheFiles();
                _cacheListedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            HashSet<Guid>? storedIds = null;
            LyricsStore? listedStore = null;

            foreach (var track in tracks)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = _entries.GetValue(track, _ => new Entry());
                _cacheFiles.TryGetValue(track.Id, out var cache);
                if (entry.Version == track.LyricsEditVersion && entry.CacheStamp == cache.Stamp)
                    continue;

                // One listing of the store per pass (every track of a library shares one store).
                var store = track.AttachedLyricsStore;
                if (store != null && !ReferenceEquals(store, listedStore))
                {
                    storedIds = store.StoredIds();
                    listedStore = store;
                }

                var version = track.LyricsEditVersion;
                var text = ReadText(track, store != null ? storedIds : null, cache.Path);
                var lines = SplitLines(text);
                var keys = new string[lines.Length];
                for (var i = 0; i < lines.Length; i++) keys[i] = SearchText.Normalize(lines[i]);

                lock (entry)
                {
                    entry.Lines = lines;
                    entry.Keys = keys;
                    entry.Version = version;
                    entry.CacheStamp = cache.Stamp;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Folds a query the way lyric lines are keyed; "" when too short to search lyrics.</summary>
    public static string QueryKey(string? query)
    {
        var key = SearchText.Normalize(query);
        return key.Length >= MinQueryLength ? key : string.Empty;
    }

    /// <summary>
    /// True when a line of <paramref name="track"/>'s indexed lyrics contains
    /// <paramref name="queryKey"/> (from <see cref="QueryKey"/>); <paramref name="snippet"/> is that
    /// line as written. False for an empty key or a song not indexed yet.
    /// </summary>
    public bool TryMatch(Track track, string queryKey, out string? snippet)
    {
        snippet = null;
        if (queryKey.Length == 0 || !_entries.TryGetValue(track, out var entry)) return false;
        string[] lines, keys;
        lock (entry) { lines = entry.Lines; keys = entry.Keys; }
        for (var i = 0; i < keys.Length; i++)
        {
            if (!keys[i].Contains(queryKey, StringComparison.Ordinal)) continue;
            snippet = lines[i];
            return true;
        }
        return false;
    }

    // The song's own lyrics win; the online cache only stands in for songs without any.
    private static string ReadText(Track track, IReadOnlySet<Guid>? storedIds, string? cachePath)
    {
        var (plain, synced) = track.ReadLyricsForSearch(storedIds);
        // Timed lyrics carry the same words as the plain text (when both exist) — one of them.
        var own = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
        if (!string.IsNullOrWhiteSpace(own)) return own;
        if (cachePath == null) return string.Empty;
        try { return File.ReadAllText(cachePath); }
        catch { return string.Empty; } // deleted or locked meanwhile: nothing to search
    }

    private Dictionary<Guid, (string Path, DateTime Stamp)> ListCacheFiles()
    {
        var files = new Dictionary<Guid, (string, DateTime)>();
        if (string.IsNullOrEmpty(_cacheDirectory)) return files;
        try
        {
            var dir = new DirectoryInfo(_cacheDirectory);
            if (!dir.Exists) return files;
            foreach (var file in dir.EnumerateFiles("*.lrc"))
            {
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var id))
                    files[id] = (file.FullName, file.LastWriteTimeUtc);
            }
        }
        catch
        {
            // Unreadable cache folder: search the songs' own lyrics only.
        }
        return files;
    }

    private static readonly Regex TtmlLineEnd = new(@"</p\s*>|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XmlTag = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex TtmlHead = new(@"<head\b.*?</head\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>The lyric lines to search and show: timestamps and tags stripped, blank lines
    /// and exact repeats (choruses) dropped. Internal for tests.</summary>
    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var cleaned = LyricsTextHelper.CleanDisplayText(text);
        // A whole TTML document pasted as the lyrics (GitHub #132): its words, one <p> a line.
        if (cleaned.TrimStart().StartsWith('<') && cleaned.Contains("<tt", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = TtmlHead.Replace(cleaned, string.Empty);
            cleaned = TtmlLineEnd.Replace(cleaned, "\n");
            cleaned = XmlTag.Replace(cleaned, string.Empty);
            cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
        }
        else
        {
            cleaned = LyricsTextHelper.StripTimestamps(cleaned);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var raw in cleaned.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !seen.Add(line)) continue;
            lines.Add(line);
        }
        return lines.ToArray();
    }
}

/// <summary>Whether a Songs search also looks in the lyrics (Discord, Cosmo 11-08).</summary>
public enum LyricsSearchMode
{
    /// <summary>Titles, artists, albums and the other tags only (the default).</summary>
    Off,
    /// <summary>Tags or lyrics.</summary>
    Include,
    /// <summary>Lyrics only.</summary>
    Only,
}
