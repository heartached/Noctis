using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

/// <summary>Why a Discover entry is listed.</summary>
public enum LastFmDiscoverReason
{
    /// <summary>Last.fm calls it similar to artists the user plays (<see cref="LastFmDiscoverItem.Because"/>).</summary>
    SimilarTo,
    /// <summary>One of the user's top artists on Last.fm, with no song in the library.</summary>
    TopArtistMissing,
    /// <summary>One of the user's top albums on Last.fm that the library doesn't have.</summary>
    TopAlbumMissing,
}

/// <summary>
/// An artist or album the library doesn't have. <see cref="Url"/> is always a Last.fm page
/// (https, last.fm host); there is deliberately nothing else to open.
/// </summary>
/// <param name="Name">The artist's name, or the album title when <see cref="IsAlbum"/>.</param>
/// <param name="Artist">The album's artist (null for an artist entry).</param>
/// <param name="Because">For <see cref="LastFmDiscoverReason.SimilarTo"/>: the user's artists it is similar to.</param>
public sealed record LastFmDiscoverItem(string Name, string? Artist, LastFmDiscoverReason Reason,
    IReadOnlyList<string> Because, string Url)
{
    public bool IsAlbum => Artist != null;
}

/// <summary>The three mixes and the Discover list built from one snapshot.</summary>
public sealed class LastFmTasteMixSet
{
    public List<Track> TopSongs { get; init; } = new();
    public List<Track> Loved { get; init; } = new();
    public List<Track> TopArtists { get; init; } = new();
    public List<LastFmDiscoverItem> Discover { get; init; } = new();

    public bool IsEmpty => TopSongs.Count == 0 && Loved.Count == 0 && TopArtists.Count == 0 && Discover.Count == 0;
}

/// <summary>
/// Builds the "From your Last.fm" mixes out of songs already in the library, and the Discover
/// list out of what it lacks (GitHub #177). Pure: no I/O, so it runs off the UI thread on any
/// library snapshot and on the phone alike. Songs are matched like the play count import
/// (<see cref="LibrarySongIndex"/>: case, accents, feat. credits and remaster decoration
/// ignored, live/remix versions kept apart).
/// </summary>
public static class LastFmTasteMixes
{
    public const int DefaultMixSize = 50;
    public const int DefaultDiscoverSize = 12;
    internal const int ArtistsInArtistMix = 12;
    internal const int SongsPerArtist = 4;
    internal const int MaxMissingAlbums = 4;
    internal const int MaxMissingArtists = 3;

    /// <summary>
    /// All four lists. <paramref name="library"/> is what may be offered: callers drop songs
    /// that aren't suggestions (snoozed, Inbox) before passing it. The artist mix leaves out
    /// songs already in the Top Songs mix so the two don't repeat each other.
    /// </summary>
    public static LastFmTasteMixSet Build(IReadOnlyList<Track> library, LastFmTasteSnapshot snapshot,
        int mixSize = DefaultMixSize, int discoverSize = DefaultDiscoverSize)
    {
        var index = new LibrarySongIndex(library);
        var top = MatchTracks(index, snapshot.TopTracks, mixSize);
        return new LastFmTasteMixSet
        {
            TopSongs = top,
            Loved = MatchTracks(index, snapshot.LovedTracks, mixSize),
            TopArtists = BuildArtistMix(library, snapshot.TopArtists, mixSize, top),
            Discover = BuildDiscover(library, snapshot, discoverSize),
        };
    }

