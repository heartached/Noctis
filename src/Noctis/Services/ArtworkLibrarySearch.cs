using System.Text.RegularExpressions;
using Noctis.Models;
using Candidate = Noctis.Services.ITunesArtworkService.ArtworkCandidate;

namespace Noctis.Services;

/// <summary>What a section of the Choose Artwork library holds.</summary>
public enum ArtworkSectionKind
{
    /// <summary>Releases whose title matches what was typed after the artist's name.</summary>
    Matches,
    Albums,
    SinglesAndEps,
    /// <summary>Other artists' releases the artist is credited on.</summary>
    AppearsOn,
    /// <summary>The rest of the artist's releases, under the matches.</summary>
    MoreByArtist,
    /// <summary>A plain search: no artist was recognised in the query.</summary>
    Results,
}

public sealed record ArtworkLibrarySection(ArtworkSectionKind Kind, IReadOnlyList<Candidate> Items);

/// <summary>
/// The Choose Artwork pop-up's answer: the artist the query named (when one was recognised)
/// and the releases to show, in sections, best first.
/// </summary>
public sealed record ArtworkLibraryResult(string? ArtistName, long ArtistId, IReadOnlyList<ArtworkLibrarySection> Sections)
{
    public static readonly ArtworkLibraryResult Empty = new(null, 0, Array.Empty<ArtworkLibrarySection>());

    /// <summary>The artist's own releases (not the Appears On ones).</summary>
    public int ReleaseCount => Sections
        .Where(s => s.Kind is ArtworkSectionKind.Albums or ArtworkSectionKind.SinglesAndEps
            or ArtworkSectionKind.Matches or ArtworkSectionKind.MoreByArtist)
        .Sum(s => s.Items.Count);

    public IEnumerable<Candidate> All => Sections.SelectMany(s => s.Items);
}

/// <summary>
/// Owner 11-26: the Choose Artwork pop-up as an artist's artwork library. Proven causes of the
/// report (requests and answers recorded in tests/Noctis.Tests/Fixtures/ArtworkLibrary):
/// <list type="bullet">
/// <item>"CHASE ATLANTIC" went to /search?entity=album&amp;attribute=albumTerm, whose index answers
/// 18 albums (7 by the band, the rest tributes and strangers), and the client ranked titles that
/// CONTAIN the query first ("Lullaby Versions of Chase Atlantic") before keeping 8.</item>
/// <item>"DON'T TRY THIS - EP" (1448869074) is in no /search?entity=album answer at all, for any
/// term or spelling ("DONT TRY THIS", "DON'T TRY THIS", with or without the artist): the index
/// has a hole there, while /lookup?id=879496517&amp;entity=album (the artist's discography) and
/// the Apple Music web search page both have it.</item>
/// </list>
/// So the artist is resolved first (/search?entity=musicArtist, plus the credits of the album
/// hits) and their whole discography read through /lookup (one request: the endpoint caps it at
/// 200 releases, newest first with sort=recent, and has no paging), and titles are matched on
/// normalised text. Pure parts here, for tests; the requests are in <see cref="ITunesArtworkService"/>.
/// </summary>
internal static class ArtworkLibrarySearch
{
    /// <summary>/lookup answers at most 200 rows (limit=300 also gives 200; offset is ignored).</summary>
    internal const int MaxReleases = 200;

    /// <summary>Releases shown for a plain search.</summary>
    internal const int MaxPlainResults = 48;

