using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>What Search's sort menu orders the songs and albums by.</summary>
public enum SearchSort { Relevance, RecentlyAdded, ReleaseYear, RecentlyPlayed, MostPlayed, Title }

/// <summary>
/// Library search with the desktop's SearchText rule: accent/case/punctuation-insensitive
/// substring on the models' cached normalized keys, or — for a query of only punctuation,
/// which folds to an empty key — a raw case-insensitive substring. Title-prefix hits first.
/// </summary>
internal static class MobileSearch
{
    internal sealed record Results(IReadOnlyList<Track> Songs, IReadOnlyList<Album> Albums, IReadOnlyList<ArtistListItem> Artists,
        IReadOnlyList<Playlist> Playlists)
    {
        public static Results Empty { get; } = new(Array.Empty<Track>(), Array.Empty<Album>(), Array.Empty<ArtistListItem>(), Array.Empty<Playlist>());

        /// <summary>The artist the query names, shown on top with their songs; null when the query names none.</summary>
        public ArtistListItem? Featured { get; init; }
    }

    /// <summary>The search's match test for one query, shared with the song lists' in-page search.</summary>
    internal sealed class Matcher
    {
        private readonly string _raw;
        private readonly string _key;

        public Matcher(string query)
        {
            _raw = query.Trim();
            _key = SearchText.Normalize(_raw);
        }

        public string Key => _key;

        public bool Hit(string text, string cachedKey) =>
            _key.Length > 0 ? cachedKey.Contains(_key, StringComparison.Ordinal) : text.Contains(_raw, StringComparison.OrdinalIgnoreCase);

        public bool Starts(string cachedKey) => _key.Length > 0 && cachedKey.StartsWith(_key, StringComparison.Ordinal);

        public bool HitTrack(Track t) => Hit(t.Title, t.SearchTitleKey) || Hit(t.Artist, t.SearchArtistKey) || Hit(t.Album, t.SearchAlbumKey);
    }

    internal static Results Find(ILibraryService library, string? query, int songCap, int albumCap, int artistCap,
        SearchSort sort = SearchSort.Relevance, IEnumerable<Playlist>? playlists = null, int playlistCap = 0,
        Func<IReadOnlyDictionary<string, string?>>? artistArtwork = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return Results.Empty;
        var match = new Matcher(query);

        var songHits = library.Tracks.Where(match.HitTrack);
        var songs = SortSongs(songHits, match, sort).Take(songCap).ToList();

        var albumHits = library.Albums.Where(a => match.Hit(a.Name, a.SearchNameKey) || match.Hit(a.Artist, a.SearchArtistKey));
        var albums = SortAlbums(albumHits, match, sort).Take(albumCap).ToList();

        // Every keystroke used to normalise every artist's name, sort every hit by culture and
        // build the artist → cover map over the whole library; only a dozen are shown.
        var artistHits = new List<(Artist Artist, string Key)>();
        foreach (var a in library.Artists)
        {
            var key = NameKey(a.Name);
            if (match.Hit(a.Name, key)) artistHits.Add((a, key));
        }

        // The featured artist: one whose name is the query, or — once three letters narrow it
        // down — the first artist the query begins. They head the results with their songs and
        // leave the Artists row, so they are not listed twice. (First in the list's own order:
        // query-prefix hits, then by name.)
        (Artist Artist, string Key) top = default;
        if (match.Key.Length > 0) top = FirstByName(artistHits.Where(x => x.Key == match.Key));
        if (top.Artist == null && match.Key.Length >= 3) top = FirstByName(artistHits.Where(x => match.Starts(x.Key)));
        if (top.Artist != null) artistHits.Remove(top);
        var shownArtists = artistHits
            .OrderByDescending(x => match.Starts(x.Key))
            .ThenBy(x => x.Artist.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(artistCap)
            .ToList();
        IReadOnlyDictionary<string, string?>? artwork = null;
        if (top.Artist != null || shownArtists.Count > 0)
            artwork = artistArtwork?.Invoke() ?? MobileLibrary.ArtistArtwork(library);

        ArtistListItem? featured = top.Artist != null ? new ArtistListItem(top.Artist, artwork!.GetValueOrDefault(top.Artist.Name)) : null;
        var artists = shownArtists.Select(x => new ArtistListItem(x.Artist, artwork!.GetValueOrDefault(x.Artist.Name))).ToList();

        var playlistHits = (playlists ?? Array.Empty<Playlist>())
            .Select(p => (Playlist: p, Key: SearchText.Normalize(p.Name)))
            .Where(x => match.Hit(x.Playlist.Name, x.Key))
            .OrderByDescending(x => match.Starts(x.Key))
            .ThenBy(x => x.Playlist.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(playlistCap)
            .Select(x => x.Playlist)
            .ToList();

        return new Results(songs, albums, artists, playlistHits) { Featured = featured };
    }

    private static (Artist Artist, string Key) FirstByName(IEnumerable<(Artist Artist, string Key)> hits)
    {
        (Artist Artist, string Key) best = default;
        foreach (var hit in hits)
            if (best.Artist == null || StringComparer.CurrentCultureIgnoreCase.Compare(hit.Artist.Name, best.Artist.Name) < 0)
                best = hit;
        return best;
    }

    // Artist names' search keys, as Track and Album cache theirs (Artist has no field for it).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> NameKeys = new(StringComparer.Ordinal);

    private static string NameKey(string name)
    {
        if (NameKeys.TryGetValue(name, out var key)) return key;
        if (NameKeys.Count > 50_000) NameKeys.Clear();
        return NameKeys[name] = SearchText.Normalize(name);
    }

    /// <summary>Relevance: title-prefix hits, then the most played; every other order keeps that as its tiebreak.</summary>
    private static IEnumerable<Track> SortSongs(IEnumerable<Track> hits, Matcher match, SearchSort sort)
    {
        var ordered = sort switch
        {
            SearchSort.RecentlyAdded => hits.OrderByDescending(t => t.DateAdded),
            SearchSort.ReleaseYear => hits.OrderByDescending(t => t.Year),
            SearchSort.RecentlyPlayed => hits.OrderByDescending(t => t.LastPlayed ?? DateTime.MinValue),
            SearchSort.MostPlayed => hits.OrderByDescending(t => t.PlayCount),
            SearchSort.Title => hits.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => hits.OrderByDescending(t => match.Starts(t.SearchTitleKey)),
        };
        return ordered
            .ThenByDescending(t => match.Starts(t.SearchTitleKey))
            .ThenByDescending(t => t.PlayCount)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IEnumerable<Album> SortAlbums(IEnumerable<Album> hits, Matcher match, SearchSort sort)
    {
        var ordered = sort switch
        {
            SearchSort.RecentlyAdded => hits.OrderByDescending(a => a.Tracks.Count == 0 ? DateTime.MinValue : a.Tracks.Max(t => t.DateAdded)),
            SearchSort.ReleaseYear => hits.OrderByDescending(a => a.Year),
            SearchSort.RecentlyPlayed => hits.OrderByDescending(a => a.Tracks.Count == 0 ? DateTime.MinValue : a.Tracks.Max(t => t.LastPlayed ?? DateTime.MinValue)),
            SearchSort.MostPlayed => hits.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)),
            SearchSort.Title => hits.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => hits.OrderByDescending(a => match.Starts(a.SearchNameKey)),
        };
        return ordered
            .ThenByDescending(a => match.Starts(a.SearchNameKey))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
    }
}
