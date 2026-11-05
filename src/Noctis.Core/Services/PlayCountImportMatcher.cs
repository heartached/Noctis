using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

// GitHub #160 (play count import) matching, moved here from the desktop's PlayCountImport.cs
// so the Last.fm mixes (#177) and the phone match Last.fm songs to the library the same way.

/// <summary>How an imported count combines with the song's count in Noctis.</summary>
public enum PlayCountImportMode
{
    /// <summary>The higher of the two (default): plays counted only in Noctis are never lost.</summary>
    KeepHigher,
    /// <summary>The imported count, even when it is lower.</summary>
    Replace,
}

/// <summary>Plays of one artist/title/album combination as the service reports them.</summary>
public sealed record SourcePlayCount(string Artist, string Title, string? Album, int Count);

/// <summary>What an import matched; <see cref="Counts"/> is the imported count per library song.</summary>
public sealed class PlayCountMatchResult
{
    public Dictionary<Track, int> Counts { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Distinct artist/title/album entries the service reported.</summary>
    public int SourceEntries { get; set; }
    public int MatchedEntries { get; set; }
    public int UnmatchedEntries => SourceEntries - MatchedEntries;
    public int MatchedPlays { get; set; }
    public int UnmatchedPlays { get; set; }
}

/// <summary>
/// Matches imported counts to library songs and applies them.
/// <para>Matching: artist and title compared folded (case, accents, punctuation and "&amp;"/"and"
/// ignored) with feat. credits and edition/remaster decoration dropped from the title, while
/// version markers (live, remix, acoustic...) must agree — a live take's plays never land on
/// the studio song. The artist may be the song's artist, its album artist, or the first
/// credited name of either side ("Daft Punk" finds "Daft Punk feat. Pharrell Williams").</para>
/// <para>Releases: when the song is on several albums in the library and the listen names an
/// album, the plays go to that album's copy (edition suffixes ignored: "Album (Deluxe)" is
/// "Album"). When the listen names no album, or one the library doesn't have, all of those
/// plays go to ONE copy — the one played most in Noctis, then a non-compilation, then the
/// earliest year — rather than being split: a split would invent per-album counts the
/// service never reported, and copying would count the same plays several times. Several
/// copies on the same album (duplicate files) are treated the same way.</para>
/// </summary>
public static class PlayCountImportMatcher
{
    public static PlayCountMatchResult Match(IReadOnlyList<Track> library, IEnumerable<SourcePlayCount> source)
    {
        var index = new LibrarySongIndex(library);
        var result = new PlayCountMatchResult();
        foreach (var entry in source)
        {
            result.SourceEntries++;
            var target = index.Find(entry.Artist, entry.Title, entry.Album);
            if (target == null)
            {
                result.UnmatchedPlays += entry.Count;
                continue;
            }
            result.MatchedEntries++;
            result.MatchedPlays += entry.Count;
            result.Counts[target] = result.Counts.TryGetValue(target, out var c) ? c + entry.Count : entry.Count;
        }
        return result;
    }

    /// <summary>
    /// Writes the counts onto the songs and returns the ones that changed. Call on the UI
    /// thread (PlayCount is observable); the current count is read here, so a play counted
    /// while the history was downloading is kept by <see cref="PlayCountImportMode.KeepHigher"/>.
    /// LastPlayed is left alone: an imported count has no date.
    /// </summary>
    public static List<Track> Apply(PlayCountMatchResult match, PlayCountImportMode mode)
    {
        var changed = new List<Track>();
        foreach (var (track, imported) in match.Counts)
        {
            var next = mode == PlayCountImportMode.Replace ? imported : Math.Max(track.PlayCount, imported);
            if (next == track.PlayCount) continue;
            track.PlayCount = next;
            changed.Add(track);
        }
        return changed;
    }

    internal static Track PickTarget(List<Track> candidates, string? album)
    {
        if (candidates.Count == 1) return candidates[0];
        IEnumerable<Track> pool = candidates;
        if (!string.IsNullOrWhiteSpace(album))
        {
            var onAlbum = candidates.Where(t => SameAlbum(t.Album, album)).ToList();
            if (onAlbum.Count > 0) pool = onAlbum;
        }
        return pool
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.IsCompilation)
            .ThenBy(t => t.Year > 0 ? t.Year : int.MaxValue)
            .ThenBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    internal static bool SameAlbum(string? a, string? b)
    {
        var fa = MatchText.Fold(a);
        var fb = MatchText.Fold(b);
        if (fa.Length == 0 || fb.Length == 0) return false;
        if (fa == fb) return true;
        var na = MatchText.Fold(AlbumTitleNormalizer.Normalize(a));
        var nb = MatchText.Fold(AlbumTitleNormalizer.Normalize(b));
        return na.Length > 0 && na == nb;
    }

    /// <summary>Folded base title plus its version markers ("song|live").</summary>
    internal static string TitleKey(string? title)
    {
        var (b, markers) = MatchText.AnalyzeTitle(title);
        return markers.Count == 0 ? b : b + "|" + string.Join(",", markers.OrderBy(m => m, StringComparer.Ordinal));
    }

    /// <summary>The whole folded credit first, then the first credited name.</summary>
    internal static IEnumerable<string> ArtistKeys(string? artist)
    {
        var whole = MatchText.Fold(artist);
        if (whole.Length == 0) yield break;
        yield return whole;
        var names = MatchText.SplitArtists(artist);
        if (names.Count > 0 && names[0] != whole) yield return names[0];
    }
}

/// <summary>
/// The library indexed by folded title + artist, as <see cref="PlayCountImportMatcher"/> matches
/// (see its remarks): build once per library snapshot, then <see cref="Find"/> per service song.
/// Used by the play count import and the Last.fm mixes (GitHub #177). Not thread-safe to build
/// concurrently with library edits; reading a built index from one thread is fine.
/// </summary>
public sealed class LibrarySongIndex
{
    private readonly Dictionary<string, List<Track>> _byTitleArtist = new(StringComparer.Ordinal);

    public LibrarySongIndex(IEnumerable<Track> library)
    {
        foreach (var t in library)
        {
            if (t.IsExternal || string.IsNullOrWhiteSpace(t.Title)) continue;
            var title = PlayCountImportMatcher.TitleKey(t.Title);
            foreach (var a in PlayCountImportMatcher.ArtistKeys(t.Artist).Concat(PlayCountImportMatcher.ArtistKeys(t.AlbumArtist)).Distinct())
            {
                var key = title + "\u001f" + a;
                if (!_byTitleArtist.TryGetValue(key, out var list)) _byTitleArtist[key] = list = new List<Track>();
                if (!list.Contains(t)) list.Add(t);
            }
        }
    }

    /// <summary>The library's copy of the song, or null. Several copies: the one on
    /// <paramref name="album"/> when given and present, else the most played / non-compilation /
    /// earliest one.</summary>
    public Track? Find(string? artist, string? title, string? album = null)
    {
        var candidates = Candidates(artist, title);
        return candidates == null ? null : PlayCountImportMatcher.PickTarget(candidates, album);
    }

    private List<Track>? Candidates(string? artist, string? title)
    {
        var key = PlayCountImportMatcher.TitleKey(title);
        if (key.Length == 0) return null;
        foreach (var a in PlayCountImportMatcher.ArtistKeys(artist))
            if (_byTitleArtist.TryGetValue(key + "\u001f" + a, out var list) && list.Count > 0)
                return list;
        return null;
    }
}
