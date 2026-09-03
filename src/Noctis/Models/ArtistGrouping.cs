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
    /// full name" complaint in #51. Users who want those back can add them.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSeparators =
        new[] { "/", ",", ";", "&", "feat.", "ft.", "featuring" };

    private static readonly object Gate = new();
    private static IReadOnlyList<string> _separators = DefaultSeparators;
    private static Regex _splitRegex = BuildSplitRegex(DefaultSeparators);
    private static ArtistGroupMode _groupMode = ArtistGroupMode.Artist;
    private static int _version;

    /// <summary>Active separators, normalized (trimmed, de-duplicated, never empty).</summary>
    public static IReadOnlyList<string> Separators => _separators;

    /// <summary>Active grouping mode for the artist index.</summary>
    public static ArtistGroupMode GroupMode => _groupMode;

    /// <summary>
    /// Bumps whenever the configuration changes. Track caches its parsed primary artist
    /// against this so a separator edit invalidates every cached value without a walk.
    /// </summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Stable fingerprint of the configuration the artist index was built under. Persisted
    /// in the index cache: a restore under a different mode or separator set must rebuild,
    /// or the Artists grid shows the old grouping until the next scan.
    /// </summary>
    public static string Signature => BuildSignature(_groupMode, _separators);

    public static string BuildSignature(ArtistGroupMode mode, IEnumerable<string> separators)
        => mode + "|" + string.Join("", NormalizeSeparators(separators));

    /// <summary>Applies a mode + separator set. No-op (no version bump) when nothing changed.</summary>
    public static void Configure(ArtistGroupMode mode, IEnumerable<string>? separators)
    {
        var normalized = NormalizeSeparators(separators);
        lock (Gate)
        {
            if (mode == _groupMode && normalized.SequenceEqual(_separators, StringComparer.Ordinal))
                return;
            _separators = normalized;
            _splitRegex = BuildSplitRegex(normalized);
            _groupMode = mode;
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Restores the shipped defaults (used by Settings' reset and by tests).</summary>
    public static void ResetToDefaults() => Configure(ArtistGroupMode.Artist, DefaultSeparators);

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

    /// <summary>Splits a credit into its distinct trimmed names using the active separators.</summary>
    public static string[] Split(string? value) => Split(value, _splitRegex);

    /// <summary>Splits with an explicit separator set (pure; used by tests and previews).</summary>
    public static string[] Split(string? value, IEnumerable<string> separators)
        => Split(value, BuildSplitRegex(NormalizeSeparators(separators)));

    private static string[] Split(string? value, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return regex
            .Split(value)
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Builds the split regex. Symbols match with any surrounding whitespace absorbed.
    /// A separator that starts or ends with a word character only matches on a word
    /// boundary ("ft." must not split "Swift."); a trailing dot is optional so "feat."
    /// also catches "feat" — the spelling the old hard-coded regex accepted. Longer
    /// separators are tried first so "featuring" wins over "feat".
    /// </summary>
    private static Regex BuildSplitRegex(IReadOnlyList<string> separators)
    {
        var sb = new StringBuilder(@"\s*(?:");
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
        sb.Append(@")\s*");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
