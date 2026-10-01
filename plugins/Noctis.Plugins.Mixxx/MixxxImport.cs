using System.Text;

namespace Noctis.Plugins.Mixxx;

/// <summary>What an import would change, and how the library compared with Mixxx's.</summary>
/// <param name="Updates">BPM/key to hand to <see cref="ITrackAnalysisWriter"/>.</param>
/// <param name="Matched">Noctis tracks found in Mixxx with a BPM or key.</param>
/// <param name="AlreadySet">Matched tracks that need nothing (values present, or equal).</param>
/// <param name="NotInMixxx">Noctis files Mixxx has no BPM or key for.</param>
public sealed record ImportPlan(IReadOnlyList<TrackAnalysisUpdate> Updates, int Matched, int AlreadySet, int NotInMixxx);

/// <summary>
/// Turns Mixxx's analysis into Noctis values and matches tracks by file path. Pure: no I/O.
/// Matching is exact on the normalized path; there is no fuzzy fallback (title/artist
/// guesses could stamp one track's tempo onto another).
/// </summary>
public static class MixxxImport
{
    /// <summary>The pitch names Noctis's own key analysis uses (KeyDetector): sharps only.</summary>
    private static readonly string[] PitchNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>
    /// Mixxx's key_id (keys.proto ChromaticKey: 1–12 = C…B major, 13–24 = C…B minor, by
    /// semitone; 0 = invalid) in the notation Noctis's analysis writes and AutoMix/Radio read:
    /// "C# minor". Null for anything else.
    /// </summary>
    public static string? KeyName(int keyId) => keyId switch
    {
        >= 1 and <= 12 => PitchNames[keyId - 1] + " major",
        >= 13 and <= 24 => PitchNames[keyId - 13] + " minor",
        _ => null,
    };

    /// <summary>Mixxx's BPM (a real number, 0 = none) as Noctis's whole-number BPM, rounded the way
    /// Noctis's own tempo analysis rounds (Math.Round). 0 when there is no usable tempo.</summary>
    public static int Bpm(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm <= 0) return 0;
        var rounded = Math.Round(bpm);
        return rounded is >= 1 and <= 999 ? (int)rounded : 0;
    }

    /// <summary>
    /// A path in the form both sides can be compared in: Unicode NFC, and on Windows forward
    /// slashes (Mixxx stores Qt paths, "C:/Music/a.mp3"; Noctis stores "C:\Music\a.mp3") without
    /// a "\\?\" prefix. Case is left alone; the caller picks the comparer.
    /// </summary>
    public static string NormalizePath(string? path, bool windows)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string p;
        try { p = path.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { p = path; } // a stray surrogate in a file name: compare it as is
        if (windows)
        {
            p = p.Replace('\\', '/');
            if (p.StartsWith("//?/", StringComparison.Ordinal)) p = p[4..];
        }
        return p;
    }

    /// <summary>
    /// Matches <paramref name="tracks"/> to <paramref name="mixxx"/> by file path. Without
    /// <paramref name="overwrite"/> only empty values are filled. Two Mixxx rows that land on the
    /// same normalized path with different values cancel out (no guessing which is right).
    /// </summary>
    /// <param name="windows">Normalize separators the Windows way.</param>
    /// <param name="ignoreCase">Compare paths case-insensitively (Windows, macOS).</param>
    public static ImportPlan Plan(IReadOnlyList<TrackInfo> tracks, IReadOnlyList<MixxxTrack> mixxx, bool overwrite, bool windows, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var byPath = new Dictionary<string, (int Bpm, string? Key)>(comparer);
        var conflicting = new HashSet<string>(comparer);
        foreach (var row in mixxx)
        {
            var value = (Bpm: Bpm(row.Bpm), Key: KeyName(row.KeyId));
            if (value.Bpm == 0 && value.Key is null) continue;
            var path = NormalizePath(row.Location, windows);
            if (path.Length == 0 || conflicting.Contains(path)) continue;
            if (byPath.TryGetValue(path, out var seen))
            {
                if (seen != value) { byPath.Remove(path); conflicting.Add(path); }
                continue;
            }
            byPath[path] = value;
        }

        var updates = new List<TrackAnalysisUpdate>();
        int matched = 0, alreadySet = 0, notInMixxx = 0;
        foreach (var track in tracks)
        {
            // Streams and remote sources have no local file Mixxx could have analysed.
            if (string.IsNullOrEmpty(track.FilePath) || track.FilePath.Contains("://", StringComparison.Ordinal)) continue;
            if (!byPath.TryGetValue(NormalizePath(track.FilePath, windows), out var found))
            {
                notInMixxx++;
                continue;
            }
            matched++;
            int? bpm = found.Bpm > 0 && (overwrite ? found.Bpm != track.Bpm : track.Bpm <= 0) ? found.Bpm : null;
            var key = found.Key is not null && (overwrite
                ? !string.Equals(found.Key, track.MusicalKey, StringComparison.Ordinal)
                : string.IsNullOrWhiteSpace(track.MusicalKey)) ? found.Key : null;
            if (bpm is null && key is null) alreadySet++;
            else updates.Add(new TrackAnalysisUpdate(track.Id, bpm, key));
        }
        return new ImportPlan(updates, matched, alreadySet, notInMixxx);
    }

    /// <summary>The notice after an import.</summary>
    public static string Summary(int changed, ImportPlan plan)
    {
        if (plan.Matched == 0)
            return plan.NotInMixxx == 0
                ? "Your library has no local files to match with Mixxx."
                : $"None of your {plan.NotInMixxx:N0} files has a BPM or key in Mixxx (matched by file path).";
        var parts = new List<string> { $"BPM/key from Mixxx set on {changed:N0} track{(changed == 1 ? "" : "s")}" };
        var kept = plan.Matched - changed;
        if (kept > 0) parts.Add($"{kept:N0} already had {(kept == 1 ? "it" : "them")}");
        if (plan.NotInMixxx > 0) parts.Add($"{plan.NotInMixxx:N0} not in Mixxx");
        return string.Join(" · ", parts) + ".";
    }
}
