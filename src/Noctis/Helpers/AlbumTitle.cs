using System.Text.RegularExpressions;

namespace Noctis.Helpers;

/// <summary>
/// Shared album-title normalization used for grouping different editions/issues of the
/// same release (e.g. "(Deluxe Edition)", "(Reissue)", "[Remastered]", " - Single").
/// This is the single source of truth used by both <see cref="ViewModels.AlbumDetailViewModel"/>'s
/// "Other Versions" section and the optional album-grid edition collapsing.
/// </summary>
public static class AlbumTitle
{
    private static readonly Regex s_featRegex =
        new(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring)\s+[^\)\]]+[\)\]]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_trailingParensRegex =
        new(@"\s*[\(\[][^\)\]]*[\)\]]\s*$", RegexOptions.Compiled);

    private static readonly Regex s_trailingDashSuffixRegex =
        new(@"\s*-\s*(single|ep)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Trailing decimal version marker, e.g. "Album 2.0" / "Album 3.5". Narrowly scoped to
    // digit.digit so non-version numbers ("Blink 182", "1989", "Channel 1") are untouched.
    private static readonly Regex s_trailingVersionRegex =
        new(@"\s+\d+\.\d+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes an album title for edition grouping by stripping any trailing
    /// parenthetical/bracketed segment (e.g. "(Deluxe Edition)", "(3am Edition)",
    /// "[Remastered]") plus " - Single" / " - EP" suffixes and any embedded
    /// "(feat. ...)" group. Mirrors Apple Music's "Other Versions" grouping behavior.
    /// </summary>
    public static string NormalizeForEdition(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = title.Trim();
        s = s_featRegex.Replace(s, " ").Trim();
        // Iteratively strip trailing markers so stacked suffixes like
        // "Album (Deluxe Edition) [Remastered]" collapse to "Album".
        for (int i = 0; i < 6; i++)
        {
            var prev = s;
            s = s_trailingParensRegex.Replace(s, string.Empty).Trim();
            s = s_trailingDashSuffixRegex.Replace(s, string.Empty).Trim();
            s = s_trailingVersionRegex.Replace(s, string.Empty).Trim();
            s = s.TrimEnd('-', '–', '—').Trim();
            if (s == prev) break;
        }
        return s;
    }

    /// <summary>
    /// True when the title is already a plain/base edition — i.e. it equals its own
    /// normalized base (case-insensitive), carrying no edition suffix.
    /// </summary>
    public static bool IsBaseEdition(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        return string.Equals(trimmed, NormalizeForEdition(title), StringComparison.OrdinalIgnoreCase);
    }
}
