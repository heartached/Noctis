using Noctis.Models;

namespace Noctis.Services;

public interface ILrcLibService
{
    /// <summary>
    /// Direct get with duration matching — returns best match, or null on a
    /// definitive miss (404). Throws <see cref="LyricsProviderException"/> when
    /// the provider could not answer (network failure, timeout, bad response).
    /// </summary>
    Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Broader search returning multiple results; empty list on a definitive
    /// miss. Throws <see cref="LyricsProviderException"/> when the provider
    /// could not answer (network failure, timeout, bad response).
    /// </summary>
    Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default);
}
