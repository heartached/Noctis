using System.Globalization;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>"Never played" or "Not played in 6 months" (one at a time: never played is a part of the other).</summary>
public enum PlayedFilter { Any, NeverPlayed, NotIn6Months }

/// <summary>
/// The chips above the reel. Empty chips match everything; kinds combine with "or" (Albums and
/// EPs), the rest with "and". Compilations are left out by the setting, not by a chip.
/// </summary>
public sealed class RouletteFilter
{
    /// <summary>Genre to match (case-insensitive, any one of the album's or song's genres), or null for any.</summary>
    public string? Genre { get; set; }

    /// <summary>Language (<see cref="RouletteAlbum.Language"/>, case-insensitive), or null for any.</summary>
    public string? Language { get; set; }

    /// <summary>Decade (1990 for the 1990s), or null for any.</summary>
    public int? Decade { get; set; }

    public bool Albums { get; set; }
    public bool EPs { get; set; }
    public bool Singles { get; set; }

    public PlayedFilter Played { get; set; }

    /// <summary>"Spin from this artist": the album's artist or one of its track artists, or null.</summary>
    public string? Artist { get; set; }

    public bool SkipCompilations { get; set; }

    /// <summary>True when any chip is on (the compilations setting is not a chip).</summary>
    public bool IsActive => Genre is not null || Language is not null || Decade is not null || Albums || EPs || Singles
                            || Played != PlayedFilter.Any || Artist is not null;

    /// <summary>Clears every chip (the compilations setting stays).</summary>
    public void Clear()
    {
        Genre = null;
        Language = null;
        Decade = null;
        Albums = EPs = Singles = false;
        Played = PlayedFilter.Any;
        Artist = null;
    }

    public bool Matches(RouletteAlbum album, DateTimeOffset now)
    {
        if (SkipCompilations && album.IsCompilation) return false;
        if (Genre is { } g && !album.Genres.Contains(g, StringComparer.OrdinalIgnoreCase)) return false;
        if (Language is { } l && !string.Equals(album.Language, l, StringComparison.OrdinalIgnoreCase)) return false;
        if (Decade is { } d && album.Decade != d) return false;
        // Albums / EPs / Singles sort albums; in Song Roulette every song is its own pick.
        if (!album.IsSong && (Albums || EPs || Singles))
        {
            var ok = album.Kind switch
            {
                ReleaseKind.Single => Singles,
                ReleaseKind.EP => EPs,
                _ => Albums,
            };
            if (!ok) return false;
        }
        switch (Played)
        {
            case PlayedFilter.NeverPlayed when !album.NeverPlayed:
                return false;
            case PlayedFilter.NotIn6Months when album.LastPlayed is { } last && last > now.AddMonths(-6):
                return false;
        }
        if (Artist is { } a && !album.Artists.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    public List<RouletteAlbum> Apply(IReadOnlyList<RouletteAlbum> albums, DateTimeOffset now)
    {
        var list = new List<RouletteAlbum>();
        foreach (var a in albums)
            if (Matches(a, now)) list.Add(a);
        return list;
    }

    /// <summary>The genres for the Genre chip, one per single genre ("Rock; Indie" gives both):
    /// most albums (or songs) first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Genres(IReadOnlyList<RouletteAlbum> albums, int max = 24)
        => albums.SelectMany(a => a.Genres)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(g => g.First()).ToList();

    /// <summary>The languages for the Language chip: most albums (or songs) first, Latin script
    /// last (it says the least).</summary>
    public static IReadOnlyList<string> Languages(IReadOnlyList<RouletteAlbum> albums)
        => albums.Where(a => a.Language is not null)
            .GroupBy(a => a.Language!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == ScriptLanguage.Latin).ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).ToList();

    /// <summary>The decades for the Decade chip, newest first.</summary>
    public static IReadOnlyList<int> Decades(IReadOnlyList<RouletteAlbum> albums)
        => albums.Where(a => a.Decade > 0).Select(a => a.Decade).Distinct().OrderByDescending(d => d).ToList();

    /// <summary>"1990s".</summary>
    public static string DecadeName(int decade) => decade.ToString(CultureInfo.InvariantCulture) + "s";
}
