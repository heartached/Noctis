using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

public enum RailItemKind { Album, Playlist, Track }

/// <summary>The Library root's lower-half views (ruling 4: chips switch it rather than filter the rails).</summary>
public enum LibraryChip { AllMusic, Playlists, Albums, Artists, Songs }

/// <summary>
/// One tile on a Library rail. Rails mix albums, playlists and songs, so the tile carries its
/// own caption and cover; <see cref="Payload"/> is what a tap opens or plays. A record, so an
/// unchanged rail compares equal and is not rebuilt (no flicker on every refresh).
/// </summary>
public sealed record RailItem(RailItemKind Kind, string Title, string Subtitle, string? ArtworkPath, object Payload)
{
    public static RailItem ForAlbum(Album album) => new(RailItemKind.Album, album.Name, album.Artist, album.ArtworkPath, album);

    public static RailItem ForTrack(Track track) => new(RailItemKind.Track, track.Title, track.Artist, track.AlbumArtworkPath, track);

    public static RailItem ForPlaylist(Playlist playlist, string? artworkPath) =>
        new(RailItemKind.Playlist, playlist.Name, playlist.TrackIds.Count == 1 ? "1 song" : $"{playlist.TrackIds.Count} songs", artworkPath, playlist);
}
