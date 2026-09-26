using System;
using System.Globalization;
using System.Text;

namespace Noctis.Helpers;

/// <summary>
/// Search-key normalization shared by every library/playlist search surface. Folds a string to a
/// comparable key so queries match regardless of apostrophes, punctuation, spacing, casing or
/// accents — e.g. "Don't You (Taylor's Version)" and the query "dont you" both reduce to keys that
/// substring-match.
/// </summary>
public static class SearchText
{
    /// <summary>
    /// Lower-cased, diacritic-free key containing only letters and digits. Whitespace, apostrophes
    /// (straight or curly), brackets and other punctuation are removed. Returns "" for null/empty.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        string decomposed;
        try { decomposed = value.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = value; } // invalid Unicode → fold what we can

        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue; // drop combining accents
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="query"/> matches <paramref name="source"/> by case-insensitive
    /// substring OR by normalized (accent/punctuation-insensitive) substring. A blank query matches
    /// everything; a blank source matches only a blank query.
    /// </summary>
    public static bool Matches(string? source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(source)) return false;

        if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        // A query made only of punctuation ("&", "**") folds to an empty key, and every key
        // "contains" the empty string — that matched the whole library. The raw substring
        // check above is the only way such a query can match.
        var nq = Normalize(query);
        if (nq.Length == 0) return false;
        return Normalize(source).Contains(nq, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="Matches(string?, string?)"/> for a whole-library scan: <paramref name="sourceKey"/> is the
    /// source's cached <see cref="Normalize"/> key (Track.SearchTitleKey etc.) and <paramref name="queryKey"/>
    /// is Normalize(query), computed once per search. The two-argument form re-normalizes both strings on
    /// every call — up to six throwaway strings per track per search.
    /// </summary>
    public static bool Matches(string? source, string sourceKey, string query, string queryKey)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(source)) return false;

        if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (queryKey.Length == 0) return false;
        return sourceKey.Contains(queryKey, StringComparison.Ordinal);
    }
}
