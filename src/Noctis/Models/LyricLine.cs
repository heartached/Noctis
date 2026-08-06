using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.Models;

/// <summary>
/// Which duet voice sings a line (iTunes/Gramophone ELRC "v1:"/"v2:"/"v3:" markers).
/// Default covers unmarked lines AND "v1:" — with no opposing voice a lone v1 renders
/// exactly like a plain line (Gramophone downgrades solitary Voice1 the same way).
/// Voice2 right-aligns the line block; Group ("v3:", both singers) centers it.
/// </summary>
public enum LyricVoice
{
    Default,
    Voice2,
    Group,
}

/// <summary>
/// Represents a single line of lyrics, optionally with a timestamp for synced (LRC) lyrics.
/// </summary>
public partial class LyricLine : ObservableObject
{
    /// <summary>Timestamp when this line should be highlighted (null for unsynced lyrics).</summary>
    public TimeSpan? Timestamp { get; set; }

    /// <summary>The lyrics text for this line.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Whether this line is currently active (highlighted during playback).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// Display opacity based on distance from active line (1.0=active, 0.25=next, 0.0=hidden).
    /// Defaults to 1.0 so all lines are visible before sync starts and for unsynced lyrics.
    /// </summary>
    [ObservableProperty]
    private double _lineOpacity = 1.0;

    /// <summary>Whether this line should accept pointer/click events (false when opacity is 0).</summary>
    [ObservableProperty]
    private bool _isClickable = true;

    /// <summary>
    /// Gaussian blur radius applied to inactive lines (depth-of-field, Apple Music style).
    /// Driven by distance from the active line. 0 on the active line; rises with distance.
    /// </summary>
    [ObservableProperty]
    private double _blurRadius;

    /// <summary>Whether this line is the intro placeholder ("...").</summary>
    public bool IsIntroPlaceholder { get; set; }

    /// <summary>Duet voice for this line (Apple Music duet layout). Views bind through the LyricVoice* converters.</summary>
    public LyricVoice Voice { get; set; }

    /// <summary>Whether these lyrics are synced (have timestamps).</summary>
    public bool IsSynced => Timestamp.HasValue;

    /// <summary>Line end timestamp (Lyricsfile). Null when unknown; used to bound the last word's highlight.</summary>
    public TimeSpan? EndTimestamp { get; set; }

    /// <summary>Optional per-word timing for karaoke-style highlighting. Null when only line-level sync is available.</summary>
    public IReadOnlyList<WordTiming>? Words
    {
        get => _words;
        set
        {
            _words = value;
            ComputeWordEmphasis(_words, EndTimestamp);
        }
    }
    private IReadOnlyList<WordTiming>? _words;

    /// <summary>
    /// Background vocals / adlibs (Apple Music "x-bg") sung alongside or after this
    /// line — rendered as a smaller karaoke row under the main words. Null when the
    /// line has none.
    /// </summary>
    public IReadOnlyList<WordTiming>? BackgroundWords
    {
        get => _backgroundWords;
        set
        {
            _backgroundWords = value;
            ComputeWordEmphasis(_backgroundWords, BackgroundEndTimestamp);
        }
    }
    private IReadOnlyList<WordTiming>? _backgroundWords;

    /// <summary>End of the background vocal; bounds its last word's highlight. Set before <see cref="BackgroundWords"/>.</summary>
    public TimeSpan? BackgroundEndTimestamp { get; set; }

    /// <summary>AMLL's shouldEmphasize floor: a held note is at least 1s of vocal.</summary>
    private const double EmphasisFloorMs = 1000;

    /// <summary>Adaptive-threshold cap: a multi-second note is a hold no matter the cadence.</summary>
    private const double EmphasisCapMs = 2500;

    /// <summary>A held word must stand out from its line's own cadence by this factor.</summary>
    private const double EmphasisCadenceFactor = 1.5;

