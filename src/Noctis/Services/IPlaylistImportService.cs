using Noctis.Models;

namespace Noctis.Services;

/// <summary>Result of analyzing an import file against the local library.</summary>
public sealed class PlaylistImportPreview
{
    public string SuggestedName { get; init; } = "Imported Playlist";
    public IReadOnlyList<Guid> MatchedTrackIds { get; init; } = Array.Empty<Guid>();
    public IReadOnlyList<string> MatchedLabels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingLabels { get; init; } = Array.Empty<string>();
    /// <summary>Every entry in file order with its match, or its best candidates when unmatched
    /// (the dialog's review step). Empty from callers that only report labels.</summary>
    public IReadOnlyList<MatchResult> Matches { get; init; } = Array.Empty<MatchResult>();
    public int TotalEntries => MatchedLabels.Count + MissingLabels.Count;
}

/// <summary>
/// Imports streaming-service playlist exports (Exportify CSV / TuneMyMusic JSON / m3u) or a
/// pasted Deezer playlist/album link, fuzzy-matches the entries against the local library,
/// and creates a playlist from the matches.
/// </summary>
public interface IPlaylistImportService
{
    /// <summary>Parses and matches the file off the UI thread, returning a preview.</summary>
    Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default);

    /// <summary>Fetches a public Deezer playlist/album by share link and matches it.
    /// Throws <see cref="ArgumentException"/> when the text is not a Deezer link.</summary>
    Task<PlaylistImportPreview> AnalyzeLinkAsync(string url, CancellationToken ct = default);

    /// <summary>Creates and persists a playlist from the matched track IDs. Returns its ID.</summary>
    Task<Guid> CreateAsync(string name, IReadOnlyList<Guid> matchedTrackIds, CancellationToken ct = default);

    /// <summary>Library songs for the review step's search (title, artist or album words), best first.</summary>
    IReadOnlyList<Track> SearchLibrary(string query, int max) => Array.Empty<Track>();
}
