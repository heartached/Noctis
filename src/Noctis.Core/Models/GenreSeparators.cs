namespace Noctis.Models;

/// <summary>
/// GitHub #260: the text that splits one genre tag into several genres ("Rock, Pop, Indie" →
/// Rock, Pop and Indie). ';' always splits: it is what <see cref="Track.Genre"/> joins a file's
/// separate genre values with. Settings › Library › Genre Separators adds the rest (',' by
/// default). Applied when genres are read for grouping and display (<see cref="Track.SplitGenres"/>,
/// <see cref="Track.SharesGenre"/>), never to the stored value or the file, so a change regroups at
/// once without a rescan and never rewrites a tag. Process-wide like <see cref="ArtistCredit"/>;
/// SettingsViewModel (desktop) and LibraryService (settings load) configure it.
/// </summary>
public static class GenreSeparators
{
    /// <summary>The separator that always applies (the stored join, see <see cref="Track.GenreSeparator"/>).</summary>
    public const char Stored = ';';

    /// <summary>
    /// The user-editable separators out of the box. Not "/": it is part of genre names like
    /// "Hip-Hop/Rap" and "Folk/Country". Not "&amp;": "R&amp;B", "Drum &amp; Bass".
    /// </summary>
    public static readonly IReadOnlyList<string> Defaults = new[] { "," };

    private static readonly object Gate = new();
    private static Splitter _current = new(Normalize(Defaults));
    private static int _version;

    /// <summary>The user-editable separators in effect (never contains ';').</summary>
    public static IReadOnlyList<string> Configured => Volatile.Read(ref _current).Configured;

    /// <summary>Every separator in effect: ';' first, then <see cref="Configured"/>.</summary>
    public static IReadOnlyList<string> Active => Volatile.Read(ref _current).All;

    /// <summary>Bumps whenever the separators change.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Applies a user separator list (null keeps the current one). No-op, and no version
    /// bump, when nothing changed. An empty list leaves ';' alone.</summary>
    public static void Configure(IEnumerable<string>? separators)
    {
        if (separators == null) return;
        var normalized = Normalize(separators);
        lock (Gate)
        {
            if (normalized.SequenceEqual(_current.Configured, StringComparer.Ordinal)) return;
            Volatile.Write(ref _current, new Splitter(normalized));
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Restores <see cref="Defaults"/> (Settings' reset and tests).</summary>
    public static void ResetToDefaults() => Configure(Defaults);

    /// <summary>Trims, drops blanks and ';' (always on anyway), de-duplicates case-insensitively.
    /// Unlike the artist separators an empty result stays empty: only ';' splits then.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? separators)
        => (separators ?? Array.Empty<string>())
            .Where(s => s != null)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s != ";")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>True when <paramref name="separator"/> (trimmed) would be added to the list.</summary>
    public static bool IsAddable(string? separator, IEnumerable<string> current)
    {
        var s = separator?.Trim() ?? string.Empty;
        return s.Length > 0 && s != ";" && !current.Any(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The genres in a stored value, split on every active separator (see
    /// <see cref="Track.SplitGenres"/>).</summary>
    internal static string[] Split(string value)
    {
        var splitter = Volatile.Read(ref _current);
        var list = new List<string>(4);
        var rest = value.AsSpan();
        while (true)
        {
            var cut = splitter.IndexOf(rest, out var length);
            var part = (cut < 0 ? rest : rest[..cut]).Trim();
            if (part.Length > 0)
            {
                var exists = false;
                foreach (var g in list)
                    if (part.Equals(g, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (!exists) list.Add(part.ToString());
            }
            if (cut < 0) return list.ToArray();
            rest = rest[(cut + length)..];
        }
    }

    /// <summary>
    /// Allocation-free walk over the genres in <paramref name="value"/>: true when one of them
    /// equals any of <paramref name="genres"/> (case-insensitive). <paramref name="tagged"/>
    /// says whether the value held any genre at all.
    /// </summary>
    internal static bool Contains(ReadOnlySpan<char> value, ReadOnlySpan<string> genres, ReadOnlySpan<char> single,
        out bool tagged)
    {
        tagged = false;
        var splitter = Volatile.Read(ref _current);
        var rest = value;
        while (true)
        {
            var cut = splitter.IndexOf(rest, out var length);
            var part = (cut < 0 ? rest : rest[..cut]).Trim();
            if (part.Length > 0)
            {
                tagged = true;
                if (genres.IsEmpty)
                {
                    if (part.Equals(single, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    foreach (var g in genres)
                        if (part.Equals(g, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            if (cut < 0) return false;
            rest = rest[(cut + length)..];
        }
    }

    /// <summary>One configuration, swapped as a unit so a reader never mixes two.</summary>
    private sealed class Splitter
    {
        public readonly IReadOnlyList<string> Configured;
        public readonly IReadOnlyList<string> All;
        private readonly string[] _all;
        // Every separator a single character: one vectorized IndexOfAny per genre.
        private readonly char[]? _chars;

        public Splitter(IReadOnlyList<string> configured)
        {
            Configured = configured;
            _all = new[] { ";" }.Concat(configured).ToArray();
            All = _all;
            _chars = _all.All(s => s.Length == 1 && !char.IsLetter(s[0])) ? _all.Select(s => s[0]).ToArray() : null;
        }

        /// <summary>Position and length of the first separator in <paramref name="text"/>
        /// (the longest one when several start there), or -1.</summary>
        public int IndexOf(ReadOnlySpan<char> text, out int length)
        {
            if (_chars != null)
            {
                length = 1;
                return text.IndexOfAny(_chars);
            }
            var best = -1;
            length = 0;
            foreach (var s in _all)
            {
                var i = IndexOfWhole(text, s);
                if (i < 0) continue;
                if (best < 0 || i < best || (i == best && s.Length > length))
                {
                    best = i;
                    length = s.Length;
                }
            }
            return best;
        }

        /// <summary>First case-insensitive occurrence of <paramref name="separator"/>; a word
        /// ("and") only as a whole word, so it never cuts "Grandcore" or "Band" apart.</summary>
        private static int IndexOfWhole(ReadOnlySpan<char> text, string separator)
        {
            var wordStart = char.IsLetterOrDigit(separator[0]);
            var wordEnd = char.IsLetterOrDigit(separator[^1]);
            var offset = 0;
            while (true)
            {
                var i = text[offset..].IndexOf(separator.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                i += offset;
                var end = i + separator.Length;
                if ((!wordStart || i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                    && (!wordEnd || end == text.Length || !char.IsLetterOrDigit(text[end])))
                    return i;
                offset = i + 1;
            }
        }
    }
}
