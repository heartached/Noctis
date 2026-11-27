using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// What <see cref="LyricsLoader.Load"/> learned about a song's own lyrics, so its next play skips
/// the slow part (beta 6, #mobile-beta: "the lyrics take a while after a song change"). On the
/// phone every look is a provider call through SAF: the sidecar search resolves the song's folder
/// and lists it, and the tag read opens the audio and parses its tags, all again on every play.
/// Remembered per file: that no sidecar is beside it (the search is skipped), and what its tags
/// hold (the tag read is skipped, its text reused, empty included). A sidecar that was found is
/// not remembered: it is read again each play (a fast open), so an edited one shows.
///
/// An entry holds only while the file is the one it was made for (<see cref="StampOf"/>: the size
/// and modified time a rescan records), and <see cref="Clear"/> drops them all (a finished rescan:
/// a sidecar may have been added). Bounded; any thread.
/// </summary>
public sealed class LyricsLookupCache
{
    /// <summary>One file's findings at <paramref name="Stamp"/>: no sidecar beside it, and its
    /// tags' lyrics (null: not read yet).</summary>
    public sealed record Entry(string Stamp, bool NoSidecar, EmbeddedLyricsText? Embedded);

    /// <summary>Most files remembered; past it the cache starts over (a whole library played
    /// through holds at most this many texts).</summary>
    internal const int MaxEntries = 512;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The file as a rescan last saw it: its size and modified time.</summary>
    public static string StampOf(Track track) => $"{track.FileSize}:{track.LastModified.Ticks}";

    /// <summary>Files remembered (tests).</summary>
    public int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    /// <summary>The findings for <paramref name="track"/>'s file, or null when there are none or
    /// they were made for another version of it.</summary>
    public Entry? Get(Track track)
    {
        if (string.IsNullOrEmpty(track.FilePath)) return null;
        lock (_entries)
            return _entries.TryGetValue(track.FilePath, out var entry) && entry.Stamp == StampOf(track) ? entry : null;
    }

    public void Set(Track track, bool noSidecar, EmbeddedLyricsText? embedded)
    {
        if (string.IsNullOrEmpty(track.FilePath)) return;
        if (!noSidecar && embedded == null)
        {
            Forget(track);
            return;
        }
        lock (_entries)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(track.FilePath)) _entries.Clear();
            _entries[track.FilePath] = new Entry(StampOf(track), noSidecar, embedded);
        }
    }

    public void Forget(Track track)
    {
        if (string.IsNullOrEmpty(track.FilePath)) return;
        lock (_entries) _entries.Remove(track.FilePath);
    }

    public void Clear()
    {
        lock (_entries) _entries.Clear();
    }
}
