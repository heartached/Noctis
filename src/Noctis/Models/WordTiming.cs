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

    /// <summary>Word start time.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>Word end time. Null when not provided; treated as the next word's Start (or line end).</summary>
    public TimeSpan? End { get; init; }

    /// <summary>True once the playhead has advanced past this word.</summary>
    [ObservableProperty]
    private bool _isPast;

    /// <summary>True while this word is currently being sung.</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
