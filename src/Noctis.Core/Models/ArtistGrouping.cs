using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Noctis.Models;

/// <summary>
/// Which tag the Artists section aggregates a track under (GitHub #51).
/// </summary>
public enum ArtistGroupMode
{
    /// <summary>The first credited name of the track's ARTIST tag — the historical behaviour.</summary>
    Artist,
    /// <summary>The first credited name of the ALBUM ARTIST tag, falling back to the track
    /// artist when the file carries none. Compilations land under "Various Artists".</summary>
    AlbumArtist,
}

public static class ArtistGroupModes
{
    public const string DefaultSetting = nameof(ArtistGroupMode.Artist);

    /// <summary>Parses the persisted setting by NAME; anything unknown (including a bare number,
    /// which Enum.TryParse would otherwise accept) falls back to grouping by artist.</summary>
    public static ArtistGroupMode Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return ArtistGroupMode.Artist;
        return Enum.TryParse<ArtistGroupMode>(name, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : ArtistGroupMode.Artist;
    }
}

/// <summary>
/// Process-wide artist-credit tokenizer: the separators that split a multi-artist tag
/// ("A / B", "A feat. B") into credited names, and the grouping mode the artist index
/// uses. One authority — <see cref="Track.ParseArtistTokens"/>, ArtistImageService and
/// the library index all go through here, so the Settings separators apply everywhere
/// (before #51 two hand-copied regexes had already drifted from each other in spirit).
///
/// Configured from AppSettings by SettingsViewModel during settings load (which runs
/// before LibraryService.LoadAsync) and again by LibraryService's startup settings read.
/// </summary>
public static class ArtistCredit
{
    /// <summary>
    /// Out-of-the-box separators. Deliberately symbols plus the explicit "featuring"
    /// spellings only: the old hard-coded list also split on the bare words "and",
    /// "with" and "x", which cut real names in half app-wide ("Florence and the
    /// Machine" → "Florence", "Lil Nas X" → "Lil Nas") — the "artists missing their
    /// full name" complaint in #51. "&amp;" is left out too: duos carry it inside their
    /// name ("Simon &amp; Garfunkel", "Bomba Estéreo &amp; ..."), so splitting on it by
    /// default cut them in two (Discord report). Users who want those back can add them.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSeparators =
        new[] { "/", ",", ";", "feat.", "ft.", "featuring" };

    /// <summary>
    /// Out-of-the-box "never split" names: well-known artists whose own name contains a
    /// default separator, so splitting would cut them apart ("Tyler, The Creator" →
    /// "Tyler" + "The Creator"; Discord, aaron 2026-10-18). Removing "," kept those whole
    /// but broke every "A, B" collaboration credit. Users add their own in Settings › Library.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultProtectedNames = new[]
    {
        "Tyler, The Creator",
        "Earth, Wind & Fire",
        "Crosby, Stills, Nash & Young",
        "Crosby, Stills & Nash",
        "Emerson, Lake & Palmer",
        "Blood, Sweat & Tears",
        "Peter, Paul and Mary",
        "Black Country, New Road",
        "Now, Now",
        "AC/DC",
    };

    private static readonly object Gate = new();
    private static IReadOnlyList<string> _separators = DefaultSeparators;
    private static IReadOnlyList<string> _protectedNames = DefaultProtectedNames;
    private static Rules _rules = new(BuildSplitRegex(DefaultSeparators), BuildProtection(DefaultProtectedNames, DefaultSeparators));
    private static ArtistGroupMode _groupMode = ArtistGroupMode.Artist;
    private static int _version;

    /// <summary>Split regex + protected-name matchers built from one configuration, swapped
    /// as a unit so a concurrent <see cref="Split(string?)"/> never mixes two configurations.</summary>
    private sealed record Rules(Regex SplitRegex, Protection? Protection);

    /// <summary>
    /// Finds protected names in a credit. <c>Find</c> is a plain case-insensitive alternation
    /// of the names (cheap: most credits contain none); <c>Whole</c> re-checks a hit at its
    /// position (\G) with the boundary rule: the name must start at the credit's start or
    /// right after a separator, and end at the credit's end or right before one, so
    /// "Now, Now" never bites into "Now, Now Every Children".
    /// </summary>
    private sealed record Protection(Regex Find, Regex Whole);

    /// <summary>Active separators, normalized (trimmed, de-duplicated, never empty).</summary>
    public static IReadOnlyList<string> Separators => _separators;

    /// <summary>Active "never split" names, normalized (trimmed, de-duplicated; may be empty).</summary>
    public static IReadOnlyList<string> ProtectedNames => _protectedNames;

    /// <summary>Active grouping mode for the artist index.</summary>
    public static ArtistGroupMode GroupMode => _groupMode;

