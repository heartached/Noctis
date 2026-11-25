namespace Noctis.Models;

/// <summary>The kind of page a queue was started from (see <see cref="PlaySource"/>).</summary>
public enum PlaySourceKind
{
    Album,
    Artist,
    Playlist,
    Genre,
    Folder,
    Songs,
    Albums,
    Favorites,
    Home,
    Inbox,
    AudioCd,
    Server,
}

/// <summary>
/// Where the playing queue was started from, for the desktop's "Playing from …" caption and the
/// island title's "go back there" click (Discord 11-24, sina 4.3.7 / 4.3.4). <see cref="Name"/> is
/// the album, artist, playlist, genre or folder name; null for a library section (Songs,
/// Favorites…), whose name is the sidebar's, in the current language. <see cref="Id"/> finds the
/// page again: an album or playlist id, the artist name, genre name or folder path.
/// </summary>
public sealed record PlaySource(PlaySourceKind Kind, string? Name = null, string? Id = null)
{
    public static PlaySource ForAlbum(Album album) => new(PlaySourceKind.Album, album.Name, album.Id.ToString());
    public static PlaySource ForArtist(string artist) => new(PlaySourceKind.Artist, artist, artist);
    public static PlaySource ForPlaylist(Playlist playlist) => new(PlaySourceKind.Playlist, playlist.Name, playlist.Id.ToString());
    public static PlaySource ForGenre(string genre) => new(PlaySourceKind.Genre, genre, genre);
    public static PlaySource Section(PlaySourceKind kind) => new(kind);
}
