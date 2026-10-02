using Noctis.Models;

namespace Noctis.Services;

public interface IKugouLyricsService
{
    /// <summary>
    /// Searches Kugou for the track and returns its KRC lyrics as word-synced ELRC, or null
    /// on a definitive miss (no matching song, or no lyrics for it). Throws
    /// <see cref="LyricsProviderException"/> when the provider could not answer
    /// (network failure, timeout, bad response).
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
