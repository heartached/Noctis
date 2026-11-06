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

        // Every song under a title+artist key, in library order: the first is the match unless
        // the entry's length (an m3u's #EXTINF) picks a closer one.
        var exact = new Dictionary<string, List<Track>>();
        foreach (var n in norm)
        {
            var key = n.Title + "" + MatchText.Fold(n.Track.PrimaryArtist);
            if (exact.TryGetValue(key, out var same)) same.Add(n.Track);
            else exact[key] = new List<Track> { n.Track };
        }

        // Path/filename indexes, built only when some entry carries a file path (m3u).
        Dictionary<string, Track>? byPath = null;
        Dictionary<string, List<(Track Track, string[] Tail)>>? byFileName = null;
        if (entries.Any(e => e.FilePath.Length > 0))
        {
            byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            byFileName = new Dictionary<string, List<(Track, string[])>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in library)
            {
                if (string.IsNullOrWhiteSpace(t.FilePath)) continue;
                var p = NormalizePath(t.FilePath);
                if (!byPath.ContainsKey(p)) byPath[p] = t;
                var tail = PathTail(t.FilePath);
                if (tail.Length == 0) continue;
                var name = tail[^1];
                if (byFileName.TryGetValue(name, out var named)) named.Add((t, tail));
                else byFileName[name] = new List<(Track, string[])> { (t, tail) };
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
                if (ByPathTail(PathTail(e.FilePath), byFileName!) is { } tailHit)
                {
                    results.Add(new MatchResult(e, tailHit.Track, tailHit.Score));
                    continue;
                }
            }

            var entry = Index(null!, e.Title, e.Artist);
            if (entry.Title.Length == 0) { results.Add(new MatchResult(e, null, 0)); continue; }

            if (exact.TryGetValue(entry.Title + "" + entry.Artist, out var hits)
                || (entry.Artists.Count > 0 && exact.TryGetValue(entry.Title + "" + entry.Artists[0], out hits)))
            {
                results.Add(new MatchResult(e, Closest(hits, e.Duration), 1.0));
                continue;
            }

            Track? best = null;
            double bestScore = 0;
            var candidates = new List<MatchCandidate>(MaxCandidates + 1);
            foreach (var n in norm)
            {
                var score = Score(entry, n);
                if (score <= 0) continue;
                // A length that is far off (an m3u's #EXTINF) makes another recording likelier.
                if (e.Duration is { } length && n.Track.Duration > TimeSpan.Zero
                    && Math.Abs((length - n.Track.Duration).TotalSeconds) > FarDurationSeconds)
                    score *= FarDurationFactor;
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

    // An #EXTINF length this many seconds off a candidate's costs it this share of its score.
    private const double FarDurationSeconds = 20;
    private const double FarDurationFactor = 0.85;

    /// <summary>Of songs that match equally by name, the one closest to <paramref name="length"/>
    /// (the first when the entry has no length).</summary>
    private static Track Closest(List<Track> tracks, TimeSpan? length)
    {
        if (tracks.Count == 1 || length is not { } want) return tracks[0];
        var best = tracks[0];
        var bestGap = double.MaxValue;
        foreach (var t in tracks)
        {
            if (t.Duration <= TimeSpan.Zero) continue;
            var gap = Math.Abs((t.Duration - want).TotalSeconds);
            if (gap < bestGap) { bestGap = gap; best = t; }
        }
        return best;
    }

    /// <summary>
    /// The library song an entry's path points at when the folders differ (a playlist written
    /// on a computer, imported on a phone): the one song with that file name, else the one whose
    /// folders agree with the entry's furthest up from the file (at least the file's own folder,
    /// and no tie). Null when the name is not there or stays ambiguous.
    /// </summary>
    private static (Track Track, double Score)? ByPathTail(string[] tail, Dictionary<string, List<(Track Track, string[] Tail)>> byFileName)
    {
        if (tail.Length == 0 || !byFileName.TryGetValue(tail[^1], out var named)) return null;
        if (named.Count == 1) return (named[0].Track, 0.95);
        Track? best = null;
        var bestShared = 0;
        var tie = false;
        foreach (var (track, other) in named)
        {
            var shared = SharedTail(tail, other);
            if (shared > bestShared) { bestShared = shared; best = track; tie = false; }
            else if (shared == bestShared) tie = true;
        }
        return best != null && bestShared >= 2 && !tie ? (best, 0.9) : null;
    }

    private static int SharedTail(string[] a, string[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length
               && string.Equals(a[a.Length - 1 - n], b[b.Length - 1 - n], StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }

    /// <summary>
    /// A path's folders and file name, in order, whatever wrote it: Windows ("C:\Music\a.mp3")
    /// or Unix paths, relative ones ("../a.mp3", the dots dropped), file:// and percent-encoded
    /// entries, and Android document URIs ("content://…/document/primary%3AMusic%2Fa.mp3" →
    /// Music, a.mp3: the storage volume before the colon is not a folder).
    /// </summary>
    internal static string[] PathTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        var p = path.Trim();
        if (p.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var doc = p.LastIndexOf("/document/", StringComparison.Ordinal);
            var tree = p.IndexOf("/tree/", StringComparison.Ordinal);
            var at = doc >= 0 ? doc + "/document/".Length : tree >= 0 ? tree + "/tree/".Length : -1;
            if (at < 0) return Array.Empty<string>();
            p = SafeUnescape(p[at..]);
            var colon = p.IndexOf(':');
            if (colon >= 0) p = p[(colon + 1)..];
        }
        else
        {
            p = PlaylistImportParser.DecodeM3uEntry(p);
        }
        p = p.Replace('\\', '/');
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') p = p[2..];
        return p.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s != "." && s != "..")
            .ToArray();
    }

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

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

    internal static int Levenshtein(string a, string b) => MatchText.Levenshtein(a, b);
}
