using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>True once the playhead has advanced past this word.</summary>
    [ObservableProperty]
    private bool _isPast;

    /// <summary>True while this word is currently being sung.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// Reveal progress in [0..1] — drives the AMLL-style left-to-right colour sweep
    /// across the word as it is sung. Past = 1, future = 0, current = fraction of elapsed.
    /// </summary>
    [ObservableProperty]
    private double _progress;
}
