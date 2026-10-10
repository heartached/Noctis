using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Orchestrates text-based track identification. Pure URL/parsing logic lives in
/// <see cref="MusicBrainzApi"/> / <see cref="DeezerApi"/>; this service owns the side effects:
/// the HTTP calls and rate limiting. Deezer (keyless, fast, reliable for mainstream music) is
/// tried first, with MusicBrainz as the fallback when Deezer has no strong match. All work runs
/// off the UI thread (callers await it from a background context).
/// </summary>
public sealed class MetadataFinderService : IMetadataFinderService
{
    /// <summary>A Deezer best hit under this similarity is no answer: MusicBrainz is asked too.
    /// (It used to be skipped whenever Deezer returned anything at all.)</summary>
    public const double StrongMatch = 0.75;

    private readonly Func<AppSettings> _settings;
    private readonly DeezerMetadataService _deezer;
    // Shared MusicBrainz pacer + a User-Agent with the project URL (MusicBrainz throttles
    // agents without contact info; the app-wide client sends a bare "Noctis/1.0").
    private readonly MetadataSearch.ProviderHttp _musicBrainz;

    public MetadataFinderService(HttpClient http, Func<AppSettings> settings, DeezerMetadataService deezer)
    {
        _settings = settings;
        _deezer = deezer;
        _musicBrainz = new MetadataSearch.ProviderHttp(http, MetadataSearch.RequestPacer.MusicBrainz);
    }

    public async Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
    {
        var settings = _settings();
        var results = new List<TagSuggestion>();

        // 1. Deezer text search — primary source (keyless, fast, reliable for popular music).
        if (settings.DeezerEnabled)
        {
            try
            {
                var hits = await _deezer.SearchAsync(track.PrimaryArtist, track.Title, track.Album, ct)
                    .ConfigureAwait(false);
                // Deezer's API gives no relevance score — compute a real confidence
                // against the track's current tags so callers can rank and gate on
                // it (raw API order auto-applied wrong-track tags at "0%").
                results.AddRange(Rescore(track, hits));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to MusicBrainz */ }
        }

        // 2. MusicBrainz text search — when Deezer has no convincing answer. The album is
        //    normalized (edition suffixes stripped) so deluxe/anniversary releases still match a
        //    canonical recording.
        if (settings.MusicBrainzEnabled && (results.Count == 0 || results.Max(r => r.Confidence) < StrongMatch))
        {
            try
            {
                var album = AlbumTitleNormalizer.Normalize(track.Album);
                var url = MusicBrainzApi.BuildRecordingSearchUrl(track.PrimaryArtist, track.Title, album);
                var json = await _musicBrainz.GetStringAsync(url, ct).ConfigureAwait(false) ?? string.Empty;
                // MusicBrainz's "score" is search relevance (100 for a compilation that merely
                // contains the title), not similarity: rescore like Deezer, or the bulk finder
                // auto-applies any hit at "100%".
                results.AddRange(Rescore(track, MusicBrainzApi.ParseRecordingSearch(json)));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* no source produced a match */ }
        }

        return results.OrderByDescending(h => h.Confidence).ToList();
    }

    private static IEnumerable<TagSuggestion> Rescore(Track track, IEnumerable<TagSuggestion> hits)
        => hits.Select(h => h with
        {
            Confidence = FuzzyTrackMatcher.TagSimilarity(track.Title, track.PrimaryArtist, h.Title, h.Artist)
        });
}