    /// <summary>
    /// Bumps whenever the configuration changes. Track caches its parsed primary artist
    /// against this so a separator edit invalidates every cached value without a walk.
    /// </summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Stable fingerprint of the configuration the artist index was built under. Persisted
    /// in the index cache: a restore under a different mode, separator set or "never split"
    /// name list must rebuild,
    /// or the Artists grid shows the old grouping until the next scan.
    /// </summary>
    public static string Signature
    {
        get { lock (Gate) return BuildSignature(_groupMode, _separators, _protectedNames); }
    }

    /// <summary>Signature under the default "never split" names.</summary>
    public static string BuildSignature(ArtistGroupMode mode, IEnumerable<string> separators)
        => BuildSignature(mode, separators, DefaultProtectedNames);

    public static string BuildSignature(ArtistGroupMode mode, IEnumerable<string> separators, IEnumerable<string>? protectedNames)
        => mode + "|" + string.Join("", NormalizeSeparators(separators))
           + "|" + string.Join("\u001F", NormalizeProtectedNames(protectedNames));

    /// <summary>Applies a mode + separator set, keeping the active "never split" names.
    /// No-op (no version bump) when nothing changed.</summary>
    public static void Configure(ArtistGroupMode mode, IEnumerable<string>? separators)
        => Configure(mode, separators, null);

