using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Built-in bookmarks list ("Listen Later") for tracks, albums, and artists —
/// separate from playlists, addable from context menus everywhere.
/// </summary>
public interface IListenLaterService
{
    /// <summary>Snapshot of all bookmarks, newest first.</summary>
    IReadOnlyList<ListenLaterItem> Items { get; }

    /// <summary>Fires after any add/remove/clear.</summary>
    event EventHandler? Changed;

    /// <summary>Adds a bookmark; duplicates (same kind + target) are ignored.</summary>
    void AddTrack(Track track);
    void AddAlbum(Album album);
    void AddArtist(string artistName);

    bool ContainsTrack(Guid trackId);
    bool ContainsAlbum(Guid albumId);
    bool ContainsArtist(string artistName);

    void Remove(Guid itemId);
    void Clear();
}
