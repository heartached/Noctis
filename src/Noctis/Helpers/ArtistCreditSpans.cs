using System;
using System.Collections.Generic;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Maps a displayed artist credit ("Kanye West, GLC, Consequence") back onto the names
/// <see cref="ArtistCredit.Split"/> yields, so a click on the joined text can tell which
/// artist sits under the pointer. The island shows the credit as one marquee run (the
/// ticker measures and scrolls exactly one TextBlock), so the names are located by character
/// range instead of being rendered as separate links. Discord (aaron, 2026-09-23): the
/// island read and behaved as if the three credited artists were a single one.
/// </summary>
public static class ArtistCreditSpans
{
    public readonly record struct Span(int Start, int Length, string Name);

    /// <summary>The credit's names with their character ranges in <paramref name="text"/>, in
    /// order of appearance. A name the splitter kept but that is not found verbatim is skipped.</summary>
    public static IReadOnlyList<Span> Locate(string? text) => Locate(text, null);

    /// <summary>Like <see cref="Locate(string?)"/>, but with <paramref name="separator"/> set
    /// the names are the text's parts between that separator instead of the artist splitter's:
    /// " · " for a genre line ("Rock · Pop", <see cref="Track.DotGenres"/>).</summary>
    public static IReadOnlyList<Span> Locate(string? text, string? separator)
    {
        var result = new List<Span>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var names = string.IsNullOrEmpty(separator)
            ? ArtistCredit.Split(text)
            : text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var searchFrom = 0;
        foreach (var name in names)
        {
            var at = text.IndexOf(name, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            result.Add(new Span(at, name.Length, name));
            searchFrom = at + name.Length;
        }
        return result;
    }

    /// <summary>
    /// The name whose text covers character <paramref name="index"/>. A single-name credit
    /// resolves to that name wherever the pointer is; for several names a separator, the
    /// surrounding whitespace or a position past the end resolve to null so the caller can
    /// fall back to the primary artist.
    /// </summary>
    public static string? NameAt(string? text, int index) => NameAt(text, index, null);

    /// <summary><see cref="NameAt(string?, int)"/> with the names split on
    /// <paramref name="separator"/> (see <see cref="Locate(string?, string?)"/>).</summary>
    public static string? NameAt(string? text, int index, string? separator)
    {
        var spans = Locate(text, separator);
        if (spans.Count == 1)
            return spans[0].Name;
        foreach (var span in spans)
        {
            if (index >= span.Start && index < span.Start + span.Length)
                return span.Name;
        }
        return null;
    }
}
