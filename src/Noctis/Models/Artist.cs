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

/// <summary>
/// A row of up to <see cref="ViewModels.LibraryArtistsViewModel.ArtistsPerRow"/> artists
/// for the virtualized artist grid. The outer ListBox virtualizes rows; each row uses
/// a non-virtualizing UniformGrid to lay out its circular portraits horizontally.
/// </summary>
public class ArtistRow
{
    public List<Artist> Artists { get; init; } = new();
}
