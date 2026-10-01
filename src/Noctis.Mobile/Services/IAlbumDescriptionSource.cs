using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Where the album page's description paragraph comes from. Asked once when an album page
/// opens, cancelled when it closes; null (nothing known, offline, timed out) hides the
/// paragraph. Tests inject a fake; the Android head injects <see cref="LastFmAlbumDescriptionSource"/>.
/// </summary>
public interface IAlbumDescriptionSource
{
    /// <summary>The album's description as plain text (paragraphs separated by blank lines),
    /// or null. Never throws for a lookup that failed; cancelling may throw
    /// <see cref="OperationCanceledException"/>.</summary>
    Task<string?> GetDescriptionAsync(string artist, string album, CancellationToken ct);
}

/// <summary>
/// The desktop's source: Last.fm's album wiki, looked up by the phone itself (artist and album
/// name only), through the shared <see cref="LastFmAlbumDescriptions"/>: same cleaning, same
/// cache format and keys, kept in the phone's data directory so a description seen once reads
/// offline. A failed or slow lookup (<see cref="DefaultTimeout"/>) yields null and is not cached, so a
/// later visit asks again. Last.fm files a collaboration under one name: when the album's full
/// credit ("Bruno Mars, Anderson .Paak &amp; Silk Sonic") has no wiki, its first artist is asked
/// (Track.GetPrimaryArtist, the Settings separators), which found An Evening with Silk Sonic.
/// </summary>
public sealed class LastFmAlbumDescriptionSource : IAlbumDescriptionSource
{
    /// <summary>How long a lookup may take before the page gives up on it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly LastFmAlbumDescriptions _descriptions;
    private readonly TimeSpan _timeout;

    public LastFmAlbumDescriptionSource(LastFmAlbumDescriptions descriptions, TimeSpan? timeout = null)
    {
        _descriptions = descriptions;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The source over the phone's own cache file, cache/lastfm_album_descriptions.json
    /// under <paramref name="dataDirectory"/> (the desktop's name, under its data root).</summary>
    public static LastFmAlbumDescriptionSource Create(string dataDirectory, HttpClient http) =>
        new(new LastFmAlbumDescriptions(http, Path.Combine(dataDirectory, "cache", "lastfm_album_descriptions.json"),
            rememberFailedLookups: false));

    public async Task<string?> GetDescriptionAsync(string artist, string album, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            // The whole lookup off the UI thread: the cache file read and write, the JSON parse
            // and the HTML cleaning all continue wherever their awaits resume.
            var text = await Task.Run(async () =>
            {
                var found = await _descriptions.GetAsync(artist, album, preferFullText: true, timeout.Token);
                var primary = Track.GetPrimaryArtist(artist);
                if (string.IsNullOrWhiteSpace(found) && primary.Length > 0
                    && !string.Equals(primary, artist.Trim(), StringComparison.OrdinalIgnoreCase))
                    found = await _descriptions.GetAsync(primary, album, preferFullText: true, timeout.Token);
                return found;
            }, timeout.Token);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DebugLog.Write("Album", "Description lookup timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write("Album", $"Description lookup failed: {ex.Message}");
            return null;
        }
    }
}
