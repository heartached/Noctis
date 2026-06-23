using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Identifies tracks via online text search (Deezer first, MusicBrainz fallback) using the
/// track's current tags.
/// </summary>
public interface IMetadataFinderService
{
    /// <summary>
    /// Returns tag suggestions for a track, best first, from a text search by the track's
    /// current tags.
    /// </summary>
    Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default);
}
