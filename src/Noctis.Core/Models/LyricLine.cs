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
    [NotifyPropertyChangedFor(nameof(IsSweepFocused))]
    private bool _isActive;

    /// <summary>
    /// True while the lyric list glides into this line ahead of its activation
    /// (LyricsTimeline.Lead): the focus ramp already gives it full opacity, so its words
    /// must already show the unsung look — the inactive look draws unsung text at full
    /// brightness, which read as the whole line lit white before its first word.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSweepFocused))]
    private bool _isLeading;

    /// <summary>The word layers show the karaoke look (dim unsung text, the sweep lit on
    /// top): the line is active, or the list is leading into it.</summary>
    public bool IsSweepFocused => IsActive || IsLeading;

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

    /// <summary>Whether this line is a "..." dots placeholder: the intro before the first
    /// lyric, or an instrumental interlude (<see cref="IsInterlude"/>).</summary>
    public bool IsIntroPlaceholder { get; set; }

    /// <summary>A placeholder inserted into a long instrumental gap mid-song
    /// (LrcParser.InsertInterludePlaceholders); <see cref="IsIntroPlaceholder"/> is set too,
    /// so every consumer that skips the intro dots skips these.</summary>
    public bool IsInterlude { get; set; }

    /// <summary>
    /// How far through its gap an active dots placeholder is, 0..1 (its start to the next
    /// line's start); NaN while it is not active. The dots fill one after another with it
    /// (LyricsTimeline drives it every frame).
    /// </summary>
    [ObservableProperty]
    private double _interludeProgress = double.NaN;

    /// <summary>Duet voice for this line (Apple Music duet layout). Views bind through the LyricVoice* converters.</summary>
    public LyricVoice Voice { get; set; }

    /// <summary>
    /// The line reads right to left (plugin API 1.5: a lyrics effect decides it): its word rows
    /// run from the right, it sits against the right edge and its words fill from the right.
    /// Set with <see cref="SetRightToLeft"/>, which passes it on to the line's words.
    /// </summary>
    [ObservableProperty]
    private bool _isRightToLeft;

    /// <summary>Sets <see cref="IsRightToLeft"/> on the line and on every word of its layers.</summary>
    public void SetRightToLeft(bool value)
    {
        IsRightToLeft = value;
        Mark(Words);
        Mark(BackgroundWords);
        Mark(TransliterationWords);
        void Mark(IReadOnlyList<WordTiming>? words)
        {
            if (words is null) return;
            foreach (var w in words) w.IsRightToLeft = value;
        }
    }

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

    /// <summary>
    /// Translation of this line (Apple TTML &lt;iTunesMetadata&gt;&lt;translations&gt;, issue #78) —
    /// rendered as static small text under the line. Null when the line has none.
    /// </summary>
    public string? Translation { get; set; }

    /// <summary>
    /// Romanization of this line (Apple TTML &lt;transliterations&gt;) when it carries no word
    /// timing — rendered as static small text under the line. Null when the line has none.
    /// </summary>
    public string? Transliteration { get; set; }

    /// <summary>
    /// Word-timed romanization — rendered as a smaller karaoke row under the line, driven
    /// like <see cref="BackgroundWords"/>. Null when the line has none.
    /// </summary>
    public IReadOnlyList<WordTiming>? TransliterationWords
    {
        get => _transliterationWords;
        set
        {
            _transliterationWords = value;
            ComputeWordEmphasis(_transliterationWords, TransliterationEndTimestamp);
        }
    }
    private IReadOnlyList<WordTiming>? _transliterationWords;

    /// <summary>End of the romanization layer; bounds its last word's highlight. Set before <see cref="TransliterationWords"/>.</summary>
    public TimeSpan? TransliterationEndTimestamp { get; set; }

    /// <summary>AMLL's shouldEmphasize floor: a held note is at least 1s of vocal.</summary>
    internal const double EmphasisFloorMs = 1000;

    /// <summary>
    /// Floor for a mid-line word that ends in punctuation and runs straight into the next
    /// word's start (every ELRC mid-line word: its end IS the next start, so the span
    /// includes any rest). Measured on 50,695 word-timed Latin lines of the AMLL TTML DB,
    /// where true word ends are known: such words are sung for a median 80% of that span
    /// (plain words 100%) — the breath sits after the comma. Asking 1.6 s of them cut the
    /// false held notes by a third (2549 → 1659) for 4% of the real ones.
    /// </summary>
    internal const double EmphasisRestAfterPunctuationMs = 1600;

    /// <summary>Gaps up to this long between one word's end and the next one's start still
    /// carry the sweep's soft edge straight across (TTML phrases have tiny gaps between
    /// words); longer ones are rests the edge must not straddle.</summary>
    internal const double SweepAbutToleranceMs = 80;

    private static void ComputeWordEmphasis(IReadOnlyList<WordTiming>? words, TimeSpan? lineEnd)
    {
        if (words == null || words.Count == 0) return;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var next = i + 1 < words.Count ? words[i + 1] : null;
            var end = w.End ?? next?.Start ?? lineEnd;
            var duration = end.HasValue ? (end.Value - w.Start).TotalMilliseconds : 0;
            w.HeldDurationMs = duration;
            w.IsLastInLine = next == null;

            // Sweep edges: a word after a rest (or first) starts its soft edge from dark,
            // a word before a rest (or last) finishes it by its own end.
            w.SweepLeadIn = i == 0 || !Abuts(words[i - 1], w);
            w.SweepTrailOut = next == null || !Abuts(w, next);

            // AMLL shouldEmphasize: at least 1s, and CJK cells (one character each, the
            // parser never merges them) gate on duration alone while Latin words must be
            // 2–7 characters. No cadence-relative gate: on the AMLL TTML DB it kept
            // precision flat (0.881 vs 0.880) and dropped a fifth of the real held notes
            // in Latin lines (recall 0.80) and over half with CJK (0.47).
            var text = w.Text.Trim();
            var abutsNext = next != null && (w.End == null || w.End.Value >= next.Start);
            var floor = abutsNext && EndsInPunctuation(text) ? EmphasisRestAfterPunctuationMs : EmphasisFloorMs;
            var emphasis = text.Length > 0 && SizeOk(text.Length, IsCjkish(text)) && duration >= floor;

            // AMLL also emphasizes a merged Latin word when one of its syllables is a
            // held note by itself ("No-o-o-o" carries the hold in one syllable).
            if (!emphasis && w.Syllables is { Count: > 1 } syllables && !IsCjkish(text))
            {
                for (int s = 0; s < syllables.Count && !emphasis; s++)
                {
                    var syl = syllables[s];
                    var sylEnd = syl.End ?? (s + 1 < syllables.Count ? syllables[s + 1].Start : end);
                    emphasis = sylEnd.HasValue && SizeOk(syl.Length, false)
                        && (sylEnd.Value - syl.Start).TotalMilliseconds >= EmphasisFloorMs;
                }
            }
            w.IsEmphasis = emphasis;
        }
    }

    private static bool SizeOk(int length, bool cjk) => cjk ? length > 0 : length is >= 2 and <= 7;

    private static bool Abuts(WordTiming a, WordTiming b) =>
        a.End is not { } end || (b.Start - end).TotalMilliseconds <= SweepAbutToleranceMs;

    // Closing punctuation where a singer breathes. Apostrophes and hyphens are elisions
    // ("goin'", "me-e-e"), not rests.
    private static bool EndsInPunctuation(string text) =>
        text.Length > 0 && ",.!?;:…)\"”，。！？；：、）」』".Contains(text[^1]);

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

    /// <summary>True when the line carries a word-timed romanization.</summary>
    public bool HasTransliterationWords => TransliterationWords != null && TransliterationWords.Count > 0;

    /// <summary>True when the line carries a romanization, timed or not.</summary>
    public bool HasTransliteration => HasTransliterationWords || !string.IsNullOrWhiteSpace(Transliteration);

    /// <summary>True when the line carries a translation.</summary>
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    /// <summary>True when the view should render the romanization karaoke row.</summary>
    public bool ShowTransliterationWords => HasTransliterationWords && !IsIntroPlaceholder;

    /// <summary>True when the view should render the untimed romanization text.</summary>
    public bool ShowTransliterationText => HasTransliteration && !HasTransliterationWords && !IsIntroPlaceholder;

    /// <summary>True when the view should render the translation text.</summary>
    public bool ShowTranslation => HasTranslation && !IsIntroPlaceholder;

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

    /// <summary>
    /// The word layer is built for this line (the default). Very long word-timed lyrics keep
    /// it only near the sung line (LyricsViewModel); elsewhere the view draws the line at rest
    /// with one control laid out the same way (KaraokeRestingWords), because a whole
    /// audiobook of word cells froze the page and starved the audio (10-28).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveWords), nameof(ShowLiveWords), nameof(ShowRestingWords))]
    private bool _isWordLayerLive = true;

    /// <summary><see cref="Words"/> while the word layer is built, else null (no cells).</summary>
    public IReadOnlyList<WordTiming>? LiveWords => IsWordLayerLive ? Words : null;

    /// <summary>The view renders the per-word karaoke layer (<see cref="ShowWords"/>, built).</summary>
    public bool ShowLiveWords => ShowWords && IsWordLayerLive;

    /// <summary>The view renders the line's words at rest in one control instead.</summary>
    public bool ShowRestingWords => ShowWords && !IsWordLayerLive;

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

    /// <summary>Index of the currently-singing romanization word. -1 = before, count = after.</summary>
    [ObservableProperty]
    private int _transliterationWordIndex = -1;

    partial void OnTransliterationWordIndexChanged(int value) => ApplyWordIndex(TransliterationWords, value);

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

            // Snap Progress for words outside the band's reach; the timeline drives the
            // current word AND its immediate neighbours per tick (the feathered edge
            // straddles token boundaries, so ±1 words carry the same front's values
            // past [0..1] — see LyricsTimeline.DriveWordLayer).
            var snap = isPast
                ? KaraokeSweep.InertPast
                : (isCurrent ? w.Progress : KaraokeSweep.InertFuture);
            if (w.Progress != snap) w.Progress = snap;
        }
    }
}
