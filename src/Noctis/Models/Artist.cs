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

    /// <summary>
    /// Cached portrait path. Deliberately NOT an observable property: ArtistImageService
    /// assigns it from a worker thread at seven sites with no dispatching, and raising
    /// PropertyChanged there would push binding updates onto a non-UI thread, which
    /// Avalonia rejects. Views that show it must re-materialize their items once the
    /// fetch reports in — see LibraryArtistsViewModel and HomeViewModel.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>Favorite flag (GitHub #41): favorites sort to the top of the artists grid
    /// and carry an accent star on the tile. Like ImagePath, deliberately NOT observable —
    /// it is stamped during list builds and reflected by re-materializing the rows.</summary>
    public bool IsFavorite { get; set; }

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
