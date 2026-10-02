using Noctis.Models;

namespace Noctis.Services;

public interface IMusixmatchService
{
    /// <summary>
    /// Matches the track on Musixmatch and returns its lyrics — word-synced ELRC from
    /// richsync when published, else line-synced LRC, else plain — or null on a definitive
    /// miss (no match, a different song, or no lyrics). Throws
    /// <see cref="LyricsProviderException"/> when the provider could not answer
    /// (network failure, timeout, bad response, token refused / captcha).
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