    private static readonly Regex SingleOrEpSuffix = new(
        @"\s+[-–—]\s+(?:single|ep)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Text for matching: case, accents and punctuation folded, apostrophes dropped ("DON'T" =
    /// "dont"), and the store's " - EP" / " - Single" suffix removed.
    /// </summary>
    internal static string Normalize(string? value)
        => AnimatedArtworkMatcher.Normalize(SingleOrEpSuffix.Replace(value ?? string.Empty, string.Empty));

    /// <summary>A store title ending in " - Single" or " - EP".</summary>
    internal static bool IsSingleOrEp(string? title) => SingleOrEpSuffix.IsMatch(title ?? string.Empty);

    /// <summary>The search terms for /search?entity=musicArtist: the query, and the tagged artist
    /// when the query names them (a tag-made query is "artist album").</summary>
    internal static IReadOnlyList<string> ArtistSearchTerms(string query, string? preferArtist)
    {
        var terms = new List<string>();
        var q = (query ?? string.Empty).Trim();
        if (q.Length > 0) terms.Add(q);
        var prefer = Track.GetPrimaryArtist(preferArtist).Trim();
        var p = Normalize(prefer);
        if (p.Length > 0 && $" {Normalize(q)} ".Contains($" {p} ", StringComparison.Ordinal) &&
            !string.Equals(prefer, q, StringComparison.OrdinalIgnoreCase))
            terms.Add(prefer);
        return terms;
    }

    /// <summary>The artist a query names and the words left over (the release title, or empty).</summary>
    internal sealed record ArtistMatch(string Name, long Id, string Rest);

    /// <summary>
    /// Which known artist the query names: the whole query (an artist-only search), else the
    /// longest name it starts or ends with ("chase atlantic dont try this"). Ties go to the
    /// tagged artist, then to the order the candidates came in (Apple's ranking).
    /// </summary>
    internal static ArtistMatch? ResolveArtist(string query, IEnumerable<(string Name, long Id)> candidates, string? preferArtist)
    {
        var q = Normalize(query);
        if (q.Length == 0) return null;
        var prefer = Normalize(Track.GetPrimaryArtist(preferArtist));

        ArtistMatch? best = null;
        var bestScore = 0;
        var order = 0;
        foreach (var (name, id) in candidates)
        {
            order++;
            var n = AnimatedArtworkMatcher.Normalize(name);
            if (id <= 0 || n.Length == 0) continue;

            string rest;
            int score;
            if (n == q) { rest = string.Empty; score = 100_000; }
            else if (q.StartsWith(n + " ", StringComparison.Ordinal)) { rest = q[(n.Length + 1)..]; score = 50_000; }
            else if (q.EndsWith(" " + n, StringComparison.Ordinal)) { rest = q[..^(n.Length + 1)]; score = 40_000; }
            else continue;

            score += n.Length * 100 + (prefer.Length > 0 && n == prefer ? 50 : 0) - Math.Min(order, 49);
            if (score > bestScore)
            {
                bestScore = score;
                best = new ArtistMatch(name, id, rest.Trim());
            }
        }
        return best;
    }

    /// <summary>
    /// How well a release title answers the typed words, 0 for not at all: equal (100), the
    /// same without spaces (90), starts with them (80), holds them as a run (60), holds every
    /// word (40). Case, accents, apostrophes and " - EP" don't count.
    /// </summary>
    internal static int TitleScore(string? title, string? wanted)
    {
        var t = Normalize(title);
        var w = Normalize(wanted);
        if (t.Length == 0 || w.Length == 0) return 0;
        if (t == w) return 100;
        if (t.Replace(" ", string.Empty, StringComparison.Ordinal) == w.Replace(" ", string.Empty, StringComparison.Ordinal)) return 90;
        if (t.StartsWith(w + " ", StringComparison.Ordinal)) return 80;
        if ($" {t} ".Contains($" {w} ", StringComparison.Ordinal)) return 60;
        var words = t.Split(' ');
        return w.Split(' ').All(x => words.Contains(x)) ? 40 : 0;
    }

    /// <summary>Whether a release is the artist's own (else it is one they appear on).</summary>
    internal static bool IsOwnRelease(Candidate c, ArtistMatch artist)
    {
        if (c.ArtistId > 0 && c.ArtistId == artist.Id) return true;
        if (c.ArtistId > 0) return false;
        return AnimatedArtworkMatcher.Normalize(Track.GetPrimaryArtist(c.ArtistName)) == AnimatedArtworkMatcher.Normalize(artist.Name);
    }

    /// <summary>Newest first; undated last, in the order they came.</summary>
    private static IEnumerable<Candidate> Newest(IEnumerable<Candidate> items)
        => items.Select((c, i) => (c, i))
            .OrderByDescending(x => x.c.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(x => x.i)
            .Select(x => x.c);

    /// <summary>
    /// One release per title and kind: the store lists an explicit and a clean "MAMACITA -
    /// Single" with the same cover. The first (or the explicit one) stays.
    /// </summary>
    internal static IReadOnlyList<Candidate> Dedupe(IEnumerable<Candidate> items)
    {
        var byId = new HashSet<long>();
        var kept = new List<Candidate>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in items)
        {
            if (!byId.Add(c.CollectionId)) continue;
            var key = $"{c.ArtistId}|{IsSingleOrEp(c.CollectionName)}|{Normalize(c.CollectionName)}";
            if (byKey.TryGetValue(key, out var at))
            {
                if (kept[at].Explicitness.Equals("cleaned", StringComparison.OrdinalIgnoreCase) &&
                    !c.Explicitness.Equals("cleaned", StringComparison.OrdinalIgnoreCase))
                    kept[at] = c;
                continue;
            }
            byKey[key] = kept.Count;
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>
    /// Lays the answer out. With an artist and nothing else typed: their Albums, Singles &amp;
    /// EPs, and Appears On, each newest first, and nobody else's records. With a title after
    /// the artist: the artist's releases matching it (best first), then the rest of theirs.
    /// With no artist: the search hits, the ones whose title matches the query first.
    /// </summary>
    /// <param name="discography">The artist's releases (/lookup), empty when none was read.</param>
    /// <param name="searchHits">The album search hits (Apple Music web search, then iTunes), in rank order.</param>
    internal static ArtworkLibraryResult Arrange(string query, ArtistMatch? artist,
        IReadOnlyList<Candidate> discography, IReadOnlyList<Candidate> searchHits)
    {
        if (artist is null)
            return new ArtworkLibraryResult(null, 0, Plain(query, searchHits));

        // The discography first; the artist's search hits fill a hole in it (a /lookup failure).
        var pool = Dedupe(discography.Concat(searchHits.Where(c => IsOwnRelease(c, artist))));
        var own = pool.Where(c => IsOwnRelease(c, artist)).ToList();
        var appears = discography.Count > 0
            ? pool.Where(c => !IsOwnRelease(c, artist)).ToList()
            : new List<Candidate>();
        var sections = new List<ArtworkLibrarySection>();

        if (artist.Rest.Length == 0)
        {
            AddSection(sections, ArtworkSectionKind.Albums, Newest(own.Where(c => !IsSingleOrEp(c.CollectionName))));
            AddSection(sections, ArtworkSectionKind.SinglesAndEps, Newest(own.Where(c => IsSingleOrEp(c.CollectionName))));
            AddSection(sections, ArtworkSectionKind.AppearsOn, Newest(appears));
            return new ArtworkLibraryResult(artist.Name, artist.Id, sections);
        }

        var matches = own.Concat(appears)
            .Select((c, i) => (c, i, score: TitleScore(c.CollectionName, artist.Rest)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.c.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(x => x.i)
            .Select(x => x.c)
            .ToList();
        if (matches.Count == 0)
        {
            // Nothing of theirs is called that: what the plain search found for the whole
            // query, minus the artist's own records (they follow, all of them).
            var others = Plain(query, searchHits.Where(c => !IsOwnRelease(c, artist)).ToList(), keepUnmatched: false);
            sections.AddRange(others);
        }
        else
        {
            AddSection(sections, ArtworkSectionKind.Matches, matches);
        }
        AddSection(sections, ArtworkSectionKind.MoreByArtist, Newest(own.Where(c => !matches.Contains(c))));
        return new ArtworkLibraryResult(artist.Name, artist.Id, sections);
    }

    private static IReadOnlyList<ArtworkLibrarySection> Plain(string query, IReadOnlyList<Candidate> hits, bool keepUnmatched = true)
    {
        var ranked = Dedupe(hits)
            .Select((c, i) => (c, i, score: Math.Max(TitleScore(c.CollectionName, query),
                ITunesArtworkService.MatchesTypedAlbumQuery(c.CollectionName, c.ArtistName, query) ? 100 : 0)))
            .Where(x => keepUnmatched || x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.i)
            .Select(x => x.c)
            .Take(MaxPlainResults)
            .ToList();
        return ranked.Count == 0
            ? Array.Empty<ArtworkLibrarySection>()
            : new[] { new ArtworkLibrarySection(ArtworkSectionKind.Results, ranked) };
    }

    private static void AddSection(List<ArtworkLibrarySection> sections, ArtworkSectionKind kind, IEnumerable<Candidate> items)
    {
        var list = items.ToList();
        if (list.Count > 0)
            sections.Add(new ArtworkLibrarySection(kind, list));
    }
}
