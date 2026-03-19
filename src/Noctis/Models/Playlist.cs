namespace Noctis.Models;

/// <summary>
/// A user-created playlist containing an ordered list of track references.
/// Persisted to JSON in %APPDATA%\Noctis\playlists\.
/// </summary>
public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-editable playlist name.</summary>
    public string Name { get; set; } = "New Playlist";

    /// <summary>Optional description for the playlist.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Playlist cover color (hex format, e.g., "#FF5733").</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>Optional custom cover art file path.</summary>
    public string? CoverArtPath { get; set; }

    /// <summary>Ordered list of track IDs. Resolved against the library at load time.</summary>
    public List<Guid> TrackIds { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this is a smart (rule-based) playlist vs a manual playlist.</summary>
    public bool IsSmartPlaylist { get; set; }

    /// <summary>Rules for smart playlists. Empty for manual playlists.</summary>
    public List<SmartPlaylistRule> Rules { get; set; } = new();

    /// <summary>True = match ALL rules (AND); False = match ANY rule (OR).</summary>
    public bool MatchAll { get; set; } = true;

    /// <summary>Optional track limit. Null = no limit.</summary>
    public int? LimitCount { get; set; }

    /// <summary>Sort order when LimitCount is set.</summary>
    public SmartPlaylistSortBy? SortBy { get; set; }

    /// <summary>Predefined color palette for playlist covers.</summary>
    public static readonly string[] ColorPalette = new[]
    {
        "#FF6B6B", // Red
        "#4ECDC4", // Teal
        "#45B7D1", // Blue
        "#FFA07A", // Light Salmon
        "#98D8C8", // Mint
        "#F7DC6F", // Yellow
        "#BB8FCE", // Purple
        "#85C1E2", // Sky Blue
        "#F8B500", // Orange
        "#52B788", // Green
        "#E07A5F", // Coral
        "#81B29A", // Sage
        "#F4A261", // Sandy Brown
        "#E76F51", // Terracotta
        "#8E44AD", // Deep Purple
        "#3498DB", // Bright Blue
        "#1ABC9C", // Turquoise
        "#F39C12", // Amber
        "#E74C3C", // Crimson
        "#95A5A6"  // Gray
    };

    /// <summary>Gets a random color from the palette.</summary>
    public static string GetRandomColor()
    {
        return ColorPalette[Random.Shared.Next(ColorPalette.Length)];
    }
}
