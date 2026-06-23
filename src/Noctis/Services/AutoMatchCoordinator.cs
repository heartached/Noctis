using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Resolves a single "Search metadata" request into one best <see cref="TagSuggestion"/>. The
/// identify chain (AcoustID/MusicBrainz/Deezer, honouring their own toggles) picks the best
/// title/artist/album/year; a Deezer enrichment pass then fills the rich fields (genre, track #,
/// disc #, bpm, isrc, track count, album artist). Identify wins for the core fields; enrichment
/// only fills blanks. All work runs off the UI thread (callers await from a background context).
/// </summary>
public sealed class AutoMatchCoordinator
{
    private readonly IMetadataFinderService _finder;
    private readonly DeezerMetadataService _deezer;
    private readonly Func<AppSettings> _settings;

    public AutoMatchCoordinator(
        IMetadataFinderService finder,
        DeezerMetadataService deezer,
        Func<AppSettings> settings)
    {
        _finder = finder;
        _deezer = deezer;
        _settings = settings;
    }

    /// <summary>Returns the best merged tag suggestion for the track, or null when nothing matched.</summary>
    public async Task<TagSuggestion?> MatchAsync(Track track, CancellationToken ct = default)
    {
        var settings = _settings();

        var identify = await TryIdentifyAsync(track, ct).ConfigureAwait(false);

        TagSuggestion? enrich = null;
        if (settings.DeezerEnabled)
        {
            var artist = identify?.Artist is { Length: > 0 } a ? a : track.PrimaryArtist;
            var title = identify?.Title is { Length: > 0 } t ? t : track.Title;
            var album = identify?.Album is { Length: > 0 } al ? al : track.Album;
            enrich = await TryEnrichAsync(artist, title, album, ct).ConfigureAwait(false);
        }

        return Merge(identify, enrich);
    }

    /// <summary>Identify wins for core fields; enrich fills any field identify left blank.</summary>
    public static TagSuggestion? Merge(TagSuggestion? identify, TagSuggestion? enrich)
    {
        if (identify is null) return enrich;
        if (enrich is null) return identify;

        return identify with
        {
            Year = identify.Year ?? enrich.Year,
            AlbumArtist = Coalesce(identify.AlbumArtist, enrich.AlbumArtist),
            Genre = Coalesce(identify.Genre, enrich.Genre),
            TrackNumber = identify.TrackNumber ?? enrich.TrackNumber,
            TrackCount = identify.TrackCount ?? enrich.TrackCount,
            DiscNumber = identify.DiscNumber ?? enrich.DiscNumber,
            Bpm = identify.Bpm ?? enrich.Bpm,
            Isrc = Coalesce(identify.Isrc, enrich.Isrc),
        };
    }

    private static string? Coalesce(string? a, string? b)
        => string.IsNullOrWhiteSpace(a) ? b : a;

    private async Task<TagSuggestion?> TryIdentifyAsync(Track track, CancellationToken ct)
    {
        try
        {
            var hits = await _finder.IdentifyAsync(track, ct).ConfigureAwait(false);
            return hits.FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<TagSuggestion?> TryEnrichAsync(string artist, string title, string album, CancellationToken ct)
    {
        try { return await _deezer.EnrichAsync(artist, title, album, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
