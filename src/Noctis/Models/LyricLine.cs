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

    /// <summary>Whether these lyrics are synced (have timestamps).</summary>
    public bool IsSynced => Timestamp.HasValue;
}
