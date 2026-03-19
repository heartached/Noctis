namespace Noctis.Models;

/// <summary>
/// Pre-computed album and artist indexes, cached to avoid expensive
/// LINQ grouping/sorting and File.Exists checks on every startup.
/// Stored as %APPDATA%\Noctis\indexes.json.
/// </summary>
public class LibraryIndexCache
{
    /// <summary>Number of tracks when the cache was built — quick validation check.</summary>
    public int TrackCount { get; set; }

    /// <summary>XOR hash of all track IDs — detects additions/removals without comparing full lists.</summary>
    public string TrackIdHash { get; set; } = "";

    /// <summary>Cached album metadata with ordered track ID lists.</summary>
    public List<CachedAlbumEntry> Albums { get; set; } = new();

    /// <summary>Cached artist aggregations.</summary>
    public List<Artist> Artists { get; set; } = new();
}

/// <summary>
/// Lightweight album data for the index cache.
/// Stores track IDs in display order instead of full Track objects.
/// </summary>
public class CachedAlbumEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public int Year { get; set; }
    public string Genre { get; set; } = "";
    public int TrackCount { get; set; }
    public long TotalDurationTicks { get; set; }
    public string? ArtworkPath { get; set; }

    /// <summary>Track IDs in disc→track number order.</summary>
    public List<Guid> TrackIds { get; set; } = new();
}