    /// <summary>The Last.fm songs found in the library, in Last.fm's order, each once.</summary>
    public static List<Track> MatchTracks(LibrarySongIndex index, IEnumerable<LastFmTasteTrack> songs, int max = DefaultMixSize)
    {
        var result = new List<Track>();
        var seen = new HashSet<Track>(ReferenceEqualityComparer.Instance);
        foreach (var s in songs)
        {
            if (result.Count >= max) break;
            if (index.Find(s.Artist, s.Title) is { } t && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Songs by the user's top Last.fm artists that are in the library: each artist's most
    /// played songs (then by title), dealt round-robin in chart order so
    /// the mix moves between artists instead of playing one artist's block.
    /// </summary>
    public static List<Track> BuildArtistMix(IReadOnlyList<Track> library, IEnumerable<LastFmTasteArtist> artists,
        int max = DefaultMixSize, IEnumerable<Track>? exclude = null)
    {
        var skip = new HashSet<Track>(exclude ?? Array.Empty<Track>(), ReferenceEqualityComparer.Instance);
        var byArtist = new Dictionary<string, List<Track>>(StringComparer.Ordinal);
        foreach (var t in library)
        {
            if (t.IsExternal || string.IsNullOrWhiteSpace(t.Title) || skip.Contains(t)) continue;
            foreach (var key in ArtistNameKeys(t.Artist).Concat(ArtistNameKeys(t.AlbumArtist)).Distinct())
            {
                if (!byArtist.TryGetValue(key, out var list)) byArtist[key] = list = new List<Track>();
                list.Add(t);
            }
        }

        var columns = new List<List<Track>>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in artists)
        {
            if (columns.Count >= ArtistsInArtistMix) break;
            var key = MatchText.Fold(a.Name);
            if (key.Length == 0 || !used.Add(key) || !byArtist.TryGetValue(key, out var songs)) continue;
            columns.Add(songs
                .Distinct<Track>(ReferenceEqualityComparer.Instance)
                // One copy per song: the same title on an album and a compilation plays once.
                .GroupBy(s => PlayCountImportMatcher.TitleKey(s.Title), StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(s => s.PlayCount).ThenBy(s => s.IsCompilation).First())
                .OrderByDescending(s => s.PlayCount)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .Take(SongsPerArtist)
                .ToList());
        }

        var result = new List<Track>();
        var taken = new HashSet<Track>(ReferenceEqualityComparer.Instance);
        for (var round = 0; round < SongsPerArtist && result.Count < max; round++)
            foreach (var col in columns)
                if (round < col.Count && result.Count < max && taken.Add(col[round]))
                    result.Add(col[round]);
        return result;
    }

    /// <summary>
    /// Artists and albums from Last.fm that the library lacks: the user's own top albums and
    /// artists missing from it first (a few of each), then artists similar to their top
    /// artists, strongest overall match first. Artists the library has (as artist, album artist
    /// or any credited name) never appear.
    /// </summary>
    public static List<LastFmDiscoverItem> BuildDiscover(IReadOnlyList<Track> library, LastFmTasteSnapshot snapshot,
        int max = DefaultDiscoverSize)
    {
        var haveArtists = new HashSet<string>(StringComparer.Ordinal);
        var haveAlbums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in library)
        {
            if (t.IsExternal) continue;
            var keys = ArtistNameKeys(t.Artist).Concat(ArtistNameKeys(t.AlbumArtist)).ToList();
            haveArtists.UnionWith(keys);
            var album = AlbumKey(t.Album);
            if (album.Length > 0)
                foreach (var k in keys) haveAlbums.Add(k + "\u001f" + album);
        }

        bool HasArtist(string name) => ArtistNameKeys(name).Any(haveArtists.Contains);

        var result = new List<LastFmDiscoverItem>();
        var listedArtists = new HashSet<string>(StringComparer.Ordinal);

        // The user's own top albums that aren't in the library (an artist they have, a record they don't).
        foreach (var a in snapshot.TopAlbums)
        {
            if (result.Count >= Math.Min(max, MaxMissingAlbums)) break;
            var artistKey = MatchText.Fold(a.Artist);
            var albumKey = AlbumKey(a.Name);
            if (artistKey.Length == 0 || albumKey.Length == 0) continue;
            if (ArtistNameKeys(a.Artist).Any(k => haveAlbums.Contains(k + "\u001f" + albumKey))) continue;
            if (!listedArtists.Add(artistKey + "\u001f" + albumKey)) continue;
            result.Add(new LastFmDiscoverItem(a.Name, a.Artist, LastFmDiscoverReason.TopAlbumMissing,
                Array.Empty<string>(), LastFmLinks.Album(a.Artist, a.Name, a.Url)));
        }

        var missingArtists = 0;
        foreach (var a in snapshot.TopArtists)
        {
            if (result.Count >= max || missingArtists >= MaxMissingArtists) break;
            var key = MatchText.Fold(a.Name);
            if (key.Length == 0 || HasArtist(a.Name) || !listedArtists.Add(key)) continue;
            // Already offered through one of their albums.
            if (result.Any(r => r.IsAlbum && MatchText.Fold(r.Artist) == key)) continue;
            result.Add(new LastFmDiscoverItem(a.Name, null, LastFmDiscoverReason.TopArtistMissing,
                Array.Empty<string>(), LastFmLinks.Artist(a.Name, a.Url)));
            missingArtists++;
        }

        // Similar artists: summed match over the seeds that name them, so an artist close to
        // several favourites outranks one close to a single one. The user's own top artists
        // are not "discoveries" even when missing (handled above).
        var topKeys = snapshot.TopArtists.Select(a => MatchText.Fold(a.Name)).ToHashSet(StringComparer.Ordinal);
        var similar = snapshot.SimilarArtists
            .Select(s => (s, Key: MatchText.Fold(s.Name)))
            .Where(x => x.Key.Length > 0 && !topKeys.Contains(x.Key) && !HasArtist(x.s.Name))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => (First: g.First().s, Score: g.Sum(x => x.s.Match),
                Because: g.Select(x => x.s.SimilarTo).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.First.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (first, _, because) in similar)
        {
            if (result.Count >= max) break;
            if (!listedArtists.Add(MatchText.Fold(first.Name))) continue;
            result.Add(new LastFmDiscoverItem(first.Name, null, LastFmDiscoverReason.SimilarTo,
                because, LastFmLinks.Artist(first.Name, first.Url)));
        }
        return result;
    }

    /// <summary>The whole folded credit and every credited name ("A feat. B" → "afeatb", "a", "b").</summary>
    internal static IEnumerable<string> ArtistNameKeys(string? artist)
    {
        var whole = MatchText.Fold(artist);
        if (whole.Length == 0) yield break;
        yield return whole;
        foreach (var n in MatchText.SplitArtists(artist))
            if (n != whole) yield return n;
    }

    private static string AlbumKey(string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return string.Empty;
        var normalized = MatchText.Fold(AlbumTitleNormalizer.Normalize(album));
        return normalized.Length > 0 ? normalized : MatchText.Fold(album);
    }
}

/// <summary>Last.fm page links. Only https last.fm URLs are passed through; anything else
/// (or nothing) is replaced by the page Last.fm serves for the name.</summary>
public static class LastFmLinks
{
    public static string Artist(string name, string? apiUrl = null)
        => IsLastFmPage(apiUrl) ? apiUrl! : "https://www.last.fm/music/" + Segment(name);

    public static string Album(string artist, string album, string? apiUrl = null)
        => IsLastFmPage(apiUrl) ? apiUrl! : "https://www.last.fm/music/" + Segment(artist) + "/" + Segment(album);

    public static bool IsLastFmPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
           (u.Host.Equals("last.fm", StringComparison.OrdinalIgnoreCase) ||
            u.Host.EndsWith(".last.fm", StringComparison.OrdinalIgnoreCase));

    // Last.fm's own form: '+' for spaces, everything else percent-encoded ('+' itself is %2B).
    private static string Segment(string s) => Uri.EscapeDataString(s.Trim()).Replace("%20", "+");
}
