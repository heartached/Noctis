namespace Noctis.Models;

/// <summary>
/// Represents a single bar in a horizontal bar chart.
/// Used by StatisticsViewModel for Top Tracks, Artists, Albums, Genres, etc.
/// </summary>
public class StatItem
{
    /// <summary>Display label (e.g., artist name, genre name).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The numeric value (e.g., play count, track count).</summary>
    public int Value { get; set; }

    /// <summary>Percentage of the maximum value in the set (0.0 - 1.0).</summary>
    public double Percentage { get; set; }

    /// <summary>Formatted display string for the value (e.g., "42 plays").</summary>
    public string ValueLabel { get; set; } = string.Empty;

    /// <summary>Optional secondary label (e.g., artist name under track title).</summary>
    public string SubLabel { get; set; } = string.Empty;
}
