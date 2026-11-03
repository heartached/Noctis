namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>Why a resident is in the plaza (shown on its card).</summary>
public enum CastReason { Called, NowPlaying, Recent, MostPlayed, Random }

public sealed record CastMember(TownAlbum Album, CastReason Reason);

/// <summary>
/// Picks who comes to the plaza. Never the whole town (a 2000-song library would be hundreds of
/// creatures): albums the user called, the one playing, then a third recently played, a third
/// most played, and random neighbours for the rest, each album once.
/// </summary>
public static class PlazaCast
{
    public const int MinCrowd = 1;
    public const int MaxCrowd = 60;
    public const int DefaultCrowd = 40;

    public static IReadOnlyList<CastMember> Choose(
        IReadOnlyList<TownAlbum> town,
        int crowd,
        Random random,
        string? nowPlayingKey = null,
        IReadOnlyCollection<string>? called = null)
    {
        crowd = Math.Clamp(crowd, MinCrowd, MaxCrowd);
        var picked = new List<CastMember>(crowd);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, TownAlbum>(town.Count, StringComparer.Ordinal);
        foreach (var a in town) byKey.TryAdd(a.Key, a);

        bool Add(TownAlbum a, CastReason why)
        {
            if (picked.Count >= crowd || !seen.Add(a.Key)) return false;
            picked.Add(new CastMember(a, why));
            return true;
        }

        if (called is not null)
            foreach (var key in called)
                if (byKey.TryGetValue(key, out var a)) Add(a, CastReason.Called);
        if (nowPlayingKey is not null && byKey.TryGetValue(nowPlayingKey, out var playing)) Add(playing, CastReason.NowPlaying);

        var third = Math.Max(1, crowd / 3);
        var recentTarget = picked.Count + third;
        foreach (var a in town.Where(a => a.LastPlayed is not null).OrderByDescending(a => a.LastPlayed).ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            if (picked.Count >= recentTarget) break;
            Add(a, CastReason.Recent);
        }

        var mostTarget = picked.Count + third;
        foreach (var a in town.Where(a => a.PlayCount > 0).OrderByDescending(a => a.PlayCount).ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            if (picked.Count >= mostTarget) break;
            Add(a, CastReason.MostPlayed);
        }

        // Random neighbours: a partial Fisher-Yates over what is left.
        var rest = town.Where(a => !seen.Contains(a.Key)).ToArray();
        for (var i = 0; i < rest.Length && picked.Count < crowd; i++)
        {
            var j = random.Next(i, rest.Length);
            (rest[i], rest[j]) = (rest[j], rest[i]);
            Add(rest[i], CastReason.Random);
        }
        return picked;
    }

    /// <summary>
    /// "New crowd": the albums the user called and the one playing stay, everyone else is picked
    /// at random, newcomers before anyone from <paramref name="previous"/> (the crowd being sent
    /// home). <see cref="Choose"/> fills two thirds with the recent and most played, the same
    /// albums every time, so New crowd kept them and played the same songs again (Discord,
    /// Mistery 11-02).
    /// </summary>
    public static IReadOnlyList<CastMember> ChooseFresh(
        IReadOnlyList<TownAlbum> town,
        int crowd,
        Random random,
        IReadOnlyCollection<string> previous,
        string? nowPlayingKey = null,
        IReadOnlyCollection<string>? called = null)
    {
        crowd = Math.Clamp(crowd, MinCrowd, MaxCrowd);
        var picked = new List<CastMember>(crowd);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, TownAlbum>(town.Count, StringComparer.Ordinal);
        foreach (var a in town) byKey.TryAdd(a.Key, a);
        void Add(TownAlbum a, CastReason why)
        {
            if (picked.Count < crowd && seen.Add(a.Key)) picked.Add(new CastMember(a, why));
        }
        if (called is not null)
            foreach (var key in called)
                if (byKey.TryGetValue(key, out var a)) Add(a, CastReason.Called);
        if (nowPlayingKey is not null && byKey.TryGetValue(nowPlayingKey, out var playing)) Add(playing, CastReason.NowPlaying);

        var before = new HashSet<string>(previous, StringComparer.Ordinal);
        foreach (var pool in new[] { town.Where(a => !before.Contains(a.Key)), town.Where(a => before.Contains(a.Key)) })
        {
            var rest = pool.Where(a => !seen.Contains(a.Key)).ToArray();
            for (var i = 0; i < rest.Length && picked.Count < crowd; i++)
            {
                var j = random.Next(i, rest.Length);
                (rest[i], rest[j]) = (rest[j], rest[i]);
                Add(rest[i], CastReason.Random);
            }
        }
        return picked;
    }

    /// <summary>Residents whose title or artist contain every word of <paramref name="query"/>,
    /// best first (title starts with it, then most played).</summary>
    public static IReadOnlyList<TownAlbum> Find(IReadOnlyList<TownAlbum> town, string query, int limit = 8)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Array.Empty<TownAlbum>();
        return town
            .Where(a => words.All(w => a.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Artist.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.Title.StartsWith(query!.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.PlayCount)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}
