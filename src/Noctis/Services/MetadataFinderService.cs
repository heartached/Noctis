using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Orchestrates text-based track identification. Pure URL/parsing logic lives in
/// <see cref="MusicBrainzApi"/> / <see cref="DeezerApi"/>; this service owns the side effects:
/// the HTTP calls and rate limiting. Deezer (keyless, fast, reliable for mainstream music) is
/// tried first, with MusicBrainz as the fallback. All work runs off the UI thread (callers await
/// it from a background context).
/// </summary>
public sealed class MetadataFinderService : IMetadataFinderService
{
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly DeezerMetadataService _deezer;

    // MusicBrainz asks for <= 1 request/second. A single gate keeps us well-behaved.
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public MetadataFinderService(HttpClient http, Func<AppSettings> settings, DeezerMetadataService deezer)
    {
        _http = http;
        _settings = settings;
        _deezer = deezer;
    }

    public async Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
    {
        var settings = _settings();

        // 1. Deezer text search — primary source (keyless, fast, reliable for popular music).
        if (settings.DeezerEnabled)
        {
            try
            {
                var hits = await _deezer.SearchAsync(track.PrimaryArtist, track.Title, track.Album, ct)
                    .ConfigureAwait(false);
                if (hits.Count > 0) return hits;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to MusicBrainz */ }
        }

        // 2. MusicBrainz text search — fallback. The album is normalized (edition suffixes
        //    stripped) so deluxe/anniversary releases still match a canonical recording.
        if (settings.MusicBrainzEnabled)
        {
            try
            {
                var album = AlbumTitleNormalizer.Normalize(track.Album);
                var url = MusicBrainzApi.BuildRecordingSearchUrl(track.PrimaryArtist, track.Title, album);
                var json = await GetAsync(url, ct).ConfigureAwait(false);
                var hits = MusicBrainzApi.ParseRecordingSearch(json);
                if (hits.Count > 0) return hits;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* no source produced a match */ }
        }

        return Array.Empty<TagSuggestion>();
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await _rateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var since = DateTime.UtcNow - _lastRequestUtc;
            if (since < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - since, ct).ConfigureAwait(false);

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rateGate.Release();
        }
    }
}
