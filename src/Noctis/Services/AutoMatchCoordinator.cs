using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Fans out a single "Auto-match" request to the enabled metadata providers and merges the
/// results into one <see cref="AutoMatchProposal"/>: tags via <see cref="IMetadataFinderService"/>
/// (which already honours the AcoustID/MusicBrainz/Deezer toggles), cover art via
/// <see cref="IAlbumArtworkSearch"/>, and lyrics via <see cref="ILrcLibService"/>. Each provider is
/// independent — one failing or returning nothing never sinks the others. All work runs off the
/// UI thread (callers await from a background context).
/// </summary>
public sealed class AutoMatchCoordinator
{
    private readonly IMetadataFinderService _finder;
    private readonly IAlbumArtworkSearch _artwork;
    private readonly ILrcLibService _lyrics;
    private readonly Func<AppSettings> _settings;

    public AutoMatchCoordinator(
        IMetadataFinderService finder,
        IAlbumArtworkSearch artwork,
        ILrcLibService lyrics,
        Func<AppSettings> settings)
    {
        _finder = finder;
        _artwork = artwork;
        _lyrics = lyrics;
        _settings = settings;
    }

    /// <summary>
    /// Returns a merged proposal for the track. Tag identification owns its own provider toggles;
    /// artwork and lyrics are gated here by their respective settings.
    /// </summary>
    public async Task<AutoMatchProposal> MatchAsync(Track track, CancellationToken ct = default)
    {
        var settings = _settings();

        var tags = await TryTagsAsync(track, ct).ConfigureAwait(false);
        var art = settings.ITunesEnabled
            ? await TryArtworkAsync(track, ct).ConfigureAwait(false)
            : null;
        var (synced, plain) = settings.LrcLibEnabled
            ? await TryLyricsAsync(track).ConfigureAwait(false)
            : (null, null);

        return new AutoMatchProposal(tags, art, synced, plain);
    }

    private async Task<TagSuggestion?> TryTagsAsync(Track track, CancellationToken ct)
    {
        try
        {
            var hits = await _finder.IdentifyAsync(track, ct).ConfigureAwait(false);
            return hits.FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<ITunesArtworkService.ArtworkCandidate?> TryArtworkAsync(Track track, CancellationToken ct)
    {
        try
        {
            var artist = !string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.AlbumArtist : track.PrimaryArtist;
            var results = await _artwork.SearchAlbumsAsync(artist, track.Album, 1, ct).ConfigureAwait(false);
            return results.FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<(string? Synced, string? Plain)> TryLyricsAsync(Track track)
    {
        try
        {
            var result = await _lyrics
                .GetLyricsAsync(track.PrimaryArtist, track.Title, track.Duration.TotalSeconds)
                .ConfigureAwait(false);

            if (result is null || !result.HasLyrics)
            {
                var alts = await _lyrics.SearchLyricsAsync(track.PrimaryArtist, track.Title).ConfigureAwait(false);
                result = alts.FirstOrDefault(r => r.HasLyrics);
            }

            return result is null
                ? (null, null)
                : (result.HasSyncedLyrics ? result.SyncedLyrics : null, result.PlainLyrics);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }
}
