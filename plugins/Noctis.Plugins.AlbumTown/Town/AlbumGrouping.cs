namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>One resident of Album Town: an album (or a single), with what the talk lines need.</summary>
public sealed record TownAlbum(
    string Key,
    string Title,
    string Artist,
    int Year,
    string Genre,
    IReadOnlyList<string> TrackIds,
    string AlbumId,
    string? ArtworkPath,
    int PlayCount,
    DateTimeOffset? LastPlayed,
    DateTimeOffset? DateAdded,
    bool IsSingle,
    bool IsFavorite)
{
    public int TrackCount => TrackIds.Count;
}

/// <summary>
/// Turns library tracks into residents: one creature per album, and singles are their own.
/// Jafezy's rule: songs that share an album cover share one creature (the cover stands in for
/// them), unless a song is a single. So within an album, a song whose cover differs from the
/// album's moves out as a single of its own, and a song without an album is a single too.
/// </summary>
public static class AlbumGrouping
{
    public static IReadOnlyList<TownAlbum> Group(IEnumerable<TrackInfo> tracks)
        => Group(tracks, out _);

    /// <param name="byFilePath">Which resident a track file belongs to (for "now playing").</param>
    public static IReadOnlyList<TownAlbum> Group(IEnumerable<TrackInfo> tracks, out IReadOnlyDictionary<string, string> byFilePath)
        => Group(tracks, out byFilePath, out _);

    /// <param name="byFilePath">Which resident a track file belongs to (for "now playing").</param>
    /// <param name="byTrackId">Which resident a track id belongs to (a playlist song's cover).</param>
    public static IReadOnlyList<TownAlbum> Group(IEnumerable<TrackInfo> tracks, out IReadOnlyDictionary<string, string> byFilePath,
        out IReadOnlyDictionary<string, string> byTrackId)
    {
        var groups = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            var key = AlbumKey(t);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<TrackInfo>();
            list.Add(t);
        }

        var result = new List<TownAlbum>(groups.Count);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, list) in groups)
        {
            if (key.StartsWith("t:", StringComparison.Ordinal))
            {
                Add(result, files, ids, key, list, single: true);
                continue;
            }
            // The album's cover is the one most of its songs show; null (no art) never splits.
            var cover = list.Where(t => t.ArtworkPath is not null)
                .GroupBy(t => t.ArtworkPath!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault();
            var stayers = new List<TrackInfo>(list.Count);
            foreach (var t in list)
            {
                if (cover is not null && t.ArtworkPath is not null && list.Count > 1
                    && !string.Equals(t.ArtworkPath, cover, StringComparison.OrdinalIgnoreCase))
                    Add(result, files, ids, "s:" + t.Id, new List<TrackInfo> { t }, single: true);
                else stayers.Add(t);
            }
            var isSingle = stayers.Count == 1 && list.Count == 1
                || stayers.All(t => string.Equals(t.ReleaseType, "Single", StringComparison.OrdinalIgnoreCase));
            Add(result, files, ids, key, stayers, isSingle);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        byFilePath = files;
        byTrackId = ids;
        return result;
    }

    /// <summary>"a:&lt;AlbumId&gt;", else "n:&lt;artist&gt;|&lt;album&gt;", else "t:&lt;track id&gt;" (no album: a single).</summary>
    public static string AlbumKey(TrackInfo t)
    {
        if (!string.IsNullOrWhiteSpace(t.AlbumId)) return "a:" + t.AlbumId;
        if (string.IsNullOrWhiteSpace(t.Album)) return "t:" + t.Id;
        var artist = string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist;
        return "n:" + Norm(artist) + "|" + Norm(t.Album);
    }

    /// <summary>The artist a song shows in town: its Album Artist tag when it has one, else its
    /// own artist. A song of an album with many artists showed each guest's name in the
    /// Apartments (Discord, Jafezy 11-02); the album artist names them all the same.</summary>
    public static string DisplayArtist(TrackInfo t)
        => (string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist).Trim();

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static void Add(List<TownAlbum> into, Dictionary<string, string> files, Dictionary<string, string> ids, string key, List<TrackInfo> list, bool single)
    {
        if (list.Count == 0) return;
        var ordered = list.OrderBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var first = ordered[0];
        var title = key.StartsWith("a:", StringComparison.Ordinal) || key.StartsWith("n:", StringComparison.Ordinal)
            ? (string.IsNullOrWhiteSpace(first.Album) ? first.Title : first.Album)
            : first.Title;
        // One name per album: the Album Artist tag when any song has one. Songs without it used to
        // vote with their own artist, so a compilation could show a guest's name.
        var artist = Most(ordered.Select(t => t.AlbumArtist)) ?? Most(ordered.Select(t => t.Artist)) ?? "";
        var genre = Most(ordered.Select(t => t.Genre)) ?? "";
        var year = Most(ordered.Where(t => t.Year > 0).Select(t => t.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))) is { } y ? int.Parse(y, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var cover = Most(ordered.Select(t => t.ArtworkPath));
        var album = new TownAlbum(
            key, title.Trim(), artist.Trim(), year, genre.Trim(),
            ordered.Select(t => t.Id).ToArray(),
            ordered.Select(t => t.AlbumId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "",
            cover,
            ordered.Sum(t => Math.Max(0, t.PlayCount)),
            ordered.Max(t => t.LastPlayed),
            ordered.Min(t => t.DateAdded),
            single,
            ordered.Any(t => t.IsFavorite));
        into.Add(album);
        foreach (var t in ordered)
        {
            if (!string.IsNullOrEmpty(t.FilePath)) files[t.FilePath] = key;
            ids[t.Id] = key;
        }
    }

    /// <summary>The most common non-blank value (ties: the first seen).</summary>
    private static string? Most(IEnumerable<string?> values)
    {
        string? best = null;
        var bestCount = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var c = counts[v] = counts.GetValueOrDefault(v) + 1;
            if (c > bestCount) { best = v; bestCount = c; }
        }
        return best;
    }
}
