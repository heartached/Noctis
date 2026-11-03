using System.Text;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

/// <summary>The outcome of matching one import entry against the library.</summary>
public sealed record MatchResult(PlaylistImportEntry Entry, Track? Match, double Score)
{
    /// <summary>For an entry left unmatched: the closest library songs, best first (at most
    /// <see cref="FuzzyTrackMatcher.MaxCandidates"/>), for the import dialog's review step.</summary>
    public IReadOnlyList<MatchCandidate> Candidates { get; init; } = Array.Empty<MatchCandidate>();
}

/// <summary>A library song that may be the one an unmatched import entry meant.</summary>
public sealed record MatchCandidate(Track Track, double Score);

/// <summary>
/// Pure fuzzy matching of imported entries to library tracks. Path-carrying entries
/// (m3u) try an exact library-path hit, then a unique-filename hit — this is what makes
/// playlists written on another machine (foreign absolute paths) resolve. Exact
/// normalized title+artist hits short-circuit next; otherwise a Levenshtein-ratio score
/// (title weighted over artist) is taken against the best candidate and accepted above
/// a threshold.
/// <para>Titles are also compared on their base form (<see cref="MatchText.AnalyzeTitle"/>):
/// a feat. credit, a remaster or edition suffix, accents and "&amp;"/"and" no longer leave a
/// song "missing" (Discord, Cosmo 11-02), while a live take, remix or edit only counts as a
/// candidate for the studio song, never as its match.</para>
/// </summary>
public static class FuzzyTrackMatcher
{
    public const double DefaultThreshold = 0.80;

    /// <summary>Most candidates kept per unmatched entry.</summary>
    public const int MaxCandidates = 3;

    /// <summary>Lowest score a library song needs to be offered as a candidate.</summary>
    public const double CandidateFloor = 0.45;

    // A base-title hit whose version markers differ (live vs studio, remix vs original) counts
    // this much of its similarity: enough to be a candidate, never enough to match.
    private const double DistinctVersionFactor = 0.6;
    // A base-title hit with the same markers (only decoration differs) ranks just under an exact title.
    private const double BaseTitleFactor = 0.98;

    private sealed record Indexed(Track Track, string Title, string Base, IReadOnlySet<string> Markers,
        string Artist, IReadOnlyList<string> Artists);

