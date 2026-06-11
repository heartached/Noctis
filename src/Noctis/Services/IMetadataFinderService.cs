using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Identifies tracks via acoustic fingerprint (Chromaprint <c>fpcalc</c> → AcoustID), with a
/// MusicBrainz text-search fallback when fingerprinting or an API key isn't available.
/// </summary>
public interface IMetadataFinderService
{
    /// <summary>True when an <c>fpcalc</c> binary was found (PATH or configured override).</summary>
    bool HasFingerprinting { get; }

    /// <summary>True when an AcoustID API key is configured.</summary>
    bool HasApiKey { get; }

    /// <summary>
    /// Returns tag suggestions for a track, best first. Uses fingerprint identification when
    /// available, otherwise a text search by the track's current tags.
    /// </summary>
    Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default);
}
