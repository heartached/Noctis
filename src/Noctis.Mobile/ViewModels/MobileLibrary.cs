using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>Library queries the phone pages share. Pure functions of the Core library.</summary>
internal static class MobileLibrary
{
    /// <summary>
    /// An artist's songs: the tracks of albums credited to the name, then every other track
    /// filed under it (Track.GroupingArtist, the key the Artists list itself groups by), each
    /// once, in that order — so a featured appearance on someone else's album is included.
    /// </summary>
    internal static List<Track> SongsBy(ILibraryService library, string name)
    {
        var albumTracks = library.Albums
            .Where(a => string.Equals(a.Artist, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a.Tracks);
        var filed = library.Tracks.Where(t => string.Equals(t.GroupingArtist, name, StringComparison.OrdinalIgnoreCase));
        var seen = new HashSet<Guid>();
        var result = new List<Track>();
        foreach (var t in albumTracks.Concat(filed))
            if (seen.Add(t.Id)) result.Add(t);
        return result;
    }

    /// <summary>
    /// Artist name → a cover to show for it: the first album credited to the name that has
    /// one, else the first of its tracks that has one. The phone has no artist portraits
    /// (Core never fills Artist.ImagePath; only the desktop's online ArtistImageService does).
    /// </summary>
    internal static Dictionary<string, string?> ArtistArtwork(ILibraryService library)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in library.Albums)
            if (!string.IsNullOrEmpty(album.ArtworkPath)) map.TryAdd(album.Artist, album.ArtworkPath);
        foreach (var track in library.Tracks)
            if (!string.IsNullOrEmpty(track.AlbumArtworkPath)) map.TryAdd(track.GroupingArtist, track.AlbumArtworkPath);
        return map;
    }
}
