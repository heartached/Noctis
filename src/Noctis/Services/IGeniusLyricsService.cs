using Noctis.Models;

namespace Noctis.Services;

public interface IGeniusLyricsService
{
    /// <summary>
    /// Asks Genius for the song and returns its plain (untimed) lyrics when a hit is this song
    /// (title and artist), or null on a miss. Never synced: Genius has no timings.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