    /// <summary>
    /// Applies a mode, separator set and "never split" name list (null keeps the active
    /// names). No-op (no version bump) when nothing changed.
    /// </summary>
    public static void Configure(ArtistGroupMode mode, IEnumerable<string>? separators, IEnumerable<string>? protectedNames)
    {
        var normalized = NormalizeSeparators(separators);
        var names = protectedNames == null ? null : NormalizeProtectedNames(protectedNames);
        lock (Gate)
        {
            names ??= _protectedNames;
            if (mode == _groupMode
                && normalized.SequenceEqual(_separators, StringComparer.Ordinal)
                && names.SequenceEqual(_protectedNames, StringComparer.Ordinal))
                return;
            _separators = normalized;
            _protectedNames = names;
            Volatile.Write(ref _rules, new Rules(BuildSplitRegex(normalized), BuildProtection(names, normalized)));
            _displayRegex = null;
            _groupMode = mode;
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Restores the shipped defaults (used by Settings' reset and by tests).</summary>
    public static void ResetToDefaults()
    {
        Configure(ArtistGroupMode.Artist, DefaultSeparators, DefaultProtectedNames);
        ShowAsTagged = false;
    }

    private static volatile bool _showAsTagged;

    /// <summary>
    /// GitHub #187: Settings › Library › "Show artists as written in the tags". Off (default):
    /// <see cref="Display(string?)"/> reads active symbol separators as the join text. On: a
    /// credit is shown exactly as tagged ("Kraak &amp; Smaak", "A x B", "A; B"). Display only —
    /// <see cref="Split(string?)"/>, the artist index and artist links are unchanged, and the
    /// shown text still splits into the same names, so a click resolves per artist. Mirrored
    /// from AppSettings by SettingsViewModel (static for the same reason as the separators).
    /// </summary>
    public static bool ShowAsTagged
    {
        get => _showAsTagged;
        set => _showAsTagged = value;
    }

    /// <summary>
    /// Trims, drops blanks, and de-duplicates case-insensitively. Unlike the separators an
    /// empty list is kept: it simply protects nothing.
    /// </summary>
    public static IReadOnlyList<string> NormalizeProtectedNames(IEnumerable<string>? names)
        => (names ?? Array.Empty<string>())
            .Where(n => n != null)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Trims, drops blanks, and de-duplicates case-insensitively. An empty result falls back
    /// to the defaults: a separator list with nothing in it would make every collaboration
    /// credit its own artist, which is never what a user editing the list meant.
    /// </summary>
    public static IReadOnlyList<string> NormalizeSeparators(IEnumerable<string>? separators)
    {
        var list = (separators ?? Array.Empty<string>())
            .Where(s => s != null)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return list.Length > 0 ? list : DefaultSeparators;
    }

    /// <summary>
    /// The text that joins names Noctis itself adds to a credit (featured artists merged in
    /// from a title), built from an ACTIVE separator so the result splits back apart. It
    /// used to be a hard-coded " &amp; ", which stopped splitting once "&amp;" left the
    /// defaults in 1.5.1 (Discord Luwi 09-22). Prefers the readable symbols in order.
    /// </summary>
    public static string JoinText => JoinTextFor(_separators);

    public static string JoinTextFor(IReadOnlyList<string> separators)
    {
        if (separators.Contains(",")) return ", ";
        if (separators.Contains(";")) return "; ";
        if (separators.Contains("/")) return " / ";
        return " " + separators[0] + " ";
    }

    /// <summary>
    /// A credit as it is SHOWN: active symbol separators ("Rihanna; Drake", "A / B") read as
    /// the join text ("Rihanna, Drake"); the tag itself is never rewritten. Word separators
    /// ("feat.") stay as written, and a "/" with no space on either side is left alone so a
    /// name like "AC/DC" keeps its slash. The result splits back into the same names, so a
    /// click on the shown text still resolves per artist (ArtistCreditSpans). Discord (Luwi,
    /// 2026-10-03): the semicolons looked technical in rows and tiles.
    /// </summary>
    public static string Display(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (_showAsTagged) return value.Trim();
        Regex? regex;
        string join;
        Rules rules;
        lock (Gate)
        {
            regex = _displayRegex ??= BuildDisplayRegex(_separators);
            join = JoinTextFor(_separators);
            rules = _rules;
        }
        if (regex == null) return value;
        var spans = FindProtected(value, rules.Protection);
        return regex.Replace(value, m =>
        {
            // Untouched when the match is already the join ("A, B") or a tight slash ("AC/DC"),
            // or sits inside a "never split" name, which is shown exactly as tagged.
            if (m.Value == join) return m.Value;
            if (m.Value == "/") return m.Value;
            if (Overlaps(spans, m.Index, m.Length)) return m.Value;
            return join;
        }).Trim();
    }

    /// <summary>
    /// A track credit as shown, given the credit Noctis indexes (<paramref name="credit"/>,
    /// which may carry names merged in from the title) and the tag's own text
    /// (<paramref name="asTagged"/>, null when it is the same). With
    /// <see cref="ShowAsTagged"/> on the tag's text is shown as written; off, the indexed
    /// credit goes through <see cref="Display(string?)"/>.
    /// </summary>
    public static string Display(string? credit, string? asTagged)
        => _showAsTagged && !string.IsNullOrWhiteSpace(asTagged) ? asTagged.Trim() : Display(credit);

    private static Regex? _displayRegex;

    /// <summary>
    /// Splits a credit into the separate values written back to a file's artist tag: on the
    /// active SYMBOL separators only, with the same rule as <see cref="Display"/> (a tight
    /// "/" stays inside a name like "AC/DC"). Word separators are not split: "A feat. B" is
    /// one credit as tagged and must stay one frame for other players. GitHub #117: this used
    /// to split on a hard-coded "," and ";", so saving "Earth, Wind &amp; Fire" wrote two
    /// performers even with "," removed from the separators.
    /// </summary>
    public static string[] SplitForTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        Regex? regex;
        Rules rules;
        lock (Gate)
        {
            regex = _displayRegex ??= BuildDisplayRegex(_separators);
            rules = _rules;
        }

        var parts = new List<string>();
        var start = 0;
        if (regex != null)
        {
            var spans = FindProtected(value, rules.Protection);
            foreach (Match m in regex.Matches(value))
            {
                if (m.Value == "/") continue;
                if (Overlaps(spans, m.Index, m.Length)) continue;
                parts.Add(value[start..m.Index]);
                start = m.Index + m.Length;
            }
        }
        parts.Add(value[start..]);

        return parts
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Regex? BuildDisplayRegex(IReadOnlyList<string> separators)
    {
        var symbols = separators.Where(s => !char.IsLetterOrDigit(s[0]) && !char.IsLetterOrDigit(s[^1])).ToArray();
        if (symbols.Length == 0) return null;
        var alternation = string.Join("|", symbols.OrderByDescending(s => s.Length).Select(Regex.Escape));
        return new Regex(@"\s*(?:" + alternation + @")\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>Splits a credit into its distinct trimmed names using the active separators;
    /// active "never split" names come out whole.</summary>
    public static string[] Split(string? value) => Split(value, Volatile.Read(ref _rules));

    /// <summary>Splits with an explicit separator set and no protected names (pure; used by
    /// tests and previews).</summary>
    public static string[] Split(string? value, IEnumerable<string> separators)
        => Split(value, new Rules(BuildSplitRegex(NormalizeSeparators(separators)), null));

    /// <summary>Splits with an explicit separator set and "never split" names (pure).</summary>
    public static string[] Split(string? value, IEnumerable<string> separators, IEnumerable<string>? protectedNames)
    {
        var normalized = NormalizeSeparators(separators);
        return Split(value, new Rules(BuildSplitRegex(normalized), BuildProtection(NormalizeProtectedNames(protectedNames), normalized)));
    }

    private static string[] Split(string? value, Rules rules)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var spans = FindProtected(value, rules.Protection);
        IEnumerable<string> parts = spans == null
            ? rules.SplitRegex.Split(value)
            : SplitAround(value, spans, gap => rules.SplitRegex.Split(gap));

        return parts
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Splits <paramref name="value"/> around the active "never split" names: each one found
    /// comes out whole, and <paramref name="splitGap"/> splits the text between them. For
    /// callers with their own separator rules (MetadataSearch's looser matcher).
    /// </summary>
    public static IEnumerable<string> SplitAroundProtected(string value, Func<string, IEnumerable<string>> splitGap)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
        var spans = FindProtected(value, Volatile.Read(ref _rules).Protection);
        return spans == null ? splitGap(value) : SplitAround(value, spans, splitGap);
    }

    /// <summary>Protected spans whole, the text between them through <paramref name="splitGap"/>.
    /// The gaps start or end on a separator (that is what bounds a protected name), so their
    /// edge pieces come out blank and are dropped by the callers.</summary>
    private static List<string> SplitAround(string value, List<(int Start, int End)> spans, Func<string, IEnumerable<string>> splitGap)
    {
        var list = new List<string>();
        var start = 0;
        foreach (var (s, e) in spans)
        {
            if (s > start) list.AddRange(splitGap(value[start..s]));
            list.Add(value[s..e]);
            start = e;
        }
        if (start < value.Length) list.AddRange(splitGap(value[start..]));
        return list;
    }

    /// <summary>Character ranges [start, end) of the protected names in <paramref name="value"/>,
    /// left to right, or null when there are none (the common case allocates nothing more).</summary>
    private static List<(int Start, int End)>? FindProtected(string value, Protection? protection)
    {
        if (protection == null) return null;
        List<(int, int)>? spans = null;
        var m = protection.Find.Match(value);
        while (m.Success)
        {
            var whole = protection.Whole.Match(value, m.Index);
            if (whole.Success)
            {
                (spans ??= new List<(int, int)>()).Add((whole.Index, whole.Index + whole.Length));
                m = protection.Find.Match(value, whole.Index + whole.Length);
            }
            else
            {
                // A shorter or later name may still start inside this hit.
                m = m.Index + 1 < value.Length ? protection.Find.Match(value, m.Index + 1) : Match.Empty;
            }
        }
        return spans;
    }

    private static bool Overlaps(List<(int Start, int End)>? spans, int index, int length)
    {
        if (spans == null) return false;
        foreach (var (s, e) in spans)
            if (index < e && index + length > s) return true;
        return false;
    }

    private static Protection? BuildProtection(IReadOnlyList<string> names, IReadOnlyList<string> separators)
    {
        if (names.Count == 0) return null;
        // Whitespace inside a name matches any run (or none), so "Tyler,The Creator" is kept too.
        var alternation = string.Join("|", names
            .OrderByDescending(n => n.Length)
            .Select(n => Regex.Replace(Regex.Escape(n), @"(?:\\ |\s)+", @"\s*")));
        var sep = SeparatorAlternation(separators);
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        var find = new Regex("(?:" + alternation + ")", options);
        var whole = new Regex(@"\G(?<=(?:^|" + sep + @")\s*)(?:" + alternation + @")(?=\s*(?:\z|" + sep + "))", options);
        return new Protection(find, whole);
    }

    /// <summary>
    /// Builds the split regex. Symbols match with any surrounding whitespace absorbed.
    /// A separator that starts or ends with a word character only matches on a word
    /// boundary ("ft." must not split "Swift."); a trailing dot is optional so "feat."
    /// also catches "feat" — the spelling the old hard-coded regex accepted. Longer
    /// separators are tried first so "featuring" wins over "feat".
    /// </summary>
    private static Regex BuildSplitRegex(IReadOnlyList<string> separators)
        => new(@"\s*" + SeparatorAlternation(separators) + @"\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The separators as one non-capturing regex group (see <see cref="BuildSplitRegex"/>).</summary>
    private static string SeparatorAlternation(IReadOnlyList<string> separators)
    {
        var sb = new StringBuilder("(?:");
        var first = true;
        foreach (var sep in separators.OrderByDescending(s => s.Length))
        {
            if (!first) sb.Append('|');
            first = false;

            var body = sep;
            var optionalDot = body.Length > 1 && body.EndsWith('.');
            if (optionalDot) body = body[..^1];

            if (char.IsLetterOrDigit(body[0])) sb.Append(@"(?<!\w)");
            sb.Append(Regex.Escape(body));
            if (optionalDot) sb.Append(@"\.?");
            if (char.IsLetterOrDigit(body[^1])) sb.Append(@"(?!\w)");
        }
        sb.Append(')');
        return sb.ToString();
    }
}
