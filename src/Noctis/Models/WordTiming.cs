using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.Models;

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

    /// <summary>True once the playhead has advanced past this word.</summary>
    [ObservableProperty]
    private bool _isPast;

    /// <summary>True while this word is currently being sung.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// Reveal progress driving the AMLL-style left-to-right colour sweep. Runs
    /// slightly past [0..1] on the words neighbouring the current one, so the
    /// feathered edge can straddle token boundaries; words out of the band's reach
    /// rest at <see cref="KaraokeSweep.InertFuture"/> / <see cref="KaraokeSweep.InertPast"/>.
    /// </summary>
    [ObservableProperty]
    private double _progress = KaraokeSweep.InertFuture;
}
