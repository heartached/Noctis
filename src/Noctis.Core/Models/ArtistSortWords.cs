namespace Noctis.Models;

/// <summary>
/// Leading words the Artists grid's name sort can skip (GitHub #99): with "The" listed,
/// "The Beatles" sorts under B. Only the sort key changes; names display in full.
/// The list is edited in Settings › Library › Organisation and used while its toggle is on.
/// </summary>
public static class ArtistSortWords
{
    /// <summary>Out-of-the-box word list (the setting itself ships off).</summary>
    public static readonly IReadOnlyList<string> DefaultWords = new[] { "The" };

    /// <summary>
    /// Trims, drops blanks, and de-duplicates case-insensitively. Unlike the artist
    /// separators an empty list is kept as-is: it simply skips nothing.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? words)
        => (words ?? Array.Empty<string>())
            .Where(w => w != null)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The name to sort by: <paramref name="name"/> minus the longest listed word it starts
    /// with (case-insensitive), but only when whitespace follows that word and something
    /// remains after it — "the weeknd" → "weeknd", while "Theory of a Deadman" and a band
    /// called just "The" keep their names.
    /// </summary>
    public static string SortKey(string name, IReadOnlyList<string> words)
    {
        if (words.Count == 0 || string.IsNullOrEmpty(name))
            return name;

        string? best = null;
        foreach (var word in words)
        {
            if (word.Length == 0 || name.Length <= word.Length) continue;
            if (!char.IsWhiteSpace(name[word.Length])) continue;
            if (!name.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || word.Length > best.Length) best = word;
        }
        if (best == null)
            return name;

        var rest = name[best.Length..].TrimStart();
        return rest.Length > 0 ? rest : name;
    }
}
