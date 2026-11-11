namespace Noctis.Models;

/// <summary>
/// Discord (Halil, 11-11): an album that also lists the songs of another album, such as a
/// deluxe edition holding the original's tracks next to its own bonus files. Lives in the
/// library only (<see cref="AppSettings.AlbumLinks"/>); no file is copied or re-tagged, and the
/// linked songs stay the same Track instances, so favourites and play counts are shared.
/// </summary>
public sealed class AlbumLink
{
    /// <summary>The album that borrows the songs (the deluxe edition).</summary>
    public Guid AlbumId { get; set; }

    /// <summary>The album whose own songs it lists too (the original).</summary>
    public Guid SourceAlbumId { get; set; }
}
