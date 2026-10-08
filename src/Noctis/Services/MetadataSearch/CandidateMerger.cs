namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Folds the same recording/release found by several providers into one candidate holding the
/// union of their fields (owner 10-08: Search metadata revamp — one row per real result, not
/// three near-identical ones).
///
/// Same entity (members always from different providers):
///   track — same ISRC, or same base title + primary artist + album and durations within 3 s;
///   album — same barcode, or same album + artist with equal track count and year.
///
/// Field precedence (first source that has a value wins):
///   title/artist/album/album artist, genre, track/disc numbers+counts, explicit, duration:
///     Apple Music → Deezer → MusicBrainz (store-clean text, one curated genre);
///   release date/year: MusicBrainz (release-group original date) → Apple → Deezer (Deezer's
///     album date can be a re-delivery date);
///   label, barcode: MusicBrainz → Deezer (Apple exposes neither);
///   ISRC: Deezer → MusicBrainz (Deezer's is per-release; MB lists several per recording);
///   composer: MusicBrainz; copyright: Apple; BPM: Deezer;
///   artwork: Apple (3000 px) → Cover Art Archive original → Deezer (1000 px), thumb with it;
///   track list: Apple → Deezer → MusicBrainz (first non-empty).
/// </summary>
public static class CandidateMerger
{
    private static readonly string[] PresentationOrder = { ProviderNames.AppleMusic, ProviderNames.Deezer, ProviderNames.MusicBrainz };
    private static readonly string[] DateOrder = { ProviderNames.MusicBrainz, ProviderNames.AppleMusic, ProviderNames.Deezer };
    private static readonly string[] CatalogOrder = { ProviderNames.MusicBrainz, ProviderNames.Deezer, ProviderNames.AppleMusic };
    private static readonly string[] IsrcOrder = { ProviderNames.Deezer, ProviderNames.MusicBrainz, ProviderNames.AppleMusic };
    private static readonly string[] ArtworkOrder = { ProviderNames.AppleMusic, ProviderNames.MusicBrainz, ProviderNames.Deezer };

    /// <summary>Groups duplicates (input should be scored, best first) and merges each group.</summary>
    public static IReadOnlyList<IReadOnlyList<MetadataCandidate>> Group(IEnumerable<MetadataCandidate> candidates, bool albumScope)
    {
        var groups = new List<List<MetadataCandidate>>();
        foreach (var c in candidates)
        {
            // Several editions can look alike by text; join the group sharing an ISRC/barcode
            // with it if any, else the one most sources already agree on.
            var home = groups
                .Where(g => g.All(m => m.Provider != c.Provider) && g.Any(m => SameEntity(m, c, albumScope)))
                .OrderByDescending(g => g.Any(m => SameCode(m, c, albumScope)))
                .ThenByDescending(g => g.Count)
                .FirstOrDefault();
            if (home is null) groups.Add(new List<MetadataCandidate> { c });
            else home.Add(c);
        }
        return groups;
    }

    public static bool SameEntity(MetadataCandidate a, MetadataCandidate b, bool albumScope)
    {
        if (albumScope)
        {
            if (MatchText.SameBarcode(a.Barcode, b.Barcode)) return true;
            if (a.TrackCount is { } ta && b.TrackCount is { } tb && ta != tb) return false;
            if (a.Year is { } ya && b.Year is { } yb && ya != yb) return false;
            return SameText(Album(a), Album(b)) && SameArtist(a.AlbumArtist.Length > 0 ? a.AlbumArtist : a.Artist,
                                                              b.AlbumArtist.Length > 0 ? b.AlbumArtist : b.Artist);
        }

        if (MatchText.SameIsrc(a.Isrc, b.Isrc)) return true;
        if (a.Duration is { } da && b.Duration is { } db && Math.Abs((da - db).TotalSeconds) > 3) return false;
        var ta2 = MatchText.AnalyzeTitle(a.Title);
        var tb2 = MatchText.AnalyzeTitle(b.Title);
        if (ta2.Base.Length == 0 || ta2.Base != tb2.Base || !ta2.Markers.SetEquals(tb2.Markers)) return false;
        if (!SameArtist(a.Artist, b.Artist)) return false;
        return a.Album.Length == 0 || b.Album.Length == 0 || SameText(Album(a), Album(b));
    }

