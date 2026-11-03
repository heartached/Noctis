namespace Noctis.Plugins.Chords.Analysis;

/// <summary>The chord kinds the analysis tells apart. <see cref="None"/> = no chord (silence).</summary>
public enum ChordQuality
{
    None = 0,
    Major = 1,
    Minor = 2,
    Dominant7 = 3,
    Major7 = 4,
    Minor7 = 5,
}

/// <summary>A chord: root pitch class (0 = C … 11 = B) and kind.</summary>
public readonly record struct Chord(int Root, ChordQuality Quality)
{
    public static readonly Chord NoChord = new(0, ChordQuality.None);

    public bool IsNone => Quality == ChordQuality.None;

    /// <summary>The triad under it (G7 → G, Am7 → Am, Cmaj7 → C): what "sevenths off" shows.</summary>
    public Chord Triad => Quality switch
    {
        ChordQuality.Dominant7 or ChordQuality.Major7 => new Chord(Root, ChordQuality.Major),
        ChordQuality.Minor7 => new Chord(Root, ChordQuality.Minor),
        _ => this,
    };

    /// <summary>Pitch classes of the chord, root first.</summary>
    public int[] Notes => Quality switch
    {
        ChordQuality.Major => new[] { Root, (Root + 4) % 12, (Root + 7) % 12 },
        ChordQuality.Minor => new[] { Root, (Root + 3) % 12, (Root + 7) % 12 },
        ChordQuality.Dominant7 => new[] { Root, (Root + 4) % 12, (Root + 7) % 12, (Root + 10) % 12 },
        ChordQuality.Major7 => new[] { Root, (Root + 4) % 12, (Root + 7) % 12, (Root + 11) % 12 },
        ChordQuality.Minor7 => new[] { Root, (Root + 3) % 12, (Root + 7) % 12, (Root + 10) % 12 },
        _ => Array.Empty<int>(),
    };

    /// <summary>"C", "F#m", "Bb7", "Ebmaj7", "Am7"; "N.C." for no chord. Flats when <paramref name="flats"/>.</summary>
    public string Name(bool flats = false) => Quality switch
    {
        ChordQuality.None => "N.C.",
        ChordQuality.Major => NoteNames.NameOf(Root, flats),
        ChordQuality.Minor => NoteNames.NameOf(Root, flats) + "m",
        ChordQuality.Dominant7 => NoteNames.NameOf(Root, flats) + "7",
        ChordQuality.Major7 => NoteNames.NameOf(Root, flats) + "maj7",
        ChordQuality.Minor7 => NoteNames.NameOf(Root, flats) + "m7",
        _ => "?",
    };

    public override string ToString() => Name();

    /// <summary>Parses what <see cref="Name"/> writes ("C", "Bbm7", "N.C."); false for anything else.</summary>
    public static bool TryParse(string? text, out Chord chord)
    {
        chord = NoChord;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text == "N.C.") return true;
        if (!NoteNames.TryParse(text, out var root, out var used)) return false;
        var rest = text[used..];
        ChordQuality? q = rest switch
        {
            "" => ChordQuality.Major,
            "m" => ChordQuality.Minor,
            "7" => ChordQuality.Dominant7,
            "maj7" => ChordQuality.Major7,
            "m7" => ChordQuality.Minor7,
            _ => null,
        };
        if (q is null) return false;
        chord = new Chord(root, q.Value);
        return true;
    }
}

/// <summary>Note names.</summary>
public static class NoteNames
{
    private static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] Flats = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    public static string NameOf(int pitchClass, bool flats) => (flats ? Flats : Sharps)[((pitchClass % 12) + 12) % 12];

    /// <summary>Reads a note name at the start of <paramref name="text"/> ("C#", "Bb", "E").</summary>
    public static bool TryParse(string text, out int pitchClass, out int length)
    {
        pitchClass = 0;
        length = 0;
        if (text.Length == 0) return false;
        var basePc = text[0] switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1 };
        if (basePc < 0) return false;
        length = 1;
        if (text.Length > 1 && (text[1] == '#' || text[1] == 'b'))
        {
            basePc += text[1] == '#' ? 1 : -1;
            length = 2;
        }
        pitchClass = (basePc + 12) % 12;
        return true;
    }
}

/// <summary>The estimated key: tonic pitch class and mode.</summary>
public readonly record struct MusicalKey(int Tonic, bool Minor)
{
    /// <summary>Spell the key's chords with flats (F, Bb, Eb … major; D, G, C … minor).</summary>
    public bool UsesFlats => Minor
        ? Tonic is 2 or 7 or 0 or 5 or 10 or 3
        : Tonic is 5 or 10 or 3 or 8 or 1 or 6;

    /// <summary>"C major", "F# minor", "Bb major".</summary>
    public string Name => NoteNames.NameOf(Tonic, UsesFlats) + (Minor ? " minor" : " major");

    /// <summary>The key's tonic chord (C for C major, Am for A minor).</summary>
    public Chord TonicChord => new(Tonic, Minor ? ChordQuality.Minor : ChordQuality.Major);

    public override string ToString() => Name;
}

/// <summary>One stretch of the song with one chord, in seconds from the start.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends (the next one's start).</param>
/// <param name="Chord">The chord.</param>
/// <param name="Confidence">0..1: how much better this chord fitted than the runner-up, averaged.</param>
public sealed record ChordSegment(double Start, double End, Chord Chord, double Confidence)
{
    public double Length => End - Start;
}

/// <summary>A song's chord estimate.</summary>
/// <param name="Segments">Chords in time order, covering 0..<paramref name="Duration"/> without gaps.</param>
/// <param name="Key">The estimated key, or null for a song with no chords found.</param>
/// <param name="Duration">Seconds of audio analysed.</param>
/// <param name="TuningCents">How far the recording sits from A = 440 Hz, in cents (-50..50).</param>
/// <param name="Sevenths">Whether seventh chords were looked for.</param>
public sealed record ChordAnalysis(IReadOnlyList<ChordSegment> Segments, MusicalKey? Key, double Duration, double TuningCents, bool Sevenths)
{
    /// <summary>Spell chords with flats (from the key).</summary>
    public bool Flats => Key?.UsesFlats ?? false;

    /// <summary>The segment playing at <paramref name="seconds"/>, or -1 before the first / past the end.</summary>
    public int IndexAt(double seconds)
    {
        int lo = 0, hi = Segments.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var s = Segments[mid];
            if (seconds < s.Start) hi = mid - 1;
            else if (seconds >= s.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>The chord at <paramref name="seconds"/> (no chord outside the song).</summary>
    public Chord ChordAt(double seconds) => IndexAt(seconds) is var i and >= 0 ? Segments[i].Chord : Chord.NoChord;
}
