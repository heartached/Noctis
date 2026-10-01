using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Library search with the desktop's SearchText rule: accent/case/punctuation-insensitive
/// substring on the models' cached normalized keys, or — for a query of only punctuation,
/// which folds to an empty key — a raw case-insensitive substring. Title-prefix hits first.
/// </summary>
internal static class MobileSearch
{
    internal sealed record Results(IReadOnlyList<Track> Songs, IReadOnlyList<Album> Albums, IReadOnlyList<ArtistListItem> Artists)
    {
        public static Results Empty { get; } = new(Array.Empty<Track>(), Array.Empty<Album>(), Array.Empty<ArtistListItem>());
    }

    internal static Results Find(ILibraryService library, string? query, int songCap, int albumCap, int artistCap)
    {
        if (string.IsNullOrWhiteSpace(query)) return Results.Empty;
        var raw = query.Trim();
        var key = SearchText.Normalize(raw);

        bool Hit(string text, string cachedKey) =>
            key.Length > 0 ? cachedKey.Contains(key, StringComparison.Ordinal) : text.Contains(raw, StringComparison.OrdinalIgnoreCase);
        bool Starts(string cachedKey) => key.Length > 0 && cachedKey.StartsWith(key, StringComparison.Ordinal);

        var songs = library.Tracks
            .Where(t => Hit(t.Title, t.SearchTitleKey) || Hit(t.Artist, t.SearchArtistKey) || Hit(t.Album, t.SearchAlbumKey))
            .OrderByDescending(t => Starts(t.SearchTitleKey))
            .ThenByDescending(t => t.PlayCount)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(songCap)
            .ToList();

        var albums = library.Albums
            .Where(a => Hit(a.Name, a.SearchNameKey) || Hit(a.Artist, a.SearchArtistKey))
            .OrderByDescending(a => Starts(a.SearchNameKey))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(albumCap)
            .ToList();

        var artistHits = library.Artists
            .Select(a => (Artist: a, Key: SearchText.Normalize(a.Name)))
            .Where(x => Hit(x.Artist.Name, x.Key))
            .OrderByDescending(x => Starts(x.Key))
            .ThenBy(x => x.Artist.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(artistCap)
            .ToList();
        var artwork = artistHits.Count > 0 ? MobileLibrary.ArtistArtwork(library) : new Dictionary<string, string?>();
        var artists = artistHits.Select(x => new ArtistListItem(x.Artist, artwork.GetValueOrDefault(x.Artist.Name))).ToList();

        return new Results(songs, albums, artists);
    }
}
