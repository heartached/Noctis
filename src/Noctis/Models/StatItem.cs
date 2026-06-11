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

    /// <summary>1-based position for ranked lists (Top Artists / Top Albums). 0 = unranked.</summary>
    public int Rank { get; set; }
}

/// <summary>One row of the chronological play log on the Statistics page.</summary>
public class PlayLogItem
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string TimeLabel { get; set; } = string.Empty;
    public bool Skipped { get; set; }
}

/// <summary>One hour cell of the listening-by-hour heatmap (0–23).</summary>
public class HourHeatCell
{
    public int Hour { get; set; }
    public int Count { get; set; }

    /// <summary>Bar opacity (0.06 floor – 1.0), relative to the busiest hour.</summary>
    public double Intensity { get; set; }

    /// <summary>Axis label, only populated every few hours to avoid clutter.</summary>
    public string HourLabel { get; set; } = string.Empty;

    public string Tooltip => $"{Count} plays · {Hour:00}:00–{Hour:00}:59";
}
