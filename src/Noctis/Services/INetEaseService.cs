using Noctis.Models;

namespace Noctis.Services;

public interface INetEaseService
{
    /// <summary>
    /// Searches NetEase Cloud Music for lyrics matching the given artist and track name.
    /// Returns the best match or null.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds);
}
