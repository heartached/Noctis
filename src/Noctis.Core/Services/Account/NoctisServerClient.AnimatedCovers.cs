using System.Net;
using System.Net.Http.Headers;

namespace Noctis.Mobile.Services.Account;

/// <summary>What <see cref="NoctisServerClient.DownloadAnimatedCoverAsync"/> did.</summary>
public enum AnimatedCoverFetch
{
    /// <summary>The loop was written to the destination.</summary>
    Saved,
    /// <summary>The computer still has the loop the phone holds (the ETag matched): nothing written.</summary>
    NotModified,
    /// <summary>The computer has no loop for the album, or is older than getNoctisAnimatedCover.</summary>
    None,
}

/// <summary>The answer, and the version (ETag) to send next time.</summary>
public sealed record AnimatedCoverDownload(AnimatedCoverFetch Result, string? ETag);

internal sealed partial class NoctisServerClient
{
    /// <summary>The largest loop taken: the desktop's own download cap (ITunesArtworkService.MaxAnimatedCoverBytes).</summary>
    public const long MaxAnimatedCoverBytes = 256L * 1024 * 1024;

    /// <summary>
    /// getNoctisAnimatedCover: the computer's animated cover of <paramref name="albumId"/> into
    /// <paramref name="destination"/> (written beside it, then moved over it). With
    /// <paramref name="etag"/> (the version last saved), an unchanged loop answers
    /// <see cref="AnimatedCoverFetch.NotModified"/> and nothing is sent. A loop is tens of MB:
    /// like a song download it may take as long as it needs while bytes keep coming, and fails as
    /// Unreachable after <see cref="StallTimeout"/> with none.
    /// </summary>
    public async Task<AnimatedCoverDownload> DownloadAnimatedCoverAsync(Guid albumId, string destination, string? etag, CancellationToken ct)
    {
        const string what = "animated cover";
        using var request = Request(HttpMethod.Get, "getNoctisAnimatedCover", new KeyValuePair<string, string>[]
        {
            new("id", NoctisRemoteIds.ToServerAlbumId(albumId)),
        });
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var sent)) request.Headers.IfNoneMatch.Add(sent);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(ApiTimeout);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, what, stall.Token, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new AnimatedCoverDownload(AnimatedCoverFetch.NotModified, etag);
        try
        {
            await ThrowIfEnvelopeAsync(response, what, stall.Token, ct).ConfigureAwait(false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
        {
            return new AnimatedCoverDownload(AnimatedCoverFetch.None, null); // 70: no loop (or an older computer)
        }
        if (response.Content.Headers.ContentLength > MaxAnimatedCoverBytes) return new AnimatedCoverDownload(AnimatedCoverFetch.None, null);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";
        try
        {
            await using (var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if ((total += read) > MaxAnimatedCoverBytes) throw new IOException("Response too large.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (new FileInfo(tmp).Length == 0) { TryDelete(tmp); return new AnimatedCoverDownload(AnimatedCoverFetch.None, null); }
            File.Move(tmp, destination, overwrite: true);
            return new AnimatedCoverDownload(AnimatedCoverFetch.Saved, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw Log(Unreachable(), what, 0);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            TryDelete(tmp);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw Log(Unreachable(), what, 0);
        }
    }
}
