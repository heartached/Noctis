using System;
using System.Collections.Generic;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Helpers;

/// <summary>
/// "Other Versions" (Discord, Cosmo 11-08: "switch a song like instrumental or like remasters"):
/// finds the library's other recordings of a song — same primary artist, same base title once the
/// version suffixes are stripped ("(Instrumental)", "[Remastered 2011]", "- Live", "(Radio Edit)",
/// "(Deluxe)", "(Taylor's Version)" …, see <see cref="MatchText.AnalyzeTitle(string?)"/>). Only
/// bracketed or " - " suffixes count as versions, so "Live Forever" is not a live take of anything.
/// </summary>
public static class SongVersions
{
    // Titles that name a slot on an album rather than a song: every album's "Intro" would
    // otherwise list every other album's "Intro" by the same artist as a version of it.
    private static readonly HashSet<string> GenericBases = new(StringComparer.Ordinal)
    {
        "intro", "outro", "interlude", "skit", "prelude", "untitled", "hiddentrack", "bonustrack",
        "unknown", "track", "reprise", "silence",
    };

    /// <summary>
    /// Other library tracks that are versions of <paramref name="track"/>, in library order, the
    /// track itself (and rows pointing at the same file) left out. Empty for an untagged artist,
    /// a generic title ("Intro", "Track 3") or a title with nothing left after stripping.
    /// </summary>
    public static IReadOnlyList<Track> FindOtherVersions(Track track, IEnumerable<Track> library)
    {
        var result = new List<Track>();
        if (!TryKey(track, out var artistKey, out var baseKey, out var prefixKey)) return result;

        foreach (var candidate in library)
        {
            if (ReferenceEquals(candidate, track) || candidate.Id == track.Id) continue;
            // Cheap prefilter on the cached search key: version suffixes come after the base
            // title, so a version's whole-title key starts with the base's (punctuation and
            // "&" both drop out of SearchTitleKey, hence the separate prefix key).
            if (!candidate.SearchTitleKey.StartsWith(prefixKey, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(track.FilePath)
                && string.Equals(candidate.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryKey(candidate, out var a, out var b, out _)) continue;
            if (a == artistKey && b == baseKey) result.Add(candidate);
        }
        return result;
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are versions of one song
    /// (same rules as <see cref="FindOtherVersions"/>, without the same-file exclusion).</summary>
    public static bool AreVersions(Track a, Track b) =>
        TryKey(a, out var artistA, out var baseA, out _)
        && TryKey(b, out var artistB, out var baseB, out _)
        && artistA == artistB && baseA == baseB;

    /// <summary>
    /// The version a title names, as written: "Song (Live) [2011 Remaster]" → "Live · 2011 Remaster".
    /// Empty for the plain title (the menu shows it as the original).
    /// </summary>
    public static string VersionLabel(string? title) =>
        string.Join(" · ", MatchText.VersionSegments(title));

    /// <summary>
    /// Where a switch to another version starts. The playing position carries over when the two
    /// recordings are about as long (within 10 s or 5 % of the playing one, whichever is more) —
    /// a remaster, a clean/explicit or a deluxe copy lines up — and the position is inside the new
    /// one. A live take, an extended mix or an unknown length starts from the beginning.
    /// </summary>
    public static TimeSpan SwitchStartPosition(TimeSpan position, TimeSpan fromDuration, TimeSpan toDuration)
    {
        if (position <= TimeSpan.FromSeconds(3)) return TimeSpan.Zero;
        if (fromDuration <= TimeSpan.Zero || toDuration <= TimeSpan.Zero) return TimeSpan.Zero;
        var tolerance = TimeSpan.FromSeconds(Math.Max(10, fromDuration.TotalSeconds * 0.05));
        if ((toDuration - fromDuration).Duration() > tolerance) return TimeSpan.Zero;
        if (position >= toDuration - TimeSpan.FromSeconds(2)) return TimeSpan.Zero;
        return position;
    }

    private static bool TryKey(Track track, out string artistKey, out string baseKey, out string prefixKey)
    {
        artistKey = baseKey = prefixKey = string.Empty;
        var primary = track.PrimaryArtist;
        if (string.IsNullOrWhiteSpace(primary)
            || primary.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)) return false;
        artistKey = MatchText.Fold(primary);
        if (artistKey.Length == 0) return false;

        baseKey = MatchText.AnalyzeTitle(track.Title).Base;
        if (baseKey.Length == 0 || IsGeneric(baseKey)) return false;
        // The base without Fold's "&" → "and", i.e. in SearchText.Normalize's terms.
        prefixKey = track.Title.Contains('&')
            ? MatchText.AnalyzeTitle(track.Title.Replace('&', ' ')).Base
            : baseKey;
        return prefixKey.Length > 0;
    }

    // "Intro", "Track 3", "Interlude 2" and bare numbers.
    private static bool IsGeneric(string baseKey)
    {
        var letters = baseKey.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return letters.Length == 0 || GenericBases.Contains(letters);
    }
}
