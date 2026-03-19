namespace Noctis.Models;

/// <summary>
/// Represents an artist, aggregated from the library's track data.
/// </summary>
public class Artist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Unknown Artist";
    public int AlbumCount { get; set; }
    public int TrackCount { get; set; }
    public string? ImagePath { get; set; }

    public override string ToString() => Name;
}

/// <summary>Base type for the flat virtualized artist list.</summary>
public abstract class ArtistListItem { }

/// <summary>Letter header row in the flat artist list.</summary>
public class ArtistHeaderItem : ArtistListItem
{
    public char Letter { get; init; }
}

/// <summary>Artist data row in the flat artist list.</summary>
public class ArtistDataItem : ArtistListItem
{
    public Artist Artist { get; init; } = null!;
}
