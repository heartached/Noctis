using System.Net;

namespace Noctis.Services;

/// <summary>
/// Large-file HTTP download that survives a flaky connection (09-25 Discord: Lyrics Studio's
/// Medium model "fails halfway" on a ~0.3 MB/s link). A dropped or stalled connection is
/// retried and continued with an HTTP Range request instead of failing the whole download,
/// and a connection that stops sending bytes is abandoned after <see cref="Options.StallTimeout"/>
/// (HttpClient.Timeout never covers body reads after the headers arrive).
/// </summary>
public static class ResumableDownload
{
    public sealed record Options
    {
        /// <summary>Failed attempts in a row before giving up; an attempt that received bytes resets the count.</summary>
        public int MaxConsecutiveFailures { get; init; } = 5;
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
        /// <summary>First retry delay; doubles per consecutive failure up to <see cref="MaxRetryDelay"/>.</summary>
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(15);
        public int BufferSize { get; init; } = 1 << 16;

        public static readonly Options Default = new();
    }

    /// <summary>
    /// Downloads to <paramref name="path"/>. With <paramref name="resumeExisting"/> an existing file
    /// there is treated as the first bytes of the download; otherwise it is overwritten.
    /// <paramref name="createRequest"/> builds a fresh GET per attempt (a request can only be sent once).
    /// <paramref name="onProgress"/> receives (bytes on disk, total or 0 when unknown).
    /// Returns the final file length. The file is left in place on failure so a later call can resume.
    /// </summary>
    public static async Task<long> DownloadAsync(
        HttpClient http,
        Func<HttpRequestMessage> createRequest,
        string path,
        bool resumeExisting,
        Action<long, long>? onProgress,
        string logName,
        CancellationToken ct,
        Options? options = null)
    {
        options ??= Options.Default;
        long offset = 0;
        if (resumeExisting)
        {
            try { offset = new FileInfo(path).Exists ? new FileInfo(path).Length : 0; } catch { offset = 0; }
        }
        else
        {
            TruncateTo(path, 0);
        }

        var failures = 0;
        var buffer = new byte[options.BufferSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var startOffset = offset;
            var outcome = await AttemptAsync(http, createRequest, path, offset, buffer, onProgress, options, ct).ConfigureAwait(false);
            if (outcome.Complete)
            {
                if (failures > 0 || startOffset > 0)
                    DebugLogger.Info(DebugLogger.Category.State, "Download.Resumed", $"{logName}: done at {outcome.Offset} bytes (resumed from {startOffset})");
                return outcome.Offset;
            }
            offset = outcome.Offset;
            var transient = outcome.Error;

            failures = offset > startOffset ? 1 : failures + 1;
            if (failures >= options.MaxConsecutiveFailures)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "Download.GaveUp", $"{logName}: {failures} failures in a row at {offset} bytes: {transient?.GetType().Name}: {transient?.Message}");
                throw new IOException($"Download failed after {failures} attempts in a row: {transient?.Message}", transient);
            }

            var delay = TimeSpan.FromTicks(Math.Min(
                options.MaxRetryDelay.Ticks,
                options.RetryDelay.Ticks * (1L << Math.Min(failures - 1, 20))));
            DebugLogger.Warn(DebugLogger.Category.State, "Download.Retry", $"{logName}: at {offset} bytes, retry in {delay.TotalSeconds:0.#}s: {transient?.GetType().Name}: {transient?.Message}");
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private readonly record struct Outcome(bool Complete, long Offset, Exception? Error);

    private static async Task<Outcome> AttemptAsync(
        HttpClient http, Func<HttpRequestMessage> createRequest, string path, long offset,
        byte[] buffer, Action<long, long>? onProgress, Options options, CancellationToken ct)
    {
        using var request = createRequest();
        if (offset > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);

        HttpResponseMessage response;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(options.StallTimeout);
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return new Outcome(false, offset, ex);
            }
        }

        using (response)
        {
            long total;
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                // Asked past the end: the file is already whole if the server's length matches.
                if (response.Content.Headers.ContentRange?.Length is long len && len == offset)
                    return new Outcome(true, offset, null);
                TruncateTo(path, 0);
                return new Outcome(false, 0, new IOException($"Partial file ({offset} bytes) does not match the server; starting over."));
            }
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset)
                {
                    TruncateTo(path, 0);
                    return new Outcome(false, 0, new IOException("Server answered a different byte range; starting over."));
                }
                total = range.Length ?? 0;
            }
            else if (response.IsSuccessStatusCode)
            {
                // Plain 200: the server ignored the Range header, so the body starts at byte 0.
                offset = 0;
                TruncateTo(path, 0);
                total = response.Content.Headers.ContentLength ?? 0;
            }
            else if (IsRetryableStatus(response.StatusCode))
            {
                return new Outcome(false, offset, new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode));
            }
            else
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidOperationException("unreachable");
            }

            onProgress?.Invoke(offset, total);

            Stream body;
            try { body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsTransient(ex, ct)) { return new Outcome(false, offset, ex); }

            await using (body)
            await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options.BufferSize, useAsync: true))
            {
                file.SetLength(offset);
                file.Seek(offset, SeekOrigin.Begin);
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    int read;
                    stallCts.CancelAfter(options.StallTimeout);
                    try
                    {
                        read = await body.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsTransient(ex, ct))
                    {
                        await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        var error = ex is OperationCanceledException
                            ? new TimeoutException($"No data received for {options.StallTimeout.TotalSeconds:0} s.", ex)
                            : ex;
                        return new Outcome(false, offset, error);
                    }
                    if (read == 0) break;
                    // Disk errors (full, locked) are not network trouble: let them propagate.
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    offset += read;
                    onProgress?.Invoke(offset, total);
                }
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            if (total > 0 && offset < total)
                return new Outcome(false, offset, new IOException($"Connection closed early at {offset} of {total} bytes."));
            if (total > 0 && offset > total)
            {
                TruncateTo(path, 0);
                return new Outcome(false, 0, new IOException($"Received {offset} bytes, more than the {total} announced; starting over."));
            }
            return new Outcome(true, offset, null);
        }
    }

    /// <summary>Network failures, HttpClient.Timeout and our stall timeout are worth retrying; the caller's cancel is not.</summary>
    internal static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested && ex switch
        {
            OperationCanceledException => true,
            HttpRequestException h => h.StatusCode is null || IsRetryableStatus(h.StatusCode.Value),
            IOException => true,
            _ => false,
        };

    internal static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static void TruncateTo(string path, long length)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            fs.SetLength(length);
        }
        catch { }
    }
}
