using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>
/// Normalizes album titles for provider lookups by stripping edition/version suffixes that
/// over-constrain a search. "Goodbye &amp; Good Riddance (5 Year Anniversary Edition) [Deluxe]"
/// has no exact release on MusicBrainz/Deezer, so the raw title yields zero matches; the
/// normalized base title ("Goodbye &amp; Good Riddance") matches the canonical release. Used as a
/// query hint only — the original tag value is never mutated.
/// </summary>
public static partial class AlbumTitleNormalizer
{
    // Words that mark a parenthetical/bracketed/trailing segment as an edition descriptor
    // rather than part of the real title. Deliberately conservative: "Live", "Acoustic" and
    // "Remix" are NOT included because they can denote a genuinely distinct release.
    private const string EditionKeywords =
        "deluxe|edition|expanded|remaster|remastered|anniversary|version|bonus|reissue|" +
        "special|collector|collectors|limited|super|platinum|definitive|complete|" +
        "explicit|clean|mono|stereo|remix(?:ed)? edition";

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(?:" + EditionKeywords + @")\b[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedEdition();

    [GeneratedRegex(@"\s*[-–—:]\s*[^-–—:]*\b(?:" + EditionKeywords + @")\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingEdition();

    // Trailing " - Single" / " - EP" markers that aren't covered by the keyword list.
    [GeneratedRegex(@"\s*[-–—]\s*(?:single|ep)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSingleEp();

    /// <summary>
    /// Returns the album title with trailing edition/version descriptors removed. Falls back to
    /// the original (trimmed) value when stripping would leave nothing.
    /// </summary>
    public static string Normalize(string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return string.Empty;

        var result = BracketedEdition().Replace(album, string.Empty);
        result = TrailingEdition().Replace(result, string.Empty);
        result = TrailingSingleEp().Replace(result, string.Empty);

        // Collapse whitespace and trim leftover separators left behind by the removals.
        result = Regex.Replace(result, @"\s{2,}", " ").Trim().Trim('-', '–', '—', ':').Trim();

        return string.IsNullOrWhiteSpace(result) ? album.Trim() : result;
    }
}
