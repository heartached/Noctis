namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Which edition of a release a candidate sits on, judged against the user's own copy (owner
/// 10-08: deluxe track count 17→15). An ISRC names the RECORDING, and the same recording ships
/// on the standard, deluxe, explicit, clean and regional releases alike — so an ISRC match says
/// nothing about album, track count, date, label, barcode or cover. Those release-level fields
/// are only safe to offer from the release whose shape matches the local one.
///
/// The judgment rests on counts, the one thing that tells editions apart reliably: titles
/// don't (MusicBrainz titles a 17-track explicit release exactly like the 15-track one; Apple
/// files the 17-track one without a "Deluxe" suffix). Counts are compared like for like:
/// track scope "track count" is the tag's TRCK total, which the providers give per disc (Apple,
/// MusicBrainz) or per album (Deezer), so a multi-disc side makes a differing count
/// inconclusive rather than a mismatch.
/// </summary>
public static class EditionMatch
{
    public enum Verdict { Unknown, Same, Different }

    /// <summary>Edition words that change a release's contents (unlike "Remastered" or
    /// "Explicit", which keep the track list).</summary>
    private static readonly HashSet<string> SizeWords = new(StringComparer.Ordinal)
    {
        "deluxe", "expanded", "anniversary", "bonus", "special", "collector", "collectors", "limited",
        "super", "platinum", "definitive", "complete", "extended", "tour",
    };

    /// <summary>True when the query carries the user's release shape (track/disc totals).</summary>
    public static bool HasContext(MetadataQuery q) => q.TrackCount is > 0 || q.DiscCount is > 0;

    /// <summary>Compares the candidate's release with the query's local release (track scope).</summary>
    public static (Verdict Verdict, string? Note) Compare(MetadataQuery q, MetadataCandidate c)
    {
        if (q.DiscCount is > 0 and var qd && c.DiscCount is > 0 and var cd && qd != cd)
            return (Verdict.Different, $"Different edition ({cd} vs {qd} discs)");
        if (q.TrackCount is > 0 and var qt && c.TrackCount is > 0 and var ct)
        {
            if (qt == ct) return (Verdict.Same, $"Same edition ({ct} tracks)");
            // Per-disc vs whole-album totals can't be told apart on a multi-disc release.
            if (MultiDisc(q.DiscNumber, q.DiscCount) || MultiDisc(c.DiscNumber, c.DiscCount))
                return (Verdict.Unknown, null);
            return (Verdict.Different, $"Different edition ({ct} vs {qt} tracks)");
        }
        return (Verdict.Unknown, null);
    }

    /// <summary>True when two candidates of one recording sit on releases that can't be the same
    /// edition, so their release-level fields must not be merged.</summary>
    public static bool Conflict(MetadataCandidate a, MetadataCandidate b)
    {
        if (a.DiscCount is > 0 and var ad && b.DiscCount is > 0 and var bd && ad != bd) return true;
        if (a.TrackCount is > 0 and var at && b.TrackCount is > 0 and var bt && at != bt)
            return !MultiDisc(a.DiscNumber, a.DiscCount) && !MultiDisc(b.DiscNumber, b.DiscCount);
        return false;
    }

    private static bool MultiDisc(int? number, int? count) => number is > 1 || count is > 1;

    /// <summary>Edition words of an album title: "Album (Deluxe Edition)" → {deluxe, edition};
    /// "Album (2011 Remaster)" → {} (same contents).</summary>
    public static IReadOnlySet<string> Words(string? album)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(album)) return set;
        var baseWords = Split(AlbumTitleNormalizer.Normalize(album)).ToHashSet(StringComparer.Ordinal);
        foreach (var w in Split(album))
            if (!baseWords.Contains(w) && SizeWords.Contains(w)) set.Add(w);
        return set;
    }

    /// <summary>A short edition label for notes ("Deluxe Edition", "Explicit"), from the title's
    /// edition words or the source's own edition text; empty when neither names one.</summary>
    public static string Label(MetadataCandidate c)
    {
        var words = Words(c.Album);
        if (words.Count > 0)
        {
            var title = c.Album.Trim();
            var norm = AlbumTitleNormalizer.Normalize(title);
            var suffix = title.Length > norm.Length && title.StartsWith(norm, StringComparison.Ordinal)
                ? title[norm.Length..].Trim().Trim('(', ')', '[', ']', '-', '–', '—', ':', ' ')
                : string.Empty;
            if (suffix.Length > 0) return suffix;
        }
        var e = c.Edition.Trim();
        return e.Length == 0 ? string.Empty : char.ToUpperInvariant(e[0]) + e[1..];
    }

    private static IEnumerable<string> Split(string s)
        => s.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static readonly char[] Separators = { ' ', '(', ')', '[', ']', '-', '–', '—', ':', ',', '.', '/', '\'', '"' };
}
