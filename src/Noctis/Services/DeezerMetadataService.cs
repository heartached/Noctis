using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// Deezer-backed tag lookup (keyless). Used as a redundancy fallback alongside MusicBrainz so
/// identification still works when one source is down or returns nothing. All work runs off the
/// UI thread (callers await from a background context).
/// </summary>
public sealed class DeezerMetadataService
{
    private readonly HttpClient _http;

    public DeezerMetadataService(HttpClient http) => _http = http;

    /// <summary>Returns Deezer tag suggestions for the given tags, best first; empty on failure.</summary>
    public async Task<IReadOnlyList<TagSuggestion>> SearchAsync(
        string artist, string title, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
            return Array.Empty<TagSuggestion>();

        try
        {
            var json = await SearchJsonWithAlbumFallbackAsync(artist, title, album, ct).ConfigureAwait(false);
            return json is null ? Array.Empty<TagSuggestion>() : DeezerApi.ParseSearch(json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<TagSuggestion>(); }
    }

    /// <summary>
    /// Runs a Deezer search, first with a normalized album hint (edition suffixes stripped) and,
    /// if that returns nothing, again with just artist + title. The album hint sharpens matches for
    /// common tracks while the drop-album retry keeps popular/deluxe albums from coming back empty.
    /// </summary>
    private async Task<string?> SearchJsonWithAlbumFallbackAsync(
        string artist, string title, string album, CancellationToken ct)
    {
        var normalizedAlbum = AlbumTitleNormalizer.Normalize(album);

        var json = await GetStringAsync(DeezerApi.BuildSearchUrl(artist, title, normalizedAlbum), ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(normalizedAlbum) && DeezerApi.FirstTrackId(json ?? string.Empty) is null)
        {
            var retry = await GetStringAsync(DeezerApi.BuildSearchUrl(artist, title, string.Empty), ct)
                .ConfigureAwait(false);
            if (DeezerApi.FirstTrackId(retry ?? string.Empty) is not null) return retry;
        }

        return json;
    }

    /// <summary>
    /// Returns a fully-populated tag suggestion for the best Deezer match of (artist, title, album),
    /// following the search hit through <c>/track/{id}</c> and <c>/album/{id}</c> to fill genre,
    /// track #, disc #, bpm, isrc, track count, album artist and year. Null on miss/failure.
    /// </summary>
    public async Task<TagSuggestion?> EnrichAsync(
        string artist, string title, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            return null;

        try
        {
            // 1. Find the best matching track id via search (album hint normalized, with a
            //    drop-album retry so deluxe/anniversary editions still resolve).
            var searchJson = await SearchJsonWithAlbumFallbackAsync(artist, title, album, ct).ConfigureAwait(false);
            if (searchJson is null) return null;
            var bestId = DeezerApi.FirstTrackId(searchJson);
            if (bestId is null) return null;

            // 2. Pull the full track record.
            var trackJson = await GetStringAsync(DeezerApi.BuildTrackUrl(bestId.Value), ct).ConfigureAwait(false);
            var track = trackJson is null ? null : DeezerApi.ParseTrack(trackJson);
            if (track is null) return null;

            // 3. Pull the album record for genre/track-count/year/album-artist.
            DeezerApi.DeezerAlbum? alb = null;
            if (track.AlbumId > 0)
            {
                var albumJson = await GetStringAsync(DeezerApi.BuildAlbumUrl(track.AlbumId), ct).ConfigureAwait(false);
                if (albumJson is not null) alb = DeezerApi.ParseAlbum(albumJson);
            }

            return new TagSuggestion(
                Title: track.Title,
                Artist: track.Artist,
                Album: alb?.Title ?? album,
                // The nested album date (original release, matching Deezer's UI) wins over the
                // /album/{id} date, which can be a later re-delivery date for re-releases.
                Year: track.AlbumYear ?? alb?.Year,
                Confidence: 0.0,
                Source: "Deezer",
                AlbumArtist: track.AlbumArtist ?? alb?.AlbumArtist,
                Genre: alb?.Genre,
                TrackNumber: track.TrackNumber,
                TrackCount: alb?.TrackCount,
                DiscNumber: track.DiscNumber,
                Bpm: track.Bpm,
                Isrc: track.Isrc);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
