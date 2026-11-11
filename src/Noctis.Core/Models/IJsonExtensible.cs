using System.Text.Json;

namespace Noctis.Models;

/// <summary>
/// A saved object that keeps the JSON members it does not know (GitHub #230), for code that
/// rebuilds such objects from view state and has to carry them over to the rebuilt copy.
/// </summary>
public interface IJsonExtensible
{
    Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class JsonExtensible
{
    /// <summary>
    /// Gives each rebuilt entry the unknown members its earlier copy (same key) carried, so
    /// settings a newer Noctis stored on a theme, folder rule or EQ preset survive this
    /// build's saves. Returns <paramref name="rebuilt"/>.
    /// </summary>
    public static List<T> CarryOver<T>(IEnumerable<T>? before, List<T> rebuilt, Func<T, string?> key)
        where T : IJsonExtensible
    {
        if (before is null) return rebuilt;
        var earlier = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in before)
            if (item?.ExtensionData is { Count: > 0 } && key(item) is { } k)
                earlier.TryAdd(k, item);
        if (earlier.Count == 0) return rebuilt;
        foreach (var item in rebuilt)
            if (item is not null && item.ExtensionData is null && key(item) is { } k && earlier.TryGetValue(k, out var old))
                item.ExtensionData = old.ExtensionData;
        return rebuilt;
    }
}
