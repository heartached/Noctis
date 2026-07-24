using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>Key/tempo compatibility helpers shared by AutoMix transitions and Track Radio.</summary>
public static class AutoMixKeyTempo
{
    public static bool IsTempoCompatible(int currentBpm, int nextBpm) =>
        currentBpm > 0 &&
        nextBpm > 0 &&
        GetNormalizedBpmDifference(currentBpm, nextBpm) <= 10;

    /// <summary>Plausible tempo range for the octave candidates below.</summary>
    private const double MinPlausibleBpm = 40;
    private const double MaxPlausibleBpm = 240;

    public static double GetNormalizedBpmDifference(int currentBpm, int nextBpm)
    {
        var current = Math.Clamp((double)currentBpm, MinPlausibleBpm, MaxPlausibleBpm);

        // Octave candidates are computed UNCLAMPED and out-of-range ones are dropped,
        // not saturated. Clamping them produced false matches at the boundary: for
        // current=240, next=128 the doubled candidate 256 clamped back down to exactly
        // 240, so the distance was 0 and IsTempoCompatible returned true — meaning every
        // track at 120+ BPM was "compatible" with any 240 BPM track, and the planner
        // picked a beat-matched crossfade on a genuine tempo mismatch.
        var candidates = new[] { (double)nextBpm, nextBpm * 2.0, nextBpm / 2.0 }
            .Where(c => c >= MinPlausibleBpm && c <= MaxPlausibleBpm)
            .ToArray();

        if (candidates.Length == 0)
            return double.MaxValue; // nothing plausible — never "compatible"

        return candidates.Min(candidate => Math.Abs(current - candidate));
    }

    public static bool AreKeysCompatible(string? currentKey, string? nextKey)
    {
        var current = NormalizeCamelotKey(currentKey);
        var next = NormalizeCamelotKey(nextKey);
        if (current == null || next == null)
            return true;

        if (current.Value.Number == next.Value.Number)
            return true;

        return current.Value.Mode == next.Value.Mode &&
               Math.Abs(current.Value.Number - next.Value.Number) is 1 or 11;
    }

    public static (int Number, char Mode)? NormalizeCamelotKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var normalized = key.Trim().ToUpperInvariant().Replace(" ", "");
        var match = Regex.Match(normalized, @"^(\d{1,2})([AB])$");
        if (!match.Success)
        {
            normalized = normalized
                .Replace("MINOR", "M")
                .Replace("MIN", "M")
                .Replace("MAJOR", "")
                .Replace("MAJ", "")
                .Replace("-", "")
                .Replace("_", "");

            var isMinor = normalized.EndsWith("M", StringComparison.Ordinal);
            var root = isMinor ? normalized[..^1] : normalized;
            return (root, isMinor) switch
            {
                ("G#" or "AB", true) => (1, 'A'),
                ("D#" or "EB", true) => (2, 'A'),
                ("A#" or "BB", true) => (3, 'A'),
                ("F", true) => (4, 'A'),
                ("C", true) => (5, 'A'),
                ("G", true) => (6, 'A'),
                ("D", true) => (7, 'A'),
                ("A", true) => (8, 'A'),
                ("E", true) => (9, 'A'),
                ("B", true) => (10, 'A'),
                ("F#" or "GB", true) => (11, 'A'),
                ("C#" or "DB", true) => (12, 'A'),
                ("B" or "CB", false) => (1, 'B'),
                ("F#" or "GB", false) => (2, 'B'),
                ("C#" or "DB", false) => (3, 'B'),
                ("G#" or "AB", false) => (4, 'B'),
                ("D#" or "EB", false) => (5, 'B'),
                ("A#" or "BB", false) => (6, 'B'),
                ("F", false) => (7, 'B'),
                ("C", false) => (8, 'B'),
                ("G", false) => (9, 'B'),
                ("D", false) => (10, 'B'),
                ("A", false) => (11, 'B'),
                ("E", false) => (12, 'B'),
                _ => null
            };
        }

        var number = int.Parse(match.Groups[1].Value);
        if (number < 1 || number > 12)
            return null;

        return (number, match.Groups[2].Value[0]);
    }
}
