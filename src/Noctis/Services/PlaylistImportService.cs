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

    public PlaylistImportService(ILibraryService library, IPersistenceService persistence, HttpClient http)
    {
        _library = library;
        _persistence = persistence;
        _http = http;
    }

    public Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        var library = _library.Tracks.ToList();
        return Task.Run(() => MatchAgainst(PlaylistImportParser.Parse(filePath), library, ct), ct);
    }

    public async Task<PlaylistImportPreview> AnalyzeLinkAsync(string url, CancellationToken ct = default)
    {
        if (!DeezerPlaylistLink.TryParse(url, out var kind, out var id))
            throw new ArgumentException("Not a Deezer playlist or album link.", nameof(url));

        var library = _library.Tracks.ToList();
        var parsed = await DeezerPlaylistLink.FetchAllAsync(kind, id, FetchAsync, ct).ConfigureAwait(false);
        return await Task.Run(() => MatchAgainst(parsed, library, ct), ct).ConfigureAwait(false);
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
