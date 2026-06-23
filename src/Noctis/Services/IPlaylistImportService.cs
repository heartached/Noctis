namespace Noctis.Services;

/// <summary>Result of analyzing an import file against the local library.</summary>
public sealed class PlaylistImportPreview
{
    public string SuggestedName { get; init; } = "Imported Playlist";
    public IReadOnlyList<Guid> MatchedTrackIds { get; init; } = Array.Empty<Guid>();
    public IReadOnlyList<string> MatchedLabels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingLabels { get; init; } = Array.Empty<string>();
    public int TotalEntries => MatchedLabels.Count + MissingLabels.Count;
}

/// <summary>
/// Imports streaming-service playlist exports (Exportify CSV / TuneMyMusic JSON), fuzzy-matches
/// the entries against the local library, and creates a playlist from the matches.
/// </summary>
public interface IPlaylistImportService
{
    /// <summary>Parses and matches the file off the UI thread, returning a preview.</summary>
    Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default);

    /// <summary>Creates and persists a playlist from the matched track IDs. Returns its ID.</summary>
    Task<Guid> CreateAsync(string name, IReadOnlyList<Guid> matchedTrackIds, CancellationToken ct = default);
}
