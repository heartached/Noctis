using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

public enum RailItemKind { Album, Playlist, Track, Artist }

/// <summary>
/// One tile on a Library rail. Rails mix albums, playlists and songs, so the tile carries its
/// own caption and cover; <see cref="Payload"/> is what a tap opens or plays. A record, so an
/// unchanged rail compares equal and is not rebuilt (no flicker on every refresh).
/// </summary>
public sealed record RailItem(RailItemKind Kind, string Title, string Subtitle, string? ArtworkPath, object Payload)
{
    public static RailItem ForAlbum(Album album) => new(RailItemKind.Album, album.Name, album.Artist, album.ArtworkPath, album);

    public static RailItem ForTrack(Track track) => new(RailItemKind.Track, track.Title, track.Artist, track.AlbumArtworkPath, track);

    public static RailItem ForArtist(Artist artist, string? artworkPath) =>
        new(RailItemKind.Artist, artist.Name, "Artist", artworkPath, artist);

    /// <summary>Artists show as circles on a rail, everything else as rounded squares.</summary>
    public bool IsArtist => Kind == RailItemKind.Artist;

    public static RailItem ForPlaylist(Playlist playlist, string? artworkPath) =>
        new(RailItemKind.Playlist, playlist.Name, playlist.TrackIds.Count == 1 ? "1 song" : $"{playlist.TrackIds.Count} songs", artworkPath, playlist);
}
