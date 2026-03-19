using Noctis.Models;

namespace Noctis.Services;

public interface ILrcLibService
{
    /// <summary>
    /// Direct get with duration matching — returns best match or null.
    /// </summary>
    Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds);

    /// <summary>
    /// Broader search returning multiple results.
    /// </summary>
    Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName);
}
