using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Apple Music catalogue through the public iTunes Search/Lookup API (keyless). Best source
/// for presentation: clean store titles, copyright line, explicit flag, track/disc numbers
/// with counts, a single curated genre, and the largest covers (the artwork CDN serves any
/// requested size up to the master — 3000 px is asked for). No ISRC, label or composer, and
/// no ISRC lookup (verified 2026-10-08: <c>lookup?isrc=</c> returns 0 results for a valid code).
/// Apple asks for ~20 calls/minute, so at most two requests per search.
/// </summary>
public sealed partial class AppleMusicProvider : IMetadataProvider
{
    private const string Api = "https://itunes.apple.com";
    private const string Country = "us";
    private const int AlbumLookupTop = 2;

    private readonly ProviderHttp _http;

    public AppleMusicProvider(HttpClient http, LruCache<string, string>? cache = null, RequestPacer? pacer = null)
        => _http = new ProviderHttp(http, pacer ?? RequestPacer.AppleMusic, cache);

    public string Name => ProviderNames.AppleMusic;
    public TimeSpan Timeout => TimeSpan.FromSeconds(12);
    public bool IsEnabled(AppSettings settings) => settings.AppleMusicMetadataEnabled;

    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MetadataQuery query, CancellationToken ct)
        => query.AlbumScope ? SearchAlbumsAsync(query, ct) : SearchSongsAsync(query, ct);

    private async Task<IReadOnlyList<MetadataCandidate>> SearchSongsAsync(MetadataQuery q, CancellationToken ct)
    {
        var term = DeezerProvider.BuildTerm(q.Artist.Length > 0 ? q.Artist : q.AlbumArtist, MatchText.StripFeaturing(q.Title));
        if (MatchText.StripFeaturing(q.Title).Length == 0) return Array.Empty<MetadataCandidate>();
        var json = await _http.GetStringAsync($"{Api}/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&country={Country}&limit=15", ct).ConfigureAwait(false);
        var rows = ParseSongRows(json);
        var songs = rows.Select(r => r.Candidate).ToList();

        // Only the album record carries the copyright line and the album's own date; fetch it
        // for the best hit (by our score).
        var best = songs.Select(c => CandidateScorer.ScoreTrack(q, c)).OrderByDescending(c => c.Confidence).FirstOrDefault();
        var collectionId = best is null ? null : rows.First(r => r.Candidate.ProviderId == best.ProviderId).CollectionId;
        if (best is null || string.IsNullOrEmpty(collectionId)) return songs;
        try
        {
            var lookup = await _http.GetStringAsync(LookupUrl(collectionId), ct).ConfigureAwait(false);
            if (lookup is not null)
            {
                var i = songs.FindIndex(s => s.ProviderId == best.ProviderId);
                songs[i] = ApplyCollection(songs[i], lookup);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* keep the search hit */ }
        return songs;
    }

    private async Task<IReadOnlyList<MetadataCandidate>> SearchAlbumsAsync(MetadataQuery q, CancellationToken ct)
    {
        var artist = q.AlbumArtist.Length > 0 ? q.AlbumArtist : q.Artist;
        var term = DeezerProvider.BuildTerm(artist, q.Album);
        if (q.Album.Trim().Length == 0) return Array.Empty<MetadataCandidate>();
        var hits = ParseCollections(await _http.GetStringAsync(AlbumSearchUrl(term), ct).ConfigureAwait(false));
        var normalized = AlbumTitleNormalizer.Normalize(q.Album);
        if (hits.Count == 0 && !string.Equals(normalized, q.Album.Trim(), StringComparison.OrdinalIgnoreCase))
            hits = ParseCollections(await _http.GetStringAsync(AlbumSearchUrl(DeezerProvider.BuildTerm(artist, normalized)), ct).ConfigureAwait(false));

        var top = hits.Select(c => CandidateScorer.ScoreAlbum(q, c))
            .OrderByDescending(c => c.Confidence)
            .Take(AlbumLookupTop)
            .Select(c => c.ProviderId)
            .ToList();
        var result = new List<MetadataCandidate>(hits.Count);
        foreach (var hit in hits)
        {
            if (!top.Contains(hit.ProviderId)) { result.Add(hit); continue; }
            try
            {
                var json = await _http.GetStringAsync(LookupUrl(hit.ProviderId), ct).ConfigureAwait(false);
                result.Add(json is null ? hit : ParseAlbumLookup(json) ?? hit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { result.Add(hit); }
        }
        return result;
    }

    private static string AlbumSearchUrl(string term)
        => $"{Api}/search?term={Uri.EscapeDataString(term)}&media=music&entity=album&country={Country}&limit=10";

    private static string LookupUrl(string collectionId)
        => $"{Api}/lookup?id={Uri.EscapeDataString(collectionId)}&entity=song&country={Country}";

    // ── Parsing (pure; tested against recorded responses) ──

    [GeneratedRegex(@"/\d+x\d+bb\.(?:jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtworkSize();

    /// <summary>Rewrites an Apple artwork URL ("…/100x100bb.jpg") to another square size.</summary>
    public static Uri? ResizeArtwork(string url, int size)
    {
        if (string.IsNullOrWhiteSpace(url) || !ArtworkSize().IsMatch(url)) return Json.Url(url);
        return Json.Url(ArtworkSize().Replace(url, $"/{size}x{size}bb.jpg"));
    }

    /// <summary>Parses song rows from a <c>/search?entity=song</c> or <c>/lookup</c> payload.</summary>
    public static IReadOnlyList<MetadataCandidate> ParseSongs(string? json)
        => ParseSongRows(json).Select(r => r.Candidate).ToList();

    /// <summary>Song rows with their album (collection) id, for the album lookup.</summary>
    internal static IReadOnlyList<(MetadataCandidate Candidate, string CollectionId)> ParseSongRows(string? json)
    {
        var list = new List<(MetadataCandidate, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var r in Json.Arr(doc.RootElement, "results"))
        {
            if (Json.Str(r, "wrapperType") != "track" || Json.Str(r, "kind") != "song") continue;
            var id = Json.Long(r, "trackId");
            var title = Json.Str(r, "trackName");
            if (id is null || title.Length == 0) continue;
            var (date, year) = MatchText.ParseDate(Json.Str(r, "releaseDate"));
            var artist = Json.Str(r, "artistName");
            var collectionArtist = Json.Str(r, "collectionArtistName");
            var art = Json.Str(r, "artworkUrl100");
            var collectionId = Json.Long(r, "collectionId")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            list.Add((new MetadataCandidate
            {
                Provider = ProviderNames.AppleMusic,
                ProviderId = id.Value.ToString(CultureInfo.InvariantCulture),
                WebUrl = Json.Url(Json.Str(r, "trackViewUrl")),
                Title = title,
                Artist = artist,
                Album = Json.Str(r, "collectionName"),
                AlbumArtist = collectionArtist.Length > 0 ? collectionArtist : artist,
                ReleaseDate = date,
                Year = year,
                Genre = Json.Str(r, "primaryGenreName"),
                TrackNumber = Json.Int(r, "trackNumber") is > 0 and var tn ? tn : null,
                TrackCount = Json.Int(r, "trackCount") is > 0 and var tc ? tc : null,
                DiscNumber = Json.Int(r, "discNumber") is > 0 and var dn ? dn : null,
                DiscCount = Json.Int(r, "discCount") is > 0 and var dc ? dc : null,
                Explicit = Explicitness(Json.Str(r, "trackExplicitness")),
                Duration = Json.Millis(Json.Long(r, "trackTimeMillis")),
                ArtworkUrl = ResizeArtwork(art, 3000),
                ArtworkThumbUrl = ResizeArtwork(art, 300),
            }, collectionId));
        }
        return list;
    }

    /// <summary>Parses album rows from a <c>/search?entity=album</c> payload.</summary>
    public static IReadOnlyList<MetadataCandidate> ParseCollections(string? json)
    {
        var list = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var r in Json.Arr(doc.RootElement, "results"))
            if (ReadCollection(r) is { } c) list.Add(c);
        return list;
    }

    private static MetadataCandidate? ReadCollection(JsonElement r)
    {
        if (Json.Str(r, "wrapperType") != "collection") return null;
        var id = Json.Long(r, "collectionId");
        var title = Json.Str(r, "collectionName");
        if (id is null || title.Length == 0) return null;
        var (date, year) = MatchText.ParseDate(Json.Str(r, "releaseDate"));
        var artist = Json.Str(r, "artistName");
        var art = Json.Str(r, "artworkUrl100");
        return new MetadataCandidate
        {
            Provider = ProviderNames.AppleMusic,
            ProviderId = id.Value.ToString(CultureInfo.InvariantCulture),
            WebUrl = Json.Url(Json.Str(r, "collectionViewUrl")),
            Album = title,
            Artist = artist,
            AlbumArtist = artist,
            ReleaseDate = date,
            Year = year,
            Genre = Json.Str(r, "primaryGenreName"),
            Copyright = Json.Str(r, "copyright"),
            TrackCount = Json.Int(r, "trackCount") is > 0 and var tc ? tc : null,
            Explicit = Explicitness(Json.Str(r, "collectionExplicitness")),
            ArtworkUrl = ResizeArtwork(art, 3000),
            ArtworkThumbUrl = ResizeArtwork(art, 300),
        };
    }

    /// <summary>Parses a <c>/lookup?id={collectionId}&amp;entity=song</c> payload into a full
    /// release candidate with its track list.</summary>
    public static MetadataCandidate? ParseAlbumLookup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = Json.Arr(doc.RootElement, "results").ToList();
        var album = rows.Select(ReadCollection).FirstOrDefault(c => c is not null);
        if (album is null) return null;
        var songs = ParseSongs(json);
        var tracks = songs
            .OrderBy(s => s.DiscNumber ?? 1).ThenBy(s => s.TrackNumber ?? int.MaxValue)
            .Select(s => new CandidateTrack
            {
                Title = s.Title,
                Artist = s.Artist,
                TrackNumber = s.TrackNumber,
                DiscNumber = s.DiscNumber,
                Duration = s.Duration,
                Explicit = s.Explicit,
            }).ToList();
        return album with
        {
            Tracks = tracks,
            DiscCount = songs.Select(s => s.DiscCount).FirstOrDefault(d => d is > 0),
            TrackCount = album.TrackCount ?? (tracks.Count > 0 ? tracks.Count : null),
        };
    }

    /// <summary>Adds the album record's copyright, date and album artist to a song candidate.</summary>
    public static MetadataCandidate ApplyCollection(MetadataCandidate song, string lookupJson)
    {
        var album = ParseAlbumLookup(lookupJson);
        if (album is null) return song;
        return song with
        {
            Copyright = album.Copyright,
            // A song's own releaseDate is when it first came out (often its single); the album
            // date is what an album track is tagged with.
            ReleaseDate = album.ReleaseDate.Length > 0 ? album.ReleaseDate : song.ReleaseDate,
            Year = album.Year ?? song.Year,
            AlbumArtist = album.AlbumArtist.Length > 0 ? album.AlbumArtist : song.AlbumArtist,
        };
    }

    private static bool? Explicitness(string value) => value switch
    {
        "explicit" => true,
        "cleaned" or "notExplicit" => false,
        _ => null,
    };
}
