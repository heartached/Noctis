using System.Globalization;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>Album, EP or single, as the filter chips group release types.</summary>
public enum ReleaseKind { Album, EP, Single }

/// <summary>One album on the reel, built from its library tracks. In Song Roulette a song rides
/// the reel the same way, as an entry of its own (<see cref="IsSong"/>, see <see cref="SongCatalog"/>).</summary>
public sealed record RouletteAlbum(
    string Key,
    string Title,
    string Artist,
    int Year,
    string Genre,
    string ReleaseType,
    IReadOnlyList<string> TrackIds,
    string AlbumId,
    string? ArtworkPath,
    int PlayCount,
    DateTimeOffset? LastPlayed,
    IReadOnlyList<string> Artists)
{
    /// <summary>"Single" and "EP" are their own; everything else (albums, live, soundtracks,
    /// compilations, remixes) counts as an album.</summary>
    public ReleaseKind Kind => ReleaseType.ToLowerInvariant() switch
    {
        "single" => ReleaseKind.Single,
        "ep" => ReleaseKind.EP,
        _ => ReleaseKind.Album,
    };

    public bool IsCompilation => string.Equals(ReleaseType, "Compilation", StringComparison.OrdinalIgnoreCase);

    /// <summary>1990 for 1994; 0 when the year is unknown.</summary>
    public int Decade => Year > 0 ? Year / 10 * 10 : 0;

    public bool NeverPlayed => PlayCount <= 0 && LastPlayed is null;

    /// <summary>A song of its own (Song Roulette), not an album.</summary>
    public bool IsSong => Key.StartsWith(SongCatalog.KeyPrefix, StringComparison.Ordinal);

    /// <summary>The song's album title (Song Roulette); empty for albums.</summary>
    public string AlbumTitle { get; init; } = "";

    /// <summary>The single genres in <see cref="Genre"/>: Noctis keeps several in one tag as
    /// "Rock; Indie" (split on ';' only: "Hip-Hop/Rap" is one genre).</summary>
    public IReadOnlyList<string> Genres { get; } = SplitGenres(Genre);

    /// <summary>Split on ';', trimmed, blanks dropped, case-insensitive duplicates dropped (Noctis's own rule).</summary>
    public static string[] SplitGenres(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Song Roulette: every song is an entry of its own, as if it were a single.</summary>
public static class SongCatalog
{
    public const string KeyPrefix = "s:";

    /// <summary>One entry per library track, in library order. Songs Noctis's own shuffle leaves
    /// out (not liked, Skip when shuffling, snoozed until after <paramref name="now"/>) stay off the reel.</summary>
    public static IReadOnlyList<RouletteAlbum> Build(IEnumerable<TrackInfo> tracks, DateTimeOffset now)
    {
        var result = new List<RouletteAlbum>();
        foreach (var t in tracks)
        {
            if (t.IsDisliked || t.SkipWhenShuffling || t.SnoozedUntil is { } until && until > now) continue;
            var artist = ((string.IsNullOrWhiteSpace(t.Artist) ? t.AlbumArtist : t.Artist) ?? "").Trim();
            var artists = new[] { t.Artist, t.AlbumArtist }
                .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new RouletteAlbum(
                KeyPrefix + t.Id, (t.Title ?? "").Trim(), artist, Math.Max(0, t.Year), (t.Genre ?? "").Trim(),
                string.IsNullOrWhiteSpace(t.ReleaseType) ? "Album" : t.ReleaseType,
                new[] { t.Id }, t.AlbumId ?? "", t.ArtworkPath, Math.Max(0, t.PlayCount), t.LastPlayed, artists)
            {
                AlbumTitle = (t.Album ?? "").Trim(),
            });
        }
        return result;
    }
}

/// <summary>Turns library tracks into albums (one per album, a track without an album is a single of its own).</summary>
public static class AlbumCatalog
{
    public static IReadOnlyList<RouletteAlbum> Group(IEnumerable<TrackInfo> tracks)
    {
        var groups = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            var key = AlbumKey(t);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<TrackInfo>();
            list.Add(t);
        }
        var result = new List<RouletteAlbum>(groups.Count);
        foreach (var (key, list) in groups)
        {
            var ordered = list.OrderBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var first = ordered[0];
            var isAlbum = !key.StartsWith("t:", StringComparison.Ordinal);
            var title = isAlbum && !string.IsNullOrWhiteSpace(first.Album) ? first.Album : first.Title;
            var artist = Most(ordered.Select(t => string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist)) ?? "";
            var year = Most(ordered.Where(t => t.Year > 0).Select(t => t.Year.ToString(CultureInfo.InvariantCulture))) is { } y
                ? int.Parse(y, CultureInfo.InvariantCulture) : 0;
            var releaseType = isAlbum ? Most(ordered.Select(t => t.ReleaseType)) ?? "Album" : "Single";
            var artists = ordered.SelectMany(t => new[] { t.AlbumArtist, t.Artist })
                .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new RouletteAlbum(
                key, title.Trim(), artist.Trim(), year, (Most(ordered.Select(t => t.Genre)) ?? "").Trim(), releaseType,
                ordered.Select(t => t.Id).ToArray(),
                ordered.Select(t => t.AlbumId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "",
                Most(ordered.Select(t => t.ArtworkPath)),
                ordered.Sum(t => Math.Max(0, t.PlayCount)),
                ordered.Max(t => t.LastPlayed),
                artists));
        }
        // A stable order (the Album of the Day hashes into it).
        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    /// <summary>"a:&lt;AlbumId&gt;", else "n:&lt;artist&gt;|&lt;album&gt;", else "t:&lt;track id&gt;".</summary>
    public static string AlbumKey(TrackInfo t)
    {
        if (!string.IsNullOrWhiteSpace(t.AlbumId)) return "a:" + t.AlbumId;
        if (string.IsNullOrWhiteSpace(t.Album)) return "t:" + t.Id;
        var artist = string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist;
        return "n:" + (artist ?? "").Trim().ToLowerInvariant() + "|" + t.Album.Trim().ToLowerInvariant();
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
