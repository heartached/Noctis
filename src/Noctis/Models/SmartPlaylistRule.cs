namespace Noctis.Models;

/// <summary>Fields that a smart playlist rule can filter on.</summary>
public enum RuleField
{
    Title,
    Artist,
    Album,
    Genre,
    Composer,
    Codec,
    Year,
    PlayCount,
    Duration,
    DateAdded,
    LastPlayed,
    IsFavorite,
    IsLossless,
    IsExplicit
}

/// <summary>Comparison operators for smart playlist rules.</summary>
public enum RuleOperator
{
    // String
    Contains,
    Equals,
    StartsWith,
    EndsWith,
    DoesNotContain,

    // Numeric / Date
    GreaterThan,
    LessThan,
    Between,

    // Boolean
    IsTrue,
    IsFalse,

    // Date-specific
    Before,
    After,
    InLastNDays
}

/// <summary>Sort order options for limited smart playlists.</summary>
public enum SmartPlaylistSortBy
{
    MostPlayed,
    LeastPlayed,
    RecentlyAdded,
    RecentlyPlayed,
    Title,
    Artist,
    Random
}

/// <summary>A single filter rule in a smart playlist.</summary>
public class SmartPlaylistRule
{
    public RuleField Field { get; set; }
    public RuleOperator Operator { get; set; }

    /// <summary>Primary value for comparison (stored as string for JSON simplicity).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Secondary value for "Between" operator (upper bound).</summary>
    public string? Value2 { get; set; }
}