    private static void ComputeWordEmphasis(IReadOnlyList<WordTiming>? words, TimeSpan? lineEnd)
    {
        if (words == null || words.Count == 0) return;

        // Resolve effective durations first. ELRC word ends are the NEXT word's
        // start, so every duration silently includes the rest that follows the
        // word — a fixed 1s gate emphasized nearly every word of a slow song.
        var durations = new double[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var end = w.End ?? (i + 1 < words.Count ? words[i + 1].Start : lineEnd);
            durations[i] = end.HasValue ? (end.Value - w.Start).TotalMilliseconds : 0;
        }

        // Adaptive gate: a held word must beat 1.5× the line's median word duration,
        // floored at AMLL's 1s and capped at 2.5s (an unambiguous hold fires even in
        // a uniformly slow line). This keeps ballads from glowing on every word.
        var threshold = Math.Clamp(
            EmphasisCadenceFactor * Median(durations), EmphasisFloorMs, EmphasisCapMs);

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            w.HeldDurationMs = durations[i];
            var text = w.Text.Trim();
            // AMLL shouldEmphasize size rule: CJK cells (one character each, the
            // parser never merges them) gate on duration alone; Latin words must be
            // 2–7 characters — longer merged words span >1s at a normal pace, and a
            // single letter reads wrong under a whole-word glow.
            var sizeOk = text.Length > 0
                && (IsCjkish(text) || (text.Length >= 2 && text.Length <= 7));
            w.IsEmphasis = sizeOk && durations[i] >= threshold;
        }
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // Matches the ELRC parser's CJK boundary (chars from the CJK radicals block up).
    private static bool IsCjkish(string text)
    {
        foreach (var c in text)
            if (c >= '⺀') return true;
        return false;
    }

    /// <summary>True when the line has word-level timing data.</summary>
    public bool HasWords => Words != null && Words.Count > 0;

    /// <summary>True when the line carries a word-timed background vocal.</summary>
    public bool HasBackgroundWords => BackgroundWords != null && BackgroundWords.Count > 0;

    /// <summary>True when the view should render the background-vocal karaoke row.</summary>
    public bool ShowBackgroundWords => HasBackgroundWords && !IsIntroPlaceholder;

    /// <summary>
    /// True when the line's entire content is a background vocal (e.g. a TTML paragraph
    /// holding only an x-bg span). <see cref="Text"/> then carries the bg text for the
    /// Unsync tab, but the main line layer must not render it — only the small bg row.
    /// </summary>
    public bool IsBackgroundOnly { get; set; }

    /// <summary>
    /// True when the view should render the per-word karaoke layer. Word-timed lines
    /// render this layer whether active or not, so the wrap geometry never changes
    /// when a line activates (the old TextBlock↔WrapPanel swap caused visible reflow).
    /// </summary>
    public bool ShowWords => HasWords && !IsIntroPlaceholder;

    /// <summary>True when the view should render the normal line-level text (not intro, no word timings, not a bg-only line).</summary>
    public bool ShowLineText => !IsIntroPlaceholder && !HasWords && !IsBackgroundOnly;

    /// <summary>Index of the currently-singing word in <see cref="Words"/>. -1 = before line, Words.Count = after line.</summary>
    [ObservableProperty]
    private int _currentWordIndex = -1;

    partial void OnCurrentWordIndexChanged(int value) => ApplyWordIndex(Words, value);

    /// <summary>Index of the currently-singing background word. -1 = before, count = after.</summary>
    [ObservableProperty]
    private int _backgroundWordIndex = -1;

    partial void OnBackgroundWordIndexChanged(int value) => ApplyWordIndex(BackgroundWords, value);

    private static void ApplyWordIndex(IReadOnlyList<WordTiming>? words, int value)
    {
        if (words == null) return;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var isCurrent = i == value;
            var isPast = i < value;
            if (w.IsCurrent != isCurrent) w.IsCurrent = isCurrent;
            if (w.IsPast != isPast) w.IsPast = isPast;

            // Snap Progress for words outside the band's reach; the VM drives the
            // current word AND its immediate neighbours per tick (the feathered
            // edge straddles token boundaries, so ±1 words carry live pre-roll /
            // overshoot values — see KaraokeSweep.BandProgress).
            var snap = isPast
                ? KaraokeSweep.InertPast
                : (isCurrent ? w.Progress : KaraokeSweep.InertFuture);
            if (w.Progress != snap) w.Progress = snap;
        }
    }
}
