namespace Noctis.Models;

/// <summary>
/// Wrapper for the Favorites view — represents either a single track or
/// a fully-favorited album displayed as one card.
/// </summary>
public class FavoriteItem
{
    public Track? Track { get; init; }
    public Album? Album { get; init; }

    public bool IsAlbum => Album != null;

    /// <summary>Display title: album name or track title.</summary>
    public string Title => IsAlbum ? Album!.Name : Track!.Title;

    /// <summary>Display subtitle: album artist or track artist.</summary>
    public string Subtitle => IsAlbum ? Album!.Artist : Track!.Artist;

    /// <summary>Artwork path for display.</summary>
    public string? ArtworkPath => IsAlbum ? Album!.ArtworkPath : Track!.AlbumArtworkPath;

    /// <summary>Whether the item contains explicit content.</summary>
    public bool IsExplicit => IsAlbum ? Album!.IsExplicit : Track!.IsExplicit;
}
