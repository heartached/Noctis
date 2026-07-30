using Noctis.Models;

namespace Noctis.Services;

public interface INetEaseService
{
    /// <summary>
    /// Searches NetEase Cloud Music for lyrics matching the given artist and track name.
    /// Returns the best match, or null on a definitive miss. Throws
    /// <see cref="LyricsProviderException"/> when the provider could not answer
    /// (network failure, timeout, bad response).
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
