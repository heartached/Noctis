using Noctis.Models;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

public enum RailItemKind { Album, Playlist, Track, Artist, Mix }

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
        new(RailItemKind.Artist, artist.Name, Loc.T("Mobile.Artist"), artworkPath, artist);

    /// <summary>A From Your Last.fm mix: its name, its song count and its first song's cover; a tap
    /// opens its songs (<see cref="LastFmMix"/>).</summary>
    public static RailItem ForMix(LastFmMix mix, IReadOnlyList<Track> tracks) =>
        new(RailItemKind.Mix, Loc.T(mix.TitleKey), Loc.Plural("Mobile.Count.Songs", tracks.Count),
            tracks.Select(t => t.AlbumArtworkPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)), mix);

    /// <summary>Artists show as circles on a rail, everything else as rounded squares.</summary>
    public bool IsArtist => Kind == RailItemKind.Artist;

    public static RailItem ForPlaylist(Playlist playlist, string? artworkPath) =>
        new(RailItemKind.Playlist, playlist.Name, Loc.Plural("Mobile.Count.Songs", playlist.TrackIds.Count), artworkPath, playlist);

    /// <summary>
    /// Every library refresh rebuilds the Album, Track and Artist objects (a rescan publishes every
    /// 1.5 s), so comparing the payload by reference made an unchanged rail differ each time and
    /// tear down and re-fade every tile: Recently Added went blank again and again during a rescan.
    /// Those payloads compare by what they are (id, or the artist's name); a tap re-resolves them
    /// (<see cref="ShellViewModel"/>'s rail commands). Playlists stay by reference.
    /// </summary>
    public bool Equals(RailItem? other) =>
        other is not null && Kind == other.Kind && Title == other.Title && Subtitle == other.Subtitle
        && ArtworkPath == other.ArtworkPath && Equals(PayloadKey, other.PayloadKey);

    public override int GetHashCode() => HashCode.Combine(Kind, Title, Subtitle, ArtworkPath, PayloadKey);

    private object PayloadKey => Payload switch
    {
        Album album => album.Id,
        Track track => track.Id,
        Artist artist => artist.Name.ToUpperInvariant(),
        _ => Payload,
    };
}
