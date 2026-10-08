using System.Net;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Deezer public API (keyless). Strong on mainstream catalogue: ISRC, BPM, track/disc numbers,
/// label, UPC, genre, explicit flag and 1000 px covers. Exact ISRC lookups via
/// <c>/track/isrc:{code}</c>.
///
/// Searches use plain free text ("artist title"), NOT the advanced <c>artist:"…" track:"…"</c>
/// syntax: verified live on 2026-10-08, any query with an <c>artist:</c> filter returns
/// <c>{"data":[],"total":0}</c> (e.g. <c>artist:"Daft Punk" track:"One More Time"</c>), while
/// "Daft Punk One More Time" returns the album version first. Ranking is ours anyway.
/// </summary>
public sealed class DeezerProvider : IMetadataProvider
{
    private const string Api = "https://api.deezer.com";
    private const int EnrichTop = 2;
    private const int AlbumEnrichTop = 3;

    private readonly ProviderHttp _http;

    public DeezerProvider(HttpClient http, LruCache<string, string>? cache = null, RequestPacer? pacer = null)
    {
        // Deezer localises genre names from Accept-Language (IP geolocation otherwise); pin
        // English so genres match the editor's fixed genre list (same as DeezerMetadataService).
        _http = new ProviderHttp(http, pacer ?? RequestPacer.Deezer, cache,
            req => req.Headers.AcceptLanguage.ParseAdd("en"));
    }

