using Noctis.Models;

namespace Noctis.Services;

public enum AnimatedCoverScope
{
    Track,
    Album
}

public interface IAnimatedCoverService
{
    /// <summary>
    /// Returns the absolute path of the best animated cover for the track, or null.
    /// Lookup priority:
    ///   1. Track sidecar: <track>.mp4 / <track>.webm next to the audio file
    ///   2. Album sidecar: cover.mp4 / cover.webm in the track's folder
    ///   3. Track-scoped managed cache
    ///   4. Album-scoped managed cache
    /// </summary>
    string? Resolve(Track track);

    /// <summary>
    /// Copies sourcePath into the managed cache for the given scope, overwriting any existing entry.
    /// Returns the cache path.
    /// </summary>
    Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope);

    /// <summary>
    /// Removes the managed-cache entry for the given scope. Sidecar files are NEVER deleted.
    /// </summary>
    void Remove(Track track, AnimatedCoverScope scope);
}
