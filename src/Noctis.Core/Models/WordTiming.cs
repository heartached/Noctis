using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.Models;

/// <summary>
/// One syllable inside a merged <see cref="WordTiming"/>: its own timing window plus
/// how many characters of the merged word it contributes. Lets the sweep follow the
/// original per-syllable timing across a word that renders as one unbreakable cell.
/// </summary>
/// <param name="Start">Syllable start time.</param>
/// <param name="End">Syllable end time; null falls back to the next syllable's start.</param>
/// <param name="Length">Characters this syllable contributes to the merged word.</param>
public readonly record struct WordSyllable(TimeSpan Start, TimeSpan? End, int Length);

/// <summary>
/// A single word inside a <see cref="LyricLine"/> with karaoke-style timing.
/// Produced by the Lyricsfile (YAML, LRCGET v2.0+) parser.
///
/// Holds observable <see cref="IsPast"/> / <see cref="IsCurrent"/> state so the view
/// can bind per-word styling without a value converter or element-index binding.
/// The state is updated by <see cref="LyricLine"/> whenever its CurrentWordIndex moves.
/// </summary>
public partial class WordTiming : ObservableObject
{
    /// <summary>Word text. Per the Lyricsfile spec, trailing spaces are preserved except on the final word of a line.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Text the sweep overlay renders: <see cref="Text"/> minus trailing whitespace.
    /// The sweep mask is relative to the overlay's bounds, so if the trailing space is
    /// included the edge spends the tail of every word's duration crossing invisible
    /// whitespace — a visible stall at the end of each word. Leading whitespace must
    /// stay so the overlay's glyphs keep lining up with the base layer.
    /// </summary>
    public string SweepText => Text.TrimEnd();

    /// <summary>Word start time.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>Word end time. Null when not provided; treated as the next word's Start (or line end).</summary>
    public TimeSpan? End { get; init; }

    /// <summary>
    /// Syllable breakdown for words built by joining several timed segments (Apple
    /// TTML splits "compromise" into com/pro/mise). Null for ordinary single-segment
    /// words. The cell renders unbroken either way; this only lets the sweep advance
    /// on each syllable's own clock instead of linearly across the whole word.
    /// </summary>
    public IReadOnlyList<WordSyllable>? Syllables { get; init; }

    /// <summary>
    /// True for long-held words (slow vocal passages). Drives the Apple Music-style
    /// extra swell + glow while the word is being sung. Computed once by
    /// <see cref="LyricLine"/> when its word list is assigned.
    /// </summary>
    public bool IsEmphasis { get; set; }

    /// <summary>
    /// Resolved sung duration in milliseconds (same end resolution as the emphasis
    /// gate). Set by <see cref="LyricLine"/> alongside <see cref="IsEmphasis"/>; the
    /// held-note glow envelope scales its intensity from this.
    /// </summary>
    public double HeldDurationMs { get; set; }

    /// <summary>True for the last word of its line (or layer). AMLL gives that word a
    /// stronger, longer held-note swell. Set by <see cref="LyricLine"/>.</summary>
    public bool IsLastInLine { get; set; }

    /// <summary>
    /// True when a rest (or the line start) comes before this word, so the sweep's soft
    /// edge must enter from fully dark at the word's start instead of already straddling
    /// it from the previous word. Set by <see cref="LyricLine"/>.
    /// </summary>
    public bool SweepLeadIn { get; set; }

    /// <summary>
    /// True when a rest (or the line end) follows this word, so the soft edge must have
    /// fully crossed the word by its end time — nothing would carry it on afterwards,
    /// and the line deactivating snapped the half-lit last letter to lit. Set by
    /// <see cref="LyricLine"/>.
    /// </summary>
    public bool SweepTrailOut { get; set; }

    /// <summary>
    /// Time since the word started (ms) while its held-note letter animation is in
    /// flight (<see cref="EmphasisMotion"/>), NaN at rest. Driven per frame by
    /// <see cref="LyricsTimeline"/> for <see cref="IsEmphasis"/> words only.
    /// </summary>
    [ObservableProperty]
    private double _emphasisMs = double.NaN;

    /// <summary>
    /// How far the word has floated up as it is sung (<see cref="WordFloat"/>): 0 at rest,
    /// 1 at the full lift. Driven per frame by <see cref="LyricsTimeline"/> while the word
    /// rises and while its line settles back; 0 on lines that are not word-synced.
    /// </summary>
    [ObservableProperty]
    private double _lift;

    /// <summary>The word's line reads right to left (<see cref="LyricLine.SetRightToLeft"/>):
    /// the sweep fills it from the right and it is drawn in one piece.</summary>
    [ObservableProperty]
    private bool _isRightToLeft;

    /// <summary>Letters the held-note animation staggers across (text elements of the
    /// visible word) — shared by the timeline's window and the renderer.</summary>
    public int EmphasisCharCount =>
        _emphasisCharCount ??= new System.Globalization.StringInfo(Text.Trim()).LengthInTextElements;
    private int? _emphasisCharCount;

    /// <summary>Held-note animation strength for this word (AMLL params).</summary>
    public EmphasisParams EmphasisParams => EmphasisMotion.For(HeldDurationMs, IsLastInLine);

    /// <summary>True once the playhead has advanced past this word.</summary>
    [ObservableProperty]
    private bool _isPast;

    /// <summary>True while this word is currently being sung.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// Reveal progress driving the AMLL-style left-to-right colour sweep: where the
    /// line's one sweep front is, through this word (0..1). On the words neighbouring
    /// the current one it runs past [0..1] (the same front measured from them), so the
    /// feathered edge can straddle token boundaries; words out of the band's reach
    /// rest at <see cref="KaraokeSweep.InertFuture"/> / <see cref="KaraokeSweep.InertPast"/>.
    /// </summary>
    [ObservableProperty]
    private double _progress = KaraokeSweep.InertFuture;
}