    private static bool SameCode(MetadataCandidate a, MetadataCandidate b, bool albumScope)
        => albumScope ? MatchText.SameBarcode(a.Barcode, b.Barcode) : MatchText.SameIsrc(a.Isrc, b.Isrc);

    private static string Album(MetadataCandidate c) => MatchText.Fold(AlbumTitleNormalizer.Normalize(c.Album));
    private static bool SameText(string a, string b) => a.Length > 0 && a == b;

    private static bool SameArtist(string a, string b)
    {
        var x = MatchText.SplitArtists(a);
        var y = MatchText.SplitArtists(b);
        return x.Count > 0 && y.Count > 0 && x[0] == y[0];
    }

    /// <summary>Merges one group into a single candidate (a lone member is returned as-is).</summary>
    public static MetadataCandidate Merge(IReadOnlyList<MetadataCandidate> group)
    {
        if (group.Count == 1) return group[0];

        // Id and web link come from the first-named provider, so "Apple Music + Deezer" opens
        // (and de-duplicates) as the Apple Music entry.
        var lead = Ordered(group, PresentationOrder).First();
        var art = Ordered(group, ArtworkOrder).FirstOrDefault(m => m.ArtworkUrl is not null);
        return new MetadataCandidate
        {
            Provider = string.Join(" + ", Ordered(group, PresentationOrder).Select(m => m.Provider)),
            ProviderId = lead.ProviderId,
            WebUrl = lead.WebUrl ?? group.Select(m => m.WebUrl).FirstOrDefault(u => u is not null),
            Confidence = group.Max(m => m.Confidence),
            Title = Text(group, PresentationOrder, m => m.Title),
            Artist = Text(group, PresentationOrder, m => m.Artist),
            Album = Text(group, PresentationOrder, m => m.Album),
            AlbumArtist = Text(group, PresentationOrder, m => m.AlbumArtist),
            ReleaseDate = Text(group, DateOrder, m => m.ReleaseDate),
            Year = Value(group, DateOrder, m => m.ReleaseDate.Length > 0 ? m.Year : null)
                   ?? Value(group, DateOrder, m => m.Year),
            Genre = Text(group, PresentationOrder, m => m.Genre),
            TrackNumber = Value(group, PresentationOrder, m => m.TrackNumber),
            TrackCount = Value(group, PresentationOrder, m => m.TrackCount),
            DiscNumber = Value(group, PresentationOrder, m => m.DiscNumber),
            DiscCount = Value(group, PresentationOrder, m => m.DiscCount),
            Composer = Text(group, CatalogOrder, m => m.Composer),
            Label = Text(group, CatalogOrder, m => m.Label),
            Copyright = Text(group, PresentationOrder, m => m.Copyright),
            Isrc = Text(group, IsrcOrder, m => m.Isrc),
            Barcode = Text(group, CatalogOrder, m => m.Barcode),
            Explicit = Value(group, PresentationOrder, m => m.Explicit),
            Bpm = Value(group, IsrcOrder, m => m.Bpm),
            Duration = Value(group, PresentationOrder, m => m.Duration),
            ArtworkUrl = art?.ArtworkUrl,
            ArtworkThumbUrl = art?.ArtworkThumbUrl ?? group.Select(m => m.ArtworkThumbUrl).FirstOrDefault(u => u is not null),
            ArtworkSize = art?.ArtworkSize,
            Tracks = Ordered(group, PresentationOrder).Select(m => m.Tracks).FirstOrDefault(t => t.Count > 0)
                     ?? Array.Empty<CandidateTrack>(),
        };
    }

    private static IEnumerable<MetadataCandidate> Ordered(IEnumerable<MetadataCandidate> group, string[] order)
        => group.OrderBy(m => Rank(m.Provider, order));

    private static int Rank(string provider, string[] order)
    {
        var i = Array.IndexOf(order, provider);
        return i < 0 ? order.Length : i;
    }

    private static string Text(IEnumerable<MetadataCandidate> group, string[] order, Func<MetadataCandidate, string> pick)
        => Ordered(group, order).Select(pick).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;

    private static T? Value<T>(IEnumerable<MetadataCandidate> group, string[] order, Func<MetadataCandidate, T?> pick) where T : struct
        => Ordered(group, order).Select(pick).FirstOrDefault(v => v.HasValue);
}
