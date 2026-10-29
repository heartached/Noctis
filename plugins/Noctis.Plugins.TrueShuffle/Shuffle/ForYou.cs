namespace Noctis.Plugins.TrueShuffle.Shuffle;

/// <summary>What the library says about a song (from the plugin API's track copy).</summary>
public sealed record TrackFacts(string Id, string Title, string Artist, string AlbumArtist, string[] Genres, int Year,
    int Bpm, string Key, int PlayCount, int Rating, bool Favorite, bool Disliked, DateTime? LastPlayedUtc)
{
    public static TrackFacts From(TrackInfo t) => new(t.Id, t.Title, t.Artist, t.AlbumArtist, SplitGenres(t.Genre), t.Year,
        t.Bpm, t.MusicalKey, t.PlayCount, t.Rating, t.IsFavorite, t.IsDisliked, t.LastPlayed?.UtcDateTime);

    /// <summary>Genres as Noctis writes them ("Rap; Trap"), split and lower-cased.</summary>
    public static string[] SplitGenres(string? genre) => string.IsNullOrWhiteSpace(genre) ? Array.Empty<string>()
        : genre.Split(new[] { ';', '/', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.ToLowerInvariant()).Where(g => g.Length > 0).Distinct().ToArray();

    /// <summary>The first credited artist, lower-cased: "A feat. B" and "A; B" are artist A here.</summary>
    public string MainArtist => _main ??= MainOf(string.IsNullOrWhiteSpace(Artist) ? AlbumArtist : Artist);
    private string? _main;

    public static string MainOf(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var a = artist.ToLowerInvariant();
        foreach (var cut in Cuts)
        {
            int i = a.IndexOf(cut, StringComparison.Ordinal);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    private static readonly string[] Cuts = { " feat.", " feat ", " ft.", " ft ", " featuring ", ";", ",", " & ", " x ", " with " };
}

/// <summary>
/// The listener's taste (ported from True Shuffle's TasteModel), worked out only from this computer:
/// the plugin's own listening log, Noctis's play counts, favorites, ratings, "not liked" marks and the
/// user's playlists. Bounded and decaying: an old phase fades, one favorite cannot take over, and a song
/// never played is unknown (zero), never disliked. No network, no catalogue.
/// </summary>
public sealed class TasteModel
{
    public static double Weight(PlayRecord p) => p.Outcome switch
    {
        PlayOutcome.Completed => 1.5,
        PlayOutcome.Qualified => 1.0,
        PlayOutcome.Partial => 0.3,
        PlayOutcome.StrongSkip => -2.5,
        PlayOutcome.QuickSkip => -0.5,
        _ => 0,
    };

    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(30), SkipHalfLife = TimeSpan.FromDays(14);
    private readonly Dictionary<string, double> _track = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _artist = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _genre = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastHeard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _plays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _skips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _inPlaylists = new(StringComparer.Ordinal);
    private readonly List<HashSet<string>> _playlists = new();
    private readonly Dictionary<string, TrackFacts> _facts = new(StringComparer.Ordinal);

    public DateTime Now { get; }
    public int KnownPlays { get; private set; }
    private TasteModel(DateTime now) => Now = now;

    public static TasteModel Build(IEnumerable<PlayRecord> plays, IReadOnlyList<TrackFacts> library,
        IEnumerable<IReadOnlyCollection<string>> playlists, DateTime nowUtc)
    {
        var model = new TasteModel(nowUtc);
        foreach (var f in library) model._facts.TryAdd(f.Id, f);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in plays)
        {
            var id = p.TrackId;
            if (!model._facts.ContainsKey(id)) continue;
            model.KnownPlays++;
            var age = nowUtc - p.StartUtc;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            double w = Weight(p);
            raw[id] = raw.GetValueOrDefault(id) + w * Math.Pow(0.5, age / HalfLife);
            if (w < -1) model._skips[id] = model._skips.GetValueOrDefault(id) + Math.Pow(0.5, age / SkipHalfLife);
            if (w > 0 || p.Outcome is not (PlayOutcome.QuickSkip or PlayOutcome.StrongSkip))
            {
                if (!model._lastHeard.TryGetValue(id, out var seen) || seen < p.StartUtc) model._lastHeard[id] = p.StartUtc;
            }
            model._plays[id] = model._plays.GetValueOrDefault(id) + 1;
        }
        // Noctis's own record of when a song last played counts too (before the log began).
        foreach (var f in library)
            if (f.LastPlayedUtc is { } lp && (!model._lastHeard.TryGetValue(f.Id, out var heard) || heard < lp))
                model._lastHeard[f.Id] = lp;
        int index = 0;
        foreach (var list in playlists)
        {
            var set = list.Where(model._facts.ContainsKey).ToHashSet(StringComparer.Ordinal);
            if (set.Count == 0) continue;
            model._playlists.Add(set);
            foreach (var id in set)
            {
                if (!model._inPlaylists.TryGetValue(id, out var lists)) model._inPlaylists[id] = lists = new List<int>();
                lists.Add(index);
            }
            index++;
        }
        foreach (var f in library)
        {
            double sum = raw.GetValueOrDefault(f.Id);
            if (f.Favorite) sum += 3;
            if (f.Rating >= 5) sum += 2.5; else if (f.Rating == 4) sum += 2; else if (f.Rating is > 0 and <= 2) sum -= 1.5;
            if (f.Disliked) sum -= 3;
            // In the listener's own playlists: chosen deliberately, up to two playlists' worth.
            sum += Math.Min(2, model._inPlaylists.GetValueOrDefault(f.Id)?.Count ?? 0);
            // Noctis's play count from before the log, lightly: a count is not a completed listen.
            if (!raw.ContainsKey(f.Id) && f.PlayCount > 0) sum += Math.Min(1.0, Math.Log(1 + f.PlayCount) / 3);
            if (sum != 0) model._track[f.Id] = Math.Tanh(sum / 4);
        }
        void Pool(Dictionary<string, double> into, Func<TrackFacts, IEnumerable<string>> keys)
        {
            var sums = new Dictionary<string, (double Sum, int Count)>(StringComparer.Ordinal);
            foreach (var f in library)
            {
                if (!model._track.TryGetValue(f.Id, out var t)) continue;
                foreach (var k in keys(f)) { var s = sums.GetValueOrDefault(k); sums[k] = (s.Sum + t, s.Count + 1); }
            }
            foreach (var (k, s) in sums) into[k] = s.Sum / (s.Count + 2);
        }
        Pool(model._artist, f => f.MainArtist.Length > 0 ? new[] { f.MainArtist } : Array.Empty<string>());
        Pool(model._genre, f => f.Genres);
        return model;
    }

    public TrackFacts? Facts(string id) => _facts.GetValueOrDefault(id);
    public double Track(string id) => _track.GetValueOrDefault(id);
    public double Artist(string mainArtist) => _artist.GetValueOrDefault(mainArtist);
    public double Genre(string[] genres)
    {
        if (genres.Length == 0) return 0;
        double best = 0, sum = 0;
        foreach (var g in genres) { var v = _genre.GetValueOrDefault(g); sum += v; best = Math.Max(best, v); }
        return (sum / genres.Length + best) / 2;
    }
    public DateTime? LastHeard(string id) => _lastHeard.TryGetValue(id, out var t) ? t : null;
    public int Plays(string id) => _plays.GetValueOrDefault(id);
    public double RecentSkips(string id) => _skips.GetValueOrDefault(id);
    /// <summary>Known to the listener: played, favorited, rated or in one of their playlists.</summary>
    public bool Familiar(string id) => _plays.ContainsKey(id) || _track.ContainsKey(id);

    /// <summary>The share of <paramref name="context"/> that sits in the same playlists as <paramref name="id"/>.</summary>
    public double PlaylistAffinity(string id, IReadOnlyCollection<string> context)
    {
        if (context.Count == 0 || !_inPlaylists.TryGetValue(id, out var lists)) return 0;
        double best = 0;
        foreach (var i in lists)
        {
            var set = _playlists[i];
            int shared = 0;
            foreach (var c in context) if (set.Contains(c)) shared++;
            best = Math.Max(best, (double)shared / context.Count);
        }
        return best;
    }

    /// <summary>How alike two songs are, from their tags (1 = alike in every way that is known).</summary>
    public static double Similarity(TrackFacts a, TrackFacts b)
    {
        double score = 0, weight = 0;
        if (a.Genres.Length > 0 && b.Genres.Length > 0)
        {
            int shared = a.Genres.Count(b.Genres.Contains);
            score += 0.35 * shared / Math.Min(a.Genres.Length, b.Genres.Length); weight += 0.35;
        }
        if (a.MainArtist.Length > 0 && b.MainArtist.Length > 0)
        {
            score += a.MainArtist == b.MainArtist ? 0.25
                : a.AlbumArtist.Length > 0 && TrackFacts.MainOf(a.AlbumArtist) == TrackFacts.MainOf(b.AlbumArtist) ? 0.12 : 0;
            weight += 0.25;
        }
        if (a.Year > 0 && b.Year > 0)
        {
            int gap = Math.Abs(a.Year - b.Year);
            score += 0.1 * (gap <= 2 ? 1 : gap <= 5 ? 0.6 : gap <= 10 ? 0.25 : 0); weight += 0.1;
        }
        if (a.Bpm > 0 && b.Bpm > 0)
        {
            // Half and double time feel alike.
            double ratio = (double)Math.Max(a.Bpm, b.Bpm) / Math.Min(a.Bpm, b.Bpm);
            double off = Math.Min(Math.Abs(ratio - 1), Math.Abs(ratio - 2));
            score += 0.15 * Math.Max(0, 1 - off / 0.08); weight += 0.15;
        }
        if (a.Key.Length > 0 && b.Key.Length > 0)
        {
            score += 0.1 * (a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) ? 1 : CamelotNeighbours(a.Key, b.Key) ? 0.6 : 0); weight += 0.1;
        }
        return weight == 0 ? 0 : score / weight;
    }

    private static bool CamelotNeighbours(string a, string b)
    {
        static (int Number, char Letter)? Parse(string k)
        {
            k = k.Trim().ToUpperInvariant();
            if (k.Length < 2 || (k[^1] != 'A' && k[^1] != 'B') || !int.TryParse(k[..^1], out int n) || n is < 1 or > 12) return null;
            return (n, k[^1]);
        }
        if (Parse(a) is not { } x || Parse(b) is not { } y) return false;
        if (x.Number == y.Number) return true; // relative major/minor
        int d = Math.Abs(x.Number - y.Number);
        return x.Letter == y.Letter && (d == 1 || d == 11);
    }
}

/// <summary>What a "For you" plan is made for.</summary>
/// <param name="Recent">Songs of this listening session, newest first; the playing one first.</param>
/// <param name="Skipped">Songs skipped early in this session: songs like them are held back for a while.</param>
/// <param name="Exploration">Share of places for songs the listener has not heard yet (0.1, 0.2 or 0.35).</param>
/// <param name="Count">How many songs to plan.</param>
public sealed record ForYouContext(IReadOnlyList<string> Recent, IReadOnlyCollection<string> Skipped, double Exploration, int Count, int Seed, DateTime NowUtc)
{
    public static double ExplorationFor(string? setting) => (setting ?? "").Trim().ToLowerInvariant() switch
    {
        "familiar" => 0.1,
        "adventurous" => 0.35,
        _ => 0.2,
    };
}

/// <summary>
/// Orders songs "for you" (ported from True Shuffle's ForYouPlanner): a sequence, not a sorted list.
/// Each place goes to the best remaining song by taste, fit with what is playing, the listener's
/// playlists and rediscovery, minus fatigue; some places go to songs not heard yet that fit; the same
/// artist does not come twice in a row or more than three times in twenty. Deterministic for a seed.
/// Only the user's own library and play history are used.
/// </summary>
public static class ForYouPlanner
{
    /// <summary>Songs heard this recently are left out while other songs are available.</summary>
    public static readonly TimeSpan RestPeriod = TimeSpan.FromHours(12);

    public static List<string> Plan(IReadOnlyList<string> candidates, TasteModel model, ForYouContext ctx)
    {
        var random = new Random(ctx.Seed);
        var known = candidates.Distinct(StringComparer.Ordinal).Where(id => model.Facts(id) != null).ToList();
        var sitting = new HashSet<string>(ctx.Recent, StringComparer.Ordinal);
        var pool = known.Where(id => !sitting.Contains(id)).ToList();
        if (pool.Count < ctx.Count)
        {
            // A short list played through in this sitting: the songs heard longest ago come back.
            var latest = ctx.Recent.Take(Math.Max(1, known.Count / 2)).ToHashSet(StringComparer.Ordinal);
            if (ctx.Recent.Count > 0) latest.Add(ctx.Recent[0]);
            pool = known.Where(id => !latest.Contains(id)).ToList();
        }
        if (pool.Count == 0) return new List<string>();
        var rested = pool.Where(id => model.LastHeard(id) is not { } t || ctx.NowUtc - t >= RestPeriod).ToList();
        if (rested.Count >= Math.Min(pool.Count, Math.Max(ctx.Count, 30))) pool = rested;

        var context = ctx.Recent.Select(model.Facts).OfType<TrackFacts>().Take(5).ToList();
        var skipped = ctx.Skipped.Select(model.Facts).OfType<TrackFacts>().ToList();
        var contextIds = ctx.Recent.Take(10).ToList();

        // Each part of the score becomes a rank percentile over the pool, so no part dominates by its scale.
        int n = pool.Count;
        var parts = new double[7][];
        for (int p = 0; p < parts.Length; p++) parts[p] = new double[n];
        for (int i = 0; i < n; i++)
        {
            var f = model.Facts(pool[i])!;
            parts[0][i] = model.Track(f.Id);
            parts[1][i] = model.Artist(f.MainArtist);
            parts[2][i] = model.Genre(f.Genres);
            double fit = 0, weight = 0, w = 1;
            foreach (var c in context) { fit += w * TasteModel.Similarity(f, c); weight += w; w *= 0.6; }
            parts[3][i] = weight == 0 ? 0 : fit / weight;
            parts[4][i] = model.PlaylistAffinity(f.Id, contextIds);
            // Rediscovery: liked, and not heard for three weeks or more.
            parts[5][i] = model.Track(f.Id) > 0.15 && model.LastHeard(f.Id) is { } heard && ctx.NowUtc - heard > TimeSpan.FromDays(21) ? model.Track(f.Id) : 0;
            // Fatigue and this session's skips (higher is worse).
            double tired = model.LastHeard(f.Id) is { } last ? Math.Pow(0.5, Math.Max(0, (ctx.NowUtc - last).TotalDays) / 1.5) : 0;
            double likeSkipped = 0;
            foreach (var s in skipped) likeSkipped = Math.Max(likeSkipped, TasteModel.Similarity(f, s));
            parts[6][i] = tired + Math.Min(1, model.RecentSkips(f.Id)) + 0.8 * likeSkipped;
        }
        var weights = new[] { 0.30, 0.12, 0.08, 0.20, 0.15, 0.05 };
        if (context.Count == 0) { weights[3] = 0; weights[4] = 0; } // nothing playing yet: lean on taste
        var ranked = parts.Select(Percentiles).ToArray();
        var relevance = new double[n];
        double total = weights.Sum();
        for (int i = 0; i < n; i++)
        {
            double r = 0;
            for (int p = 0; p < weights.Length; p++) r += weights[p] * ranked[p][i];
            r = r / total - 0.25 * (parts[6][i] > 0 ? ranked[6][i] : 0);
            relevance[i] = r + random.NextDouble() * 0.03; // a little variety between plans
        }

        var familiar = Enumerable.Range(0, n).Where(i => model.Familiar(pool[i])).OrderByDescending(i => relevance[i]).ToList();
        var fresh = Enumerable.Range(0, n).Where(i => !model.Familiar(pool[i])).OrderByDescending(i => relevance[i]).ToList();
        var result = new List<string>(Math.Min(ctx.Count, n));
        var artists = new List<string>();
        TrackFacts? previous = context.FirstOrDefault();
        bool Allowed(string artist, bool strict)
        {
            if (artist.Length == 0) return true;
            int recent = 0, inTwenty = 0;
            for (int k = artists.Count - 1, seen = 0; k >= 0 && seen < 19; k--, seen++)
            {
                if (artists[k] != artist) continue;
                if (seen < 2) recent++; // not in the last two picks
                inTwenty++;
            }
            return strict ? recent == 0 && inTwenty < 3 : recent == 0 || artists.Count == 0;
        }
        while (result.Count < ctx.Count && (familiar.Count > 0 || fresh.Count > 0))
        {
            bool explore = fresh.Count > 0 && (familiar.Count == 0 || random.NextDouble() < ctx.Exploration);
            var list = explore ? fresh : familiar;
            int pick = -1;
            // The original fell back to the top of the chosen list as soon as that list had no song by
            // another artist, so an artist could come twice in a row although the other list had
            // songs by other artists. The other list is tried first now.
            foreach (var choice in new[] { list, explore ? familiar : fresh })
            {
                foreach (bool strict in new[] { true, false })
                {
                    double best = double.NegativeInfinity;
                    // The best few by relevance, with a small bonus for flowing on from the previous pick.
                    foreach (var i in choice.Take(40))
                    {
                        var f = model.Facts(pool[i])!;
                        if (!Allowed(f.MainArtist, strict)) continue;
                        double score = relevance[i] + (previous == null ? 0 : 0.08 * TasteModel.Similarity(f, previous));
                        if (score > best) { best = score; pick = i; }
                    }
                    if (pick >= 0) break;
                }
                if (pick >= 0)
                {
                    list = choice;
                    break;
                }
            }
            if (pick < 0) pick = list[0]; // only one artist left: a short library
            list.Remove(pick);
            var chosen = model.Facts(pool[pick])!;
            result.Add(chosen.Id);
            artists.Add(chosen.MainArtist);
            previous = chosen;
        }
        return result;
    }

    /// <summary>Values as their rank percentile (0..1); ties share a rank; constant lists give 0.</summary>
    public static double[] Percentiles(double[] values)
    {
        int n = values.Length;
        var result = new double[n];
        if (n < 2) return result;
        var order = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
        if (values[order[0]] == values[order[^1]]) return result;
        for (int k = 0; k < n;)
        {
            int j = k;
            while (j + 1 < n && values[order[j + 1]] == values[order[k]]) j++;
            double rank = (k + j) / 2.0 / (n - 1);
            for (int m = k; m <= j; m++) result[order[m]] = rank;
            k = j + 1;
        }
        return result;
    }
}
