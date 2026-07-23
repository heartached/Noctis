using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>The outcome of matching one import entry against the library.</summary>
public sealed record MatchResult(PlaylistImportEntry Entry, Track? Match, double Score);

/// <summary>
/// Pure fuzzy matching of imported entries to library tracks. Path-carrying entries
/// (m3u) try an exact library-path hit, then a unique-filename hit — this is what makes
/// playlists written on another machine (foreign absolute paths) resolve. Exact
/// normalized title+artist hits short-circuit next; otherwise a Levenshtein-ratio score
/// (title weighted over artist) is taken against the best candidate and accepted above
/// a threshold.
/// </summary>
public static class FuzzyTrackMatcher
{
    public const double DefaultThreshold = 0.80;

    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<PlaylistImportEntry> entries,
        IReadOnlyList<Track> library,
        double threshold = DefaultThreshold)
    {
        var norm = library
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => (track: t, title: Normalize(t.Title), artist: Normalize(t.PrimaryArtist)))
            .ToList();

        var exact = new Dictionary<string, Track>();
        foreach (var n in norm)
        {
            var key = n.title + "" + n.artist;
            if (!exact.ContainsKey(key)) exact[key] = n.track;
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

            var et = Normalize(e.Title);
            var ea = Normalize(e.Artist);
            if (et.Length == 0) { results.Add(new MatchResult(e, null, 0)); continue; }

            if (exact.TryGetValue(et + "" + ea, out var hit))
            {
                results.Add(new MatchResult(e, hit, 1.0));
                continue;
            }

            Track? best = null;
            double bestScore = 0;
            foreach (var n in norm)
            {
                var titleSim = Ratio(et, n.title);
                if (titleSim < 0.5) continue; // cheap prune: titles must be in the ballpark
                var artistSim = ea.Length == 0 || n.artist.Length == 0 ? 0.5 : Ratio(ea, n.artist);
                var score = 0.65 * titleSim + 0.35 * artistSim;
                if (score > bestScore) { bestScore = score; best = n.track; }
            }

            results.Add(bestScore >= threshold
                ? new MatchResult(e, best, bestScore)
                : new MatchResult(e, null, bestScore));
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
        var dist = Levenshtein(a, b);
        return 1.0 - (double)dist / maxLen;
    }

    private static int Levenshtein(string a, string b)
    {
        // Length-based early-out: if lengths differ by more than half the longer length,
        // the ratio can't clear our prune threshold anyway.
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
