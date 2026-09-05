using System.Net.Http;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Parsing and matching are delegated to the pure <see cref="PlaylistImportParser"/> /
/// <see cref="DeezerPlaylistLink"/> and <see cref="FuzzyTrackMatcher"/>; this service reads the
/// file (or fetches the link) off-thread and owns playlist persistence.
/// </summary>
public sealed class PlaylistImportService : IPlaylistImportService
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly HttpClient _http;
    private readonly ITidalAuthService _tidal;

    public PlaylistImportService(ILibraryService library, IPersistenceService persistence, HttpClient http, ITidalAuthService tidal)
    {
        _library = library;
        _persistence = persistence;
        _http = http;
        _tidal = tidal;
    }

    public Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        var library = _library.Tracks.ToList();
        return Task.Run(() => MatchAgainst(PlaylistImportParser.Parse(filePath), library, ct), ct);
    }

    public async Task<PlaylistImportPreview> AnalyzeLinkAsync(string url, CancellationToken ct = default)
    {
        var library = _library.Tracks.ToList();
        PlaylistImportParseResult parsed;
        if (DeezerPlaylistLink.TryParse(url, out var kind, out var id))
            parsed = await DeezerPlaylistLink.FetchAllAsync(kind, id, FetchAsync, ct).ConfigureAwait(false);
        else if (TidalPlaylistLink.TryParse(url, out var tidalKind, out var tidalId))
            parsed = await FetchTidalAsync(tidalKind, tidalId, ct).ConfigureAwait(false);
        else
            throw new ArgumentException("Not a Deezer or TIDAL playlist/album link.", nameof(url));

        return await Task.Run(() => MatchAgainst(parsed, library, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TIDAL needs the user's bearer token for every call. No token (never signed in, or the
    /// refresh was refused) surfaces as <see cref="TidalNotConnectedException"/> so the dialog
    /// can offer the sign-in; a 401 mid-walk forgets the dead session and does the same.
    /// </summary>
    private async Task<PlaylistImportParseResult> FetchTidalAsync(TidalLinkKind kind, string id, CancellationToken ct)
    {
        var token = await _tidal.GetAccessTokenAsync(ct).ConfigureAwait(false)
                    ?? throw new TidalNotConnectedException();
        var unauthorized = false;

        async Task<string?> Fetch(string url, CancellationToken c)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/vnd.api+json");
            using var resp = await _http.SendAsync(req, c).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) unauthorized = true;
            if (!resp.IsSuccessStatusCode) return null;
            return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: c).ConfigureAwait(false);
        }

        var parsed = await TidalPlaylistLink.FetchAllAsync(kind, id, CountryCode(), Fetch, ct).ConfigureAwait(false);
        if (unauthorized && parsed.Entries.Count == 0)
        {
            _tidal.Disconnect();
            throw new TidalNotConnectedException();
        }
        return parsed;
    }

    /// <summary>TIDAL's catalogue is per market; the OS region is the best guess without a user.read scope.</summary>
    private static string CountryCode()
    {
        var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
        return region.Length == 2 && region.All(char.IsAsciiLetterUpper) ? region : "US";
    }

    private async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        // English names, same as the metadata search (Deezer localises by Accept-Language/IP).
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.AcceptLanguage.ParseAdd("en");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
    }

    private static PlaylistImportPreview MatchAgainst(PlaylistImportParseResult parsed, List<Track> library, CancellationToken ct)
    {
        var matches = FuzzyTrackMatcher.Match(parsed.Entries, library, ct: ct);

        var matchedIds = new List<Guid>();
        var matchedLabels = new List<string>();
        var missing = new List<string>();

        foreach (var m in matches)
        {
            var label = Label(m.Entry);
            if (m.Match is not null)
            {
                matchedIds.Add(m.Match.Id);
                matchedLabels.Add(label);
            }
            else
            {
                missing.Add(label);
            }
        }

        return new PlaylistImportPreview
        {
            SuggestedName = parsed.SuggestedName,
            MatchedTrackIds = matchedIds,
            MatchedLabels = matchedLabels,
            MissingLabels = missing
        };
    }

    public async Task<Guid> CreateAsync(string name, IReadOnlyList<Guid> matchedTrackIds, CancellationToken ct = default)
    {
        var playlists = await _persistence.LoadPlaylistsAsync();
        var playlist = new Playlist
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Imported Playlist" : name.Trim(),
            TrackIds = matchedTrackIds.ToList()
        };
        playlists.Add(playlist);
        await _persistence.SavePlaylistsAsync(playlists);
        return playlist.Id;
    }

    private static string Label(PlaylistImportEntry e)
        => string.IsNullOrWhiteSpace(e.Artist) ? e.Title : $"{e.Artist} – {e.Title}";
}
