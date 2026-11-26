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
    /// <summary>The album's cover. A server album's arrives later (<see cref="ServerAlbumId"/>), so it can be set.</summary>
    public string? ArtworkPath { get; set; } = ArtworkPath;

    /// <summary>Set for an album of the user's media servers (GitHub #268): it has no library
    /// tracks (<see cref="TrackIds"/> is empty) and plays through the server.</summary>
    public string? ServerAlbumId { get; init; }

    /// <summary>The server it plays from (server albums only).</summary>
    public string ServerName { get; init; } = "";

    public bool IsServer => ServerAlbumId is not null;

    /// <summary>False for a server album whose server does not say whether it was played: unknown
    /// is not never, so the play filters leave it out. Library albums always know.</summary>
    public bool PlaysKnown { get; init; } = true;

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

    public bool NeverPlayed => PlaysKnown && PlayCount <= 0 && LastPlayed is null;

    /// <summary>True when it is known not to have been played since <paramref name="since"/>
    /// (never played counts). A server album played on a date its server does not give is unknown.</summary>
    public bool NotPlayedSince(DateTimeOffset since)
    {
        if (!PlaysKnown) return false;
        if (LastPlayed is { } last) return last <= since;
        return !IsServer || PlayCount <= 0;
    }

    /// <summary>A song of its own (Song Roulette), not an album.</summary>
    public bool IsSong => Key.StartsWith(SongCatalog.KeyPrefix, StringComparison.Ordinal);

    /// <summary>The song's album title (Song Roulette); empty for albums.</summary>
    public string AlbumTitle { get; init; } = "";

    /// <summary>The Language chip's value (<see cref="ScriptLanguage"/>, GitHub #202): told from
    /// the script of the titles and artist, and later from the lyrics when those are in Latin
    /// letters (<see cref="Views.RouletteView.ReadLyricsLanguagesAsync"/>). Null when unknown.</summary>
    public string? Language { get; set; }

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
                Language = ScriptLanguage.Detect(t.Title, t.Album, t.Artist),
            });
        }
        return result;
    }
}

/// <summary>An album of one of the user's media servers, as Album Roulette keeps it
/// (its own type, so nothing here needs plugin API 1.9's ServerAlbumInfo to load).
/// <paramref name="PlayCount"/> and <paramref name="LastPlayed"/> are what its server says;
/// both null = the server does not say.</summary>
public sealed record RemoteAlbum(string Id, string Title, string Artist, int Year, string Genre, string ServerName, string? ArtworkPath,
    int? PlayCount = null, DateTimeOffset? LastPlayed = null);

/// <summary>Turns library tracks into albums (one per album, a track without an album is a single of its own).</summary>
public static class AlbumCatalog
{
    public const string ServerKeyPrefix = "r:";

    /// <summary>
    /// The library's albums plus the media servers' albums (GitHub #268). A server album that
    /// is also in the library (same album artist and title) is left out: the library copy
    /// plays from this computer and has the play counts. Stable order (Album of the Day).
    /// </summary>
    public static IReadOnlyList<RouletteAlbum> WithServerAlbums(IReadOnlyList<RouletteAlbum> library, IEnumerable<RemoteAlbum> server)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in library) known.Add(NameKey(a.Artist, a.Title));
        var result = new List<RouletteAlbum>(library);
        var added = false;
        foreach (var r in server)
        {
            if (string.IsNullOrEmpty(r.Id) || !known.Add(NameKey(r.Artist, r.Title))) continue;
            var title = (r.Title ?? "").Trim();
            var artist = (r.Artist ?? "").Trim();
            result.Add(new RouletteAlbum(
                ServerKeyPrefix + r.Id, title, artist, Math.Max(0, r.Year), (r.Genre ?? "").Trim(), "Album",
                Array.Empty<string>(), "", r.ArtworkPath, Math.Max(0, r.PlayCount ?? 0), r.LastPlayed,
                artist.Length > 0 ? new[] { artist } : Array.Empty<string>())
            {
                ServerAlbumId = r.Id,
                PlaysKnown = r.PlayCount is not null || r.LastPlayed is not null,
                ServerName = r.ServerName ?? "",
                Language = ScriptLanguage.Detect(title, title, artist),
            });
            added = true;
        }
        if (added) result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    private static string NameKey(string? artist, string? title)
        => (artist ?? "").Trim().ToLowerInvariant() + "|" + (title ?? "").Trim().ToLowerInvariant();

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
                artists)
            {
                // The language most of its songs are in (one Korean song does not make a Korean album).
                Language = Most(ordered.Select(t => ScriptLanguage.Detect(t.Title, t.Album, t.Artist))),
            });
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
