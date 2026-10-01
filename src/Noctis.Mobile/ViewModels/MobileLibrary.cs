using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>Library queries the phone pages share. Pure functions of the Core library.</summary>
internal static class MobileLibrary
{
    /// <summary>How many play-log events the recent rows read (Library's Shelf, Home's rails):
    /// one number, so both pages agree on what "recent" means.</summary>
    internal const int RecentLogScan = 400;

    /// <summary>The albums of <paramref name="recent"/> (newest-first songs from the play log),
    /// each once, newest first — the Library Shelf and Home's Recently Played share it.</summary>
    internal static List<Album> RecentAlbums(ILibraryService library, IEnumerable<Track> recent, int max) =>
        recent.Select(t => t.AlbumId).Distinct()
            .Select(library.GetAlbumById).OfType<Album>()
            .Take(max).ToList();

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

    /// <summary><see cref="ArtistArtwork"/>'s cover for one name, without building the whole map.</summary>
    internal static string? ArtistArtworkFor(ILibraryService library, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return library.Albums.FirstOrDefault(a => !string.IsNullOrEmpty(a.ArtworkPath)
                   && string.Equals(a.Artist, name, StringComparison.OrdinalIgnoreCase))?.ArtworkPath
               ?? library.Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtworkPath)
                   && string.Equals(t.GroupingArtist, name, StringComparison.OrdinalIgnoreCase))?.AlbumArtworkPath;
    }

    /// <summary>
    /// Replace <paramref name="target"/> only when membership or order changed: a Reset tears
    /// every tile down and re-realises it, felt as a flicker on each refresh (the desktop
    /// HomeViewModel.ReplaceRowIfChanged rule). Default equality: reference for Album/Track,
    /// value for RailItem.
    /// </summary>
    internal static void ReplaceIfChanged<T>(BulkObservableCollection<T> target, IReadOnlyList<T> next)
    {
        if (target.Count == next.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count && same; i++)
                same = EqualityComparer<T>.Default.Equals(target[i], next[i]);
            if (same) return;
        }
        target.ReplaceAll(next);
    }

    /// <summary>Albums newest-first by their most recently added track.</summary>
    internal static List<Album> RecentlyAddedAlbums(ILibraryService library, int max) =>
        library.Albums
            .Where(a => a.Tracks.Count > 0)
            .OrderByDescending(a => a.Tracks.Max(t => t.DateAdded))
            .Take(max)
            .ToList();

    /// <summary>The date a release sorts by (the desktop ArtistDetailViewModel.ReleaseSortDate
    /// rule): the first track's parseable release-date tag, else January 1 of the album year,
    /// else the epoch so untagged releases sink.</summary>
    internal static DateTime ReleaseSortDate(Album album)
    {
        var tagged = album.Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ReleaseDate))?.ReleaseDate;
        if (Track.TryParseReleaseDate(tagged, out var date)) return date;
        if (album.Year is > 0 and < 10000) return new DateTime(album.Year, 1, 1);
        return DateTime.MinValue;
    }
}
