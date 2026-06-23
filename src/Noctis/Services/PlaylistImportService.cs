using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Parsing and matching are delegated to the pure <see cref="PlaylistImportParser"/> and
/// <see cref="FuzzyTrackMatcher"/>; this service reads the file off-thread and owns playlist
/// persistence.
/// </summary>
public sealed class PlaylistImportService : IPlaylistImportService
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;

    public PlaylistImportService(ILibraryService library, IPersistenceService persistence)
    {
        _library = library;
        _persistence = persistence;
    }

    public Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        var library = _library.Tracks.ToList();
        return Task.Run(() =>
        {
            var parsed = PlaylistImportParser.Parse(filePath);
            var matches = FuzzyTrackMatcher.Match(parsed.Entries, library);

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
        }, ct);
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
