using Noctis.Models;

namespace Noctis.Services;

public interface IYouTubeMusicLyricsService
{
    /// <summary>
    /// Finds the track on YouTube Music and returns its lyrics — line-synced when YouTube
    /// Music has timed lyrics, else plain — or null on a definitive miss. Throws
    /// <see cref="LyricsProviderException"/> when the provider could not answer
    /// (network failure, timeout, bad response).
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
