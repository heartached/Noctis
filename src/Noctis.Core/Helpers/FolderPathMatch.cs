namespace Noctis.Helpers;

/// <summary>
/// Allocation-free "is this path inside that folder" for the hidden-folders filter, which
/// runs once per track on every index rebuild. '/' and '\' count as the same separator
/// and case follows <see cref="PathComparison"/>; a trailing separator on the folder is
/// ignored. "Rock" never matches "Rock Classics": the folder must end at a separator.
/// </summary>
public static class FolderPathMatch
{
    /// <summary>True when <paramref name="path"/> lies anywhere below <paramref name="folder"/>
    /// (the folder itself does not count).</summary>
    public static bool IsInside(string? path, string? folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(folder)) return false;
        var root = TrimEnd(folder);
        if (path.Length <= root.Length || !IsSeparator(path[root.Length])) return false;
        return EqualPrefix(path, root, root.Length);
    }

    /// <summary>True when both name the same folder, separators and trailing slash aside.</summary>
    public static bool IsSame(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var x = TrimEnd(a);
        var y = TrimEnd(b);
        return x.Length == y.Length && EqualPrefix(x, y, x.Length);
    }

    /// <summary>True when <paramref name="path"/> is inside, or is, any of <paramref name="folders"/>.</summary>
    public static bool IsInOrSameAsAny(string? path, IReadOnlyList<string> folders)
    {
        foreach (var f in folders)
            if (IsInside(path, f) || IsSame(path, f))
                return true;
        return false;
    }

    private static string TrimEnd(string folder) => folder.TrimEnd('/', '\\');

    private static bool IsSeparator(char c) => c == '/' || c == '\\';

    private static bool EqualPrefix(string a, string b, int length)
    {
        var ignoreCase = PathComparison.Comparison == StringComparison.OrdinalIgnoreCase;
        for (int i = 0; i < length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            if (IsSeparator(x) && IsSeparator(y)) continue;
            if (ignoreCase && char.ToUpperInvariant(x) == char.ToUpperInvariant(y)) continue;
            return false;
        }
        return true;
    }
}