    private static Indexed Index(Track t, string? title, string? artist)
    {
        var (b, markers) = MatchText.AnalyzeTitle(title);
        return new Indexed(t, MatchText.Fold(title), b, markers, MatchText.Fold(artist), MatchText.SplitArtists(artist));
    }

    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<PlaylistImportEntry> entries,
        IReadOnlyList<Track> library,
        double threshold = DefaultThreshold,
        CancellationToken ct = default)
    {
        var norm = library
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => Index(t, t.Title, t.Artist))
            .ToList();

        var exact = new Dictionary<string, Track>();
        foreach (var n in norm)
        {
            var key = n.Title + "" + MatchText.Fold(n.Track.PrimaryArtist);
            if (!exact.ContainsKey(key)) exact[key] = n.Track;
        }

        // Path/filename indexes, built only when some entry carries a file path (m3u).
        Dictionary<string, Track>? byPath = null;
        Dictionary<string, Track?>? byFileName = null; // null value = ambiguous name
        if (entries.Any(e => e.FilePath.Length > 0))
        {
            byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            byFileName = new Dictionary<string, Track?>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in library)
            {
                if (string.IsNullOrWhiteSpace(t.FilePath)) continue;
                var p = NormalizePath(t.FilePath);
                if (!byPath.ContainsKey(p)) byPath[p] = t;
                var name = Path.GetFileName(p);
                if (name.Length == 0) continue;
                byFileName[name] = byFileName.TryGetValue(name, out _) ? null : t;
            }
        }

        var results = new List<MatchResult>(entries.Count);
        foreach (var e in entries)
        {
            // Each unmatched entry costs a Levenshtein pass over the library; without
            // this check a closed import dialog kept burning CPU to completion.
            ct.ThrowIfCancellationRequested();

            if (e.FilePath.Length > 0 && byPath != null)
            {
                var p = NormalizePath(e.FilePath);
                if (byPath.TryGetValue(p, out var pathHit))
                {
                    results.Add(new MatchResult(e, pathHit, 1.0));
                    continue;
                }
                var name = Path.GetFileName(p);
                if (name.Length > 0 && byFileName!.TryGetValue(name, out var nameHit) && nameHit != null)
                {
                    results.Add(new MatchResult(e, nameHit, 0.95));
                    continue;
                }
            }

            var entry = Index(null!, e.Title, e.Artist);
            if (entry.Title.Length == 0) { results.Add(new MatchResult(e, null, 0)); continue; }

            if (exact.TryGetValue(entry.Title + "" + entry.Artist, out var hit)
                || (entry.Artists.Count > 0 && exact.TryGetValue(entry.Title + "" + entry.Artists[0], out hit)))
            {
                results.Add(new MatchResult(e, hit, 1.0));
                continue;
            }

            Track? best = null;
            double bestScore = 0;
            var candidates = new List<MatchCandidate>(MaxCandidates + 1);
            foreach (var n in norm)
            {
                var score = Score(entry, n);
                if (score <= 0) continue;
                if (score > bestScore) { bestScore = score; best = n.Track; }
                if (score >= CandidateFloor) Keep(candidates, new MatchCandidate(n.Track, score));
            }

            results.Add(bestScore >= threshold
                ? new MatchResult(e, best, bestScore)
                : new MatchResult(e, null, bestScore) { Candidates = candidates });
        }

        return results;
    }

    /// <summary>
    /// Similarity in [0,1] between two (title, artist) tag pairs — title weighted
    /// over artist, same normalization/weights as playlist matching. Used to score
    /// metadata-lookup suggestions against a track's current tags.
    /// </summary>
    public static double TagSimilarity(string? titleA, string? artistA, string? titleB, string? artistB)
    {
        var ta = Normalize(titleA);
        var tb = Normalize(titleB);
        if (ta.Length == 0 || tb.Length == 0) return 0;
        var aa = Normalize(artistA);
        var ab = Normalize(artistB);
        var titleSim = Ratio(ta, tb);
        var artistSim = aa.Length == 0 || ab.Length == 0 ? 0.5 : Ratio(aa, ab);
        return 0.65 * titleSim + 0.35 * artistSim;
    }

    /// <summary>
    /// Title weighted over artist. The title is the better of the whole folded titles and the
    /// base titles (decoration stripped); a base hit across different version markers is held
    /// under the match threshold. 0 when the titles are not in the same ballpark.
    /// </summary>
    private static double Score(Indexed entry, Indexed n)
    {
        var whole = Ratio(entry.Title, n.Title);
        var baseSim = entry.Base.Length > 0 && n.Base.Length > 0 ? Ratio(entry.Base, n.Base) : 0;
        baseSim *= entry.Markers.SetEquals(n.Markers) ? BaseTitleFactor : DistinctVersionFactor;
        // A raw title hit across version markers (one side's marker sits outside brackets) is no better.
        if (!entry.Markers.SetEquals(n.Markers)) whole *= DistinctVersionFactor;
        var titleSim = Math.Max(whole, baseSim);
        if (titleSim < 0.5) return 0; // cheap prune: titles must be in the ballpark
        return 0.65 * titleSim + 0.35 * ArtistSim(entry, n);
    }

    // The whole credit, or the best pairing of credited names ("Queen & David Bowie" has
    // David Bowie). Unknown on either side is neutral.
    private static double ArtistSim(Indexed a, Indexed b)
    {
        if (a.Artist.Length == 0 || b.Artist.Length == 0) return 0.5;
        var sim = Ratio(a.Artist, b.Artist);
        if (sim >= 1 || a.Artists.Count == 0 || b.Artists.Count == 0) return sim;
        sim = Math.Max(sim, Ratio(a.Artists[0], b.Artists[0]));
        foreach (var x in a.Artists)
            foreach (var y in b.Artists)
                sim = Math.Max(sim, Ratio(x, y) * 0.95);
        return sim;
    }

    private static void Keep(List<MatchCandidate> best, MatchCandidate c)
    {
        var at = best.FindIndex(x => c.Score > x.Score);
        if (at < 0) { if (best.Count < MaxCandidates) best.Add(c); return; }
        best.Insert(at, c);
        if (best.Count > MaxCandidates) best.RemoveAt(best.Count - 1);
    }

    /// <summary>
    /// Library songs for a typed search in the import review: every word of
    /// <paramref name="query"/> must appear in the song's title, artist or album (folded, so
    /// accents and case don't matter); title hits rank first, then shorter titles.
    /// </summary>
    public static IReadOnlyList<Track> Search(string? query, IReadOnlyList<Track> library, int max)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MatchText.Fold).Where(w => w.Length > 0).Distinct().ToArray();
        if (words.Length == 0 || max <= 0) return Array.Empty<Track>();

        var hits = new List<(Track Track, int TitleHits, int Length)>();
        foreach (var t in library)
        {
            var title = MatchText.Fold(t.Title);
            var rest = MatchText.Fold(t.Artist) + "" + MatchText.Fold(t.AlbumArtist) + "" + MatchText.Fold(t.Album);
            var titleHits = 0;
            var all = true;
            foreach (var w in words)
            {
                if (title.Contains(w, StringComparison.Ordinal)) titleHits++;
                else if (!rest.Contains(w, StringComparison.Ordinal)) { all = false; break; }
            }
            if (all) hits.Add((t, titleHits, title.Length));
        }
        return hits.OrderByDescending(h => h.TitleHits).ThenBy(h => h.Length)
            .Take(max).Select(h => h.Track).ToList();
    }

    // Forward slashes + trim so Windows- and Unix-written paths compare equal.
    private static string NormalizePath(string p) => p.Replace('\\', '/').Trim();

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Similarity in [0,1] derived from Levenshtein edit distance.</summary>
    private static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;
        // Length-based early-out: edit distance is at least the length gap, so if
        // lengths differ by more than half the longer length the ratio can't clear
        // our 0.5 prune threshold anyway — skip the O(a×b) pass and its allocations.
        if (Math.Abs(a.Length - b.Length) * 2 > maxLen) return 0;
        var dist = Levenshtein(a, b);
        return 1.0 - (double)dist / maxLen;
    }

    internal static int Levenshtein(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
