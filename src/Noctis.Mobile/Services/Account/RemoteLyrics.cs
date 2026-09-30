using System.Text;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// A desktop song's lyrics as the desktop has them (getNoctisLyrics): the texts of its .ttml /
/// .elrc / .lrc sidecars, and the library's stored synced (LRC) and plain lyrics. Null = none.
/// </summary>
public sealed record RemoteLyrics(string? Ttml, string? Elrc, string? Lrc, string? Synced, string? Plain)
{
    /// <summary>Largest text kept, in UTF-8 bytes (the desktop serves nothing bigger either).</summary>
    public const int MaxTextBytes = 2 * 1024 * 1024;

    public static RemoteLyrics Empty { get; } = new(null, null, null, null, null);

    public bool IsEmpty => Ttml is null && Elrc is null && Lrc is null && Synced is null && Plain is null;

    /// <summary>The sidecar text for a loader extension (".ttml", ".elrc", ".lrc"), or null.</summary>
    public string? SidecarText(string extension) => extension.ToLowerInvariant() switch
    {
        ".ttml" => Ttml,
        ".elrc" => Elrc,
        ".lrc" => Lrc,
        _ => null,
    };

    /// <summary>Untrusted text → itself when non-blank and at most <see cref="MaxTextBytes"/> as UTF-8, else null.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextBytes) return null;
        return Encoding.UTF8.GetByteCount(text) <= MaxTextBytes ? text : null;
    }

    /// <summary>Every field through <see cref="Clean"/> (a copy read back from disk is untrusted too).</summary>
    public RemoteLyrics Cleaned() => new(Clean(Ttml), Clean(Elrc), Clean(Lrc), Clean(Synced), Clean(Plain));
}

/// <summary>
/// Where the phone's lyrics page gets a desktop song's lyrics and local copy. Implemented by
/// <see cref="NoctisAccountService"/>; read by <see cref="RemoteLyricsFileAccess"/>.
/// </summary>
public interface IRemoteLyricsSource
{
    /// <summary>
    /// The desktop's lyrics for one of its songs. Blocking: call it off the UI thread. The phone's
    /// saved copy when it is current (fetched since the last sync); otherwise asked of the desktop,
    /// waiting at most <paramref name="timeout"/>, and on any failure the saved copy however old.
    /// Null when there are none (or none known).
    /// </summary>
    RemoteLyrics? GetLyrics(Guid trackId, TimeSpan timeout);

    /// <summary>The downloaded file of a desktop song, or null when it is streamed only.</summary>
    string? DownloadedPath(Guid trackId);
}
