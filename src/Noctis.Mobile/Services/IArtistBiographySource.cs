using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Where the artist page's biography paragraph comes from: the album description's twin
/// (<see cref="IAlbumDescriptionSource"/>). Asked once when an artist page opens, cancelled when
/// it closes; null (nothing known, a placeholder name, offline, timed out) hides the paragraph.
/// Tests inject a fake; the Android head injects <see cref="LastFmArtistBiographySource"/>.
/// </summary>
public interface IArtistBiographySource
{
    /// <summary>The artist's biography as plain text (paragraphs separated by blank lines), or
    /// null. Never throws for a lookup that failed; cancelling may throw
    /// <see cref="OperationCanceledException"/>.</summary>
    Task<string?> GetBiographyAsync(string artist, CancellationToken ct);
}

/// <summary>
/// Last.fm's artist bio, looked up by the phone itself (the artist's name only) through
/// <see cref="LastFmArtistBiographies"/>, the album descriptions' pattern: same key, same cleaning,
/// kept in the phone's data directory so a bio seen once reads offline; a failed or slow lookup
/// (<see cref="LastFmAlbumDescriptionSource.DefaultTimeout"/>) yields null and is not cached. A
/// joint credit ("Bruno Mars, Anderson .Paak &amp; Silk Sonic") with no bio of its own falls back
/// to its first artist (Track.GetPrimaryArtist, the Settings separators), as the album text does.
/// "Unknown Artist" and "Various Artists" are never asked.
/// </summary>
public sealed class LastFmArtistBiographySource : IArtistBiographySource
{
    private readonly LastFmArtistBiographies _biographies;
    private readonly TimeSpan _timeout;

    public LastFmArtistBiographySource(LastFmArtistBiographies biographies, TimeSpan? timeout = null)
    {
        _biographies = biographies;
        _timeout = timeout ?? LastFmAlbumDescriptionSource.DefaultTimeout;
    }

    /// <summary>The source over the phone's own cache file, cache/lastfm_artist_bios.json under
    /// <paramref name="dataDirectory"/>, beside the album descriptions' file.</summary>
    public static LastFmArtistBiographySource Create(string dataDirectory, HttpClient http) =>
        new(new LastFmArtistBiographies(http, Path.Combine(dataDirectory, "cache", "lastfm_artist_bios.json"),
            rememberFailedLookups: false));

    public async Task<string?> GetBiographyAsync(string artist, CancellationToken ct)
    {
        if (!LastFmArtistBiographies.IsLookupName(artist)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            // The whole lookup off the UI thread, as the album description's.
            var text = await Task.Run(async () =>
            {
                var found = await _biographies.GetAsync(artist, preferFullText: true, timeout.Token);
                var primary = Track.GetPrimaryArtist(artist);
                if (string.IsNullOrWhiteSpace(found) && primary.Length > 0
                    && !string.Equals(primary, artist.Trim(), StringComparison.OrdinalIgnoreCase))
                    found = await _biographies.GetAsync(primary, preferFullText: true, timeout.Token);
                return found;
            }, timeout.Token);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DebugLog.Write("Artist", "Biography lookup timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write("Artist", $"Biography lookup failed: {ex.Message}");
            return null;
        }
    }
}
