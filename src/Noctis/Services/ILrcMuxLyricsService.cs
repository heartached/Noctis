using Noctis.Models;

namespace Noctis.Services;

public interface ILrcMuxLyricsService
{
    /// <summary>
    /// Asks lrcmux for the track and returns its answer — word timings as ELRC, line timings as LRC, or
    /// plain text — when it is this song (title, artist, duration within 5 s), or null on a miss.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
