using System.Text.Json;

namespace Noctis.Mobile.Services.Account;

internal sealed partial class NoctisServerClient
{
    /// <summary>
    /// getNoctisLyrics: a desktop song's lyrics (sidecar texts and the stored synced/plain fields).
    /// Untrusted: a member that is not a string, is blank, or is over
    /// <see cref="RemoteLyrics.MaxTextBytes"/> as UTF-8 is dropped. A song the desktop does not
    /// know, or a desktop without the method, is <see cref="NoctisErrorKind.Server"/>.
    /// </summary>
    public async Task<RemoteLyrics> GetLyricsAsync(Guid trackId, CancellationToken ct)
    {
        var r = await GetJsonAsync("getNoctisLyrics", new KeyValuePair<string, string>[]
        {
            new("id", NoctisRemoteIds.ToServerTrackId(trackId)),
        }, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("noctisLyrics", out var l) || l.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Server, "The server sent unreadable lyrics.");
        return new RemoteLyrics(
            RemoteLyrics.Clean(Str(l, "ttml")),
            RemoteLyrics.Clean(Str(l, "elrc")),
            RemoteLyrics.Clean(Str(l, "lrc")),
            RemoteLyrics.Clean(Str(l, "synced")),
            RemoteLyrics.Clean(Str(l, "plain")));
    }
}
