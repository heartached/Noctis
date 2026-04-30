using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Models;

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

    /// <summary>Whether this line is the intro placeholder ("...").</summary>
    public bool IsIntroPlaceholder { get; set; }

    /// <summary>Whether these lyrics are synced (have timestamps).</summary>
    public bool IsSynced => Timestamp.HasValue;

    /// <summary>Line end timestamp (Lyricsfile). Null when unknown; used to bound the last word's highlight.</summary>
    public TimeSpan? EndTimestamp { get; set; }

    /// <summary>Optional per-word timing for karaoke-style highlighting. Null when only line-level sync is available.</summary>
    public IReadOnlyList<WordTiming>? Words { get; set; }

    /// <summary>True when the line has word-level timing data.</summary>
    public bool HasWords => Words != null && Words.Count > 0;

    /// <summary>True when the view should render per-word karaoke (active line + word timings available).</summary>
    public bool ShowWords => IsActive && HasWords;

    /// <summary>True when the view should render the normal line-level text (not intro, not word mode).</summary>
    public bool ShowLineText => !IsIntroPlaceholder && !ShowWords;

    /// <summary>Index of the currently-singing word in <see cref="Words"/>. -1 = before line, Words.Count = after line.</summary>
    [ObservableProperty]
    private int _currentWordIndex = -1;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWords));
        OnPropertyChanged(nameof(ShowLineText));
    }

    partial void OnCurrentWordIndexChanged(int value)
    {
        if (Words == null) return;
        for (int i = 0; i < Words.Count; i++)
        {
            var w = Words[i];
            var isCurrent = i == value;
            var isPast = i < value;
            if (w.IsCurrent != isCurrent) w.IsCurrent = isCurrent;
            if (w.IsPast != isPast) w.IsPast = isPast;
        }
    }
}