    public string Name => ProviderNames.Deezer;
    public TimeSpan Timeout => TimeSpan.FromSeconds(12);
    public bool IsEnabled(AppSettings settings) => settings.DeezerEnabled;

    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MetadataQuery query, CancellationToken ct)
        => query.AlbumScope ? SearchAlbumsAsync(query, ct) : SearchTracksAsync(query, ct);

    // ── Track scope ──

    private async Task<IReadOnlyList<MetadataCandidate>> SearchTracksAsync(MetadataQuery q, CancellationToken ct)
    {
        var found = new List<MetadataCandidate>();
        var enriched = new HashSet<string>();

        if (q.Isrc.Length > 0)
        {
            var json = await GetAsync($"{Api}/track/isrc:{Uri.EscapeDataString(MatchText.NormalizeCode(q.Isrc))}", ct).ConfigureAwait(false);
            if (json is not null && ParseTrackWithAlbumId(json) is { } exact)
            {
                var full = await TryApplyAlbumAsync(exact.Candidate, exact.AlbumId, ct).ConfigureAwait(false);
                found.Add(full);
                enriched.Add(full.ProviderId);
            }
        }

        var term = BuildTerm(q.Artist.Length > 0 ? q.Artist : q.AlbumArtist, MatchText.StripFeaturing(q.Title));
        if (term.Length > 0)
        {
            var json = await GetAsync($"{Api}/search?q={Uri.EscapeDataString(term)}&limit=15", ct).ConfigureAwait(false);
            foreach (var c in ParseSearchTracks(json))
                if (found.All(f => f.ProviderId != c.ProviderId)) found.Add(c);
        }

        // Fill the details of the BEST hits (by our score), never blindly the first one: the old
        // path took Deezer's first hit, so ISRC/track #/BPM could belong to another version.
        var best = found.Select(c => CandidateScorer.ScoreTrack(q, c))
            .OrderByDescending(c => c.Confidence)
            .Where(c => !enriched.Contains(c.ProviderId))
            .Take(EnrichTop)
            .Select(c => c.ProviderId)
            .ToList();
        for (var i = 0; i < found.Count; i++)
        {
            if (!best.Contains(found[i].ProviderId)) continue;
            found[i] = await TryEnrichTrackAsync(found[i], ct).ConfigureAwait(false);
        }
        return found;
    }

    private async Task<MetadataCandidate> TryEnrichTrackAsync(MetadataCandidate light, CancellationToken ct)
    {
        try
        {
            var json = await GetAsync($"{Api}/track/{light.ProviderId}", ct).ConfigureAwait(false);
            var full = json is null ? null : ParseTrackWithAlbumId(json);
            if (full is null) return light;
            return await TryApplyAlbumAsync(full.Value.Candidate, full.Value.AlbumId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return light; } // details are a bonus; the search hit stands
    }

    private async Task<MetadataCandidate> TryApplyAlbumAsync(MetadataCandidate track, long? albumId, CancellationToken ct)
    {
        if (albumId is not > 0) return track;
        try
        {
            var json = await GetAsync($"{Api}/album/{albumId}", ct).ConfigureAwait(false);
            return json is null ? track : ApplyAlbum(track, json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return track; }
    }

    // ── Album scope ──

    private async Task<IReadOnlyList<MetadataCandidate>> SearchAlbumsAsync(MetadataQuery q, CancellationToken ct)
    {
        var artist = q.AlbumArtist.Length > 0 ? q.AlbumArtist : q.Artist;
        var term = BuildTerm(artist, q.Album);
        if (term.Length == 0) return Array.Empty<MetadataCandidate>();

        var hits = ParseSearchAlbums(await GetAsync($"{Api}/search/album?q={Uri.EscapeDataString(term)}&limit=10", ct).ConfigureAwait(false));
        var normalized = AlbumTitleNormalizer.Normalize(q.Album);
        if (hits.Count == 0 && !string.Equals(normalized, q.Album.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // "Album (Deluxe Edition)" may not exist on Deezer under that exact name.
            var retry = BuildTerm(artist, normalized);
            hits = ParseSearchAlbums(await GetAsync($"{Api}/search/album?q={Uri.EscapeDataString(retry)}&limit=10", ct).ConfigureAwait(false));
        }

        var top = hits.Select(c => CandidateScorer.ScoreAlbum(q, c))
            .OrderByDescending(c => c.Confidence)
            .Take(AlbumEnrichTop)
            .Select(c => c.ProviderId)
            .ToList();
        var result = new List<MetadataCandidate>(hits.Count);
        foreach (var hit in hits)
        {
            if (!top.Contains(hit.ProviderId)) { result.Add(hit); continue; }
            try
            {
                var albumJson = await GetAsync($"{Api}/album/{hit.ProviderId}", ct).ConfigureAwait(false);
                var tracksJson = await GetAsync($"{Api}/album/{hit.ProviderId}/tracks?limit=300", ct).ConfigureAwait(false);
                result.Add(albumJson is null ? hit : ParseAlbum(albumJson, tracksJson) ?? hit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { result.Add(hit); }
        }
        return result;
    }

    // ── HTTP ──

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        if (json is null) return null;
        // Deezer reports errors with HTTP 200 and an "error" object.
        var code = ErrorCode(json);
        if (code is null) return json;
        if (code == 800) return null; // "no data" (unknown id / ISRC)
        throw new HttpRequestException(code == 4 ? "Deezer quota exceeded" : $"Deezer error {code}", null,
            code == 4 ? HttpStatusCode.TooManyRequests : null);
    }

    internal static int? ErrorCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var err = Json.Obj(doc.RootElement, "error");
            return err.ValueKind == JsonValueKind.Object ? Json.Int(err, "code") ?? -1 : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string BuildTerm(string artist, string title)
        => $"{Track.GetPrimaryArtist(artist)} {title}".Trim();

    // ── Parsing (pure; tested against recorded responses) ──

    /// <summary>Parses a <c>/search</c> (track) payload into light candidates.</summary>
    public static IReadOnlyList<MetadataCandidate> ParseSearchTracks(string? json)
    {
        var list = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var item in Json.Arr(doc.RootElement, "data"))
            if (ReadTrack(item) is { } c) list.Add(c.Candidate);
        return list;
    }

    /// <summary>Parses a <c>/track/{id}</c> or <c>/track/isrc:{code}</c> payload.</summary>
    public static MetadataCandidate? ParseTrack(string? json) => ParseTrackWithAlbumId(json)?.Candidate;

    internal static (MetadataCandidate Candidate, long? AlbumId)? ParseTrackWithAlbumId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return ReadTrack(doc.RootElement);
    }

    private static (MetadataCandidate Candidate, long? AlbumId)? ReadTrack(JsonElement t)
    {
        var id = Json.Long(t, "id");
        var title = Json.Str(t, "title");
        if (id is null || title.Length == 0) return null;
        var album = Json.Obj(t, "album");
        var (date, year) = MatchText.ParseDate(Json.Str(album, "release_date"));
        if (date.Length == 0) (date, year) = MatchText.ParseDate(Json.Str(t, "release_date"));
        var bpm = Json.Dbl(t, "bpm");
        var c = new MetadataCandidate
        {
            Provider = ProviderNames.Deezer,
            ProviderId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WebUrl = Json.Url(Json.Str(t, "link")),
            Title = title,
            Artist = Json.Str(Json.Obj(t, "artist"), "name"),
            Album = Json.Str(album, "title"),
            ReleaseDate = date,
            Year = year,
            TrackNumber = Json.Int(t, "track_position") is > 0 and var tn ? tn : null,
            DiscNumber = Json.Int(t, "disk_number") is > 0 and var dn ? dn : null,
            Isrc = Json.Str(t, "isrc"),
            Explicit = Json.Bool(t, "explicit_lyrics"),
            Bpm = bpm is > 0 ? (int)Math.Round(bpm.Value) : null,
            Duration = Json.Seconds(Json.Int(t, "duration")),
        };
        return (WithCover(c, album), Json.Long(album, "id"));
    }

    /// <summary>Adds album-level fields from an <c>/album/{id}</c> payload to a track candidate.</summary>
    public static MetadataCandidate ApplyAlbum(MetadataCandidate track, string albumJson)
    {
        using var doc = JsonDocument.Parse(albumJson);
        var a = doc.RootElement;
        if (Json.Str(a, "title").Length == 0) return track;
        // The track's nested album date is the original release Deezer shows in its UI; the
        // /album date can be a later re-delivery for re-released editions — keep the former.
        var (date, year) = track.ReleaseDate.Length > 0 ? (track.ReleaseDate, track.Year) : MatchText.ParseDate(Json.Str(a, "release_date"));
        var result = track with
        {
            Album = track.Album.Length > 0 ? track.Album : Json.Str(a, "title"),
            AlbumArtist = Json.Str(Json.Obj(a, "artist"), "name"),
            Genre = FirstGenre(a),
            Label = Json.Str(a, "label"),
            Barcode = Json.Str(a, "upc"),
            TrackCount = Json.Int(a, "nb_tracks") is > 0 and var n ? n : null,
            ReleaseDate = date,
            Year = year,
        };
        return result.ArtworkUrl is null ? WithCover(result, a) : result;
    }

    /// <summary>Parses a <c>/search/album</c> payload into light release candidates.</summary>
    public static IReadOnlyList<MetadataCandidate> ParseSearchAlbums(string? json)
    {
        var list = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var a in Json.Arr(doc.RootElement, "data"))
        {
            var id = Json.Long(a, "id");
            var title = Json.Str(a, "title");
            if (id is null || title.Length == 0) continue;
            var artist = Json.Str(Json.Obj(a, "artist"), "name");
            list.Add(WithCover(new MetadataCandidate
            {
                Provider = ProviderNames.Deezer,
                ProviderId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WebUrl = Json.Url(Json.Str(a, "link")),
                Album = title,
                Artist = artist,
                AlbumArtist = artist,
                TrackCount = Json.Int(a, "nb_tracks") is > 0 and var n ? n : null,
                Explicit = Json.Bool(a, "explicit_lyrics"),
            }, a));
        }
        return list;
    }

    /// <summary>Parses <c>/album/{id}</c> (+ optional <c>/album/{id}/tracks</c>, which carries the
    /// track/disc positions the embedded list lacks) into a full release candidate.</summary>
    public static MetadataCandidate? ParseAlbum(string albumJson, string? tracksJson)
    {
        using var doc = JsonDocument.Parse(albumJson);
        var a = doc.RootElement;
        var id = Json.Long(a, "id");
        var title = Json.Str(a, "title");
        if (id is null || title.Length == 0) return null;

        var tracks = new List<CandidateTrack>();
        if (!string.IsNullOrWhiteSpace(tracksJson))
        {
            using var tdoc = JsonDocument.Parse(tracksJson);
            tracks.AddRange(Json.Arr(tdoc.RootElement, "data").Select(ReadAlbumTrack));
        }
        if (tracks.Count == 0)
        {
            // Embedded list: no positions, but its order is the running order.
            var n = 0;
            tracks.AddRange(Json.Arr(Json.Obj(a, "tracks"), "data").Select(t => ReadAlbumTrack(t) with { TrackNumber = ++n }));
        }

        var (date, year) = MatchText.ParseDate(Json.Str(a, "release_date"));
        var artist = Json.Str(Json.Obj(a, "artist"), "name");
        var discs = tracks.Select(t => t.DiscNumber ?? 1).DefaultIfEmpty(1).Max();
        return WithCover(new MetadataCandidate
        {
            Provider = ProviderNames.Deezer,
            ProviderId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WebUrl = Json.Url(Json.Str(a, "link")),
            Album = title,
            Artist = artist,
            AlbumArtist = artist,
            ReleaseDate = date,
            Year = year,
            Genre = FirstGenre(a),
            Label = Json.Str(a, "label"),
            Barcode = Json.Str(a, "upc"),
            Explicit = Json.Bool(a, "explicit_lyrics"),
            TrackCount = Json.Int(a, "nb_tracks") is > 0 and var nb ? nb : tracks.Count > 0 ? tracks.Count : null,
            DiscCount = tracks.Count > 0 ? discs : null,
            Duration = Json.Seconds(Json.Int(a, "duration")),
            Tracks = tracks,
        }, a);
    }

    private static CandidateTrack ReadAlbumTrack(JsonElement t) => new()
    {
        Title = Json.Str(t, "title"),
        Artist = Json.Str(Json.Obj(t, "artist"), "name"),
        TrackNumber = Json.Int(t, "track_position") is > 0 and var tn ? tn : null,
        DiscNumber = Json.Int(t, "disk_number") is > 0 and var dn ? dn : null,
        Duration = Json.Seconds(Json.Int(t, "duration")),
        Isrc = Json.Str(t, "isrc"),
        Explicit = Json.Bool(t, "explicit_lyrics"),
    };

    private static string FirstGenre(JsonElement album)
        => Json.Arr(Json.Obj(album, "genres"), "data").Select(g => Json.Str(g, "name")).FirstOrDefault(s => s.Length > 0) ?? string.Empty;

    // cover_xl is the largest size the API names (1000 px); cover_medium (250 px) for lists.
    private static MetadataCandidate WithCover(MetadataCandidate c, JsonElement album)
    {
        var xl = Json.Url(Json.Str(album, "cover_xl"));
        var big = Json.Url(Json.Str(album, "cover_big"));
        var thumb = Json.Url(Json.Str(album, "cover_medium")) ?? big;
        if (xl is null && big is null) return c;
        return c with
        {
            ArtworkUrl = xl ?? big,
            ArtworkSize = xl is not null ? 1000 : 500,
            ArtworkThumbUrl = thumb,
        };
    }
}
