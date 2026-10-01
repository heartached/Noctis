using System.Net;
using System.Net.Http.Headers;
using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Noctis.Services.YouTube;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 09-25 Discord (silly KAT, Windows): in-app downloads "take ages and sometimes fail halfway";
/// Lyrics Studio's Medium model (1.5 GB) never finished on a ~0.3 MB/s link. Every download was a
/// single attempt with no resume, and the model's .part was deleted on error. These drive
/// <see cref="ResumableDownload"/> and its three callers through a scripted fake server.
/// </summary>
public class ResumableDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-resume-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly byte[] Data = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 + 7)).ToArray();

    private static readonly ResumableDownload.Options Fast = new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        StallTimeout = TimeSpan.FromMilliseconds(300),
        MaxConsecutiveFailures = 4,
        BufferSize = 4096,
    };

    public ResumableDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DroppedConnection_ResumesFromOffset_AndFileIsExact()
    {
        var server = new FakeServer(Data,
            Step.DropAfter(100_000),
            Step.DropAfter(50_000),
            Step.Serve());
        var path = Path.Combine(_root, "a.bin");

        var length = await ResumableDownload.DownloadAsync(server.Client(), Get, path, false, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data.Length, length);
        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(new long?[] { null, 100_000, 150_000 }, server.RangeStarts);
    }

    [Fact]
    public async Task StalledConnection_IsAbandoned_AndResumed()
    {
        var server = new FakeServer(Data, Step.StallAfter(120_000), Step.Serve());
        var path = Path.Combine(_root, "b.bin");

        await ResumableDownload.DownloadAsync(server.Client(), Get, path, false, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(new long?[] { null, 120_000 }, server.RangeStarts);
    }

    [Fact]
    public async Task ServerIgnoringRange_RestartsFromZero_WithoutCorruption()
    {
        var server = new FakeServer(Data, Step.DropAfter(80_000), Step.Serve(ignoreRange: true));
        var path = Path.Combine(_root, "c.bin");

        await ResumableDownload.DownloadAsync(server.Client(), Get, path, false, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ServerError_IsRetried()
    {
        var server = new FakeServer(Data, Step.Status(HttpStatusCode.ServiceUnavailable), Step.Serve());
        var path = Path.Combine(_root, "d.bin");

        await ResumableDownload.DownloadAsync(server.Client(), Get, path, false, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, server.RangeStarts.Count);
    }

    [Fact]
    public async Task NotFound_FailsImmediately()
    {
        var server = new FakeServer(Data, Step.Status(HttpStatusCode.NotFound), Step.Serve());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ResumableDownload.DownloadAsync(server.Client(), Get, Path.Combine(_root, "e.bin"), false, null, "test", CancellationToken.None, Fast));
        Assert.Single(server.RangeStarts);
    }

    [Fact]
    public async Task NoProgressAtAll_GivesUpAfterMaxConsecutiveFailures()
    {
        var server = new FakeServer(Data, Enumerable.Repeat(Step.DropAfter(0), 10).ToArray());

        await Assert.ThrowsAsync<IOException>(() =>
            ResumableDownload.DownloadAsync(server.Client(), Get, Path.Combine(_root, "f.bin"), false, null, "test", CancellationToken.None, Fast));
        Assert.Equal(Fast.MaxConsecutiveFailures, server.RangeStarts.Count);
    }

    [Fact]
    public async Task SlowButSteadyProgress_NeverHitsTheFailureCap()
    {
        // Seven drops, each after some progress: more than MaxConsecutiveFailures (4) in total,
        // but never four in a row without bytes, so the download must still finish.
        var steps = Enumerable.Range(0, 7).Select(_ => Step.DropAfter(30_000)).Append(Step.Serve()).ToArray();
        var server = new FakeServer(Data, steps);
        var path = Path.Combine(_root, "g.bin");

        await ResumableDownload.DownloadAsync(server.Client(), Get, path, false, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CallerCancel_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeServer(Data, Step.StallAfter(10_000), Step.Serve());
        cts.CancelAfter(100); // before the 300 ms stall timeout

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResumableDownload.DownloadAsync(server.Client(), Get, Path.Combine(_root, "h.bin"), false, null, "test", cts.Token, Fast));
        Assert.Single(server.RangeStarts);
    }

    [Fact]
    public async Task ExistingCompleteFile_416_IsAcceptedAsDone()
    {
        var path = Path.Combine(_root, "i.bin");
        await File.WriteAllBytesAsync(path, Data);
        var server = new FakeServer(Data, Step.Serve());

        var length = await ResumableDownload.DownloadAsync(server.Client(), Get, path, true, null, "test", CancellationToken.None, Fast);

        Assert.Equal(Data.Length, length);
        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ProgressReportsBytesAndTotal()
    {
        var server = new FakeServer(Data, Step.DropAfter(100_000), Step.Serve());
        long lastDone = 0, lastTotal = 0;

        await ResumableDownload.DownloadAsync(server.Client(), Get, Path.Combine(_root, "j.bin"), false,
            (done, total) => { lastDone = done; lastTotal = total; }, "test", CancellationToken.None, Fast);

        Assert.Equal(Data.Length, lastDone);
        Assert.Equal(Data.Length, lastTotal);
    }

    // ── Callers ──────────────────────────────────────────────────────────────

    /// <summary>The Medium entry with this test data's length and checksum (the real file is 1.5 GB).</summary>
    private static WhisperModelInfo ModelOf(byte[] data) => WhisperModelManager.Lullaby with
    {
        Bytes = data.Length,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
    };

    [Fact]
    public async Task WhisperModel_ResumesLeftoverPart_FromAnEarlierFailedRun()
    {
        var server = new FakeServer(Data, Step.Serve());
        var manager = new WhisperModelManager(_root, server.Client(), Fast, ModelOf(Data));
        var target = manager.PathFor(WhisperModelSize.Base);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target + ".part", Data.AsSpan(0, 200_000).ToArray());

        await manager.DownloadAsync(WhisperModelSize.Base, null, CancellationToken.None);

        Assert.Equal(new long?[] { 200_000 }, server.RangeStarts);
        Assert.Equal(Data, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(WhisperModelManager.Lullaby.Url, server.Urls.Single());
        Assert.Equal(WhisperModelState.Ready, manager.State);
    }

    [Fact]
    public async Task WhisperModel_KeepsPartOnFailure_SoTheNextTryContinues()
    {
        var manager = new WhisperModelManager(_root,
            new FakeServer(Data, Step.DropAfter(90_000), Step.DropAfter(0), Step.DropAfter(0), Step.DropAfter(0), Step.DropAfter(0)).Client(), Fast, ModelOf(Data));

        await Assert.ThrowsAsync<IOException>(() => manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None));

        var part = manager.PathFor(WhisperModelSize.Medium) + ".part";
        Assert.Equal(90_000, new FileInfo(part).Length);
        Assert.Equal(WhisperModelState.Partial, manager.State);
    }

    [Fact]
    public async Task WhisperModel_ThatFailsItsChecksum_IsDiscarded_NotInstalled()
    {
        var corrupt = Data.ToArray();
        corrupt[123_456] ^= 0x40; // same length, one bit off in transit
        var manager = new WhisperModelManager(_root, new FakeServer(corrupt, Step.Serve()).Client(), Fast, ModelOf(Data));

        await Assert.ThrowsAsync<WhisperModelIntegrityException>(() => manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None));

        Assert.False(File.Exists(manager.ModelPath));
        Assert.False(File.Exists(manager.ModelPath + ".part")); // a resume would keep the bad byte
        Assert.Equal(WhisperModelState.Missing, manager.State);
        Assert.False(manager.IsInstalled());
    }

    [Fact]
    public async Task WhisperModel_ReportsEachPhase_IncludingTheRetry()
    {
        var server = new FakeServer(Data, Step.DropAfter(100_000), Step.Serve());
        var manager = new WhisperModelManager(_root, server.Client(), Fast, ModelOf(Data));
        var reports = new List<ModelDownloadProgress>();

        await manager.DownloadAsync(WhisperModelSize.Medium, new InlineProgress<ModelDownloadProgress>(r => { lock (reports) reports.Add(r); }), CancellationToken.None);

        Assert.Equal(ModelDownloadPhase.Connecting, reports[0].Phase);
        Assert.Equal(ModelDownloadPhase.Done, reports[^1].Phase);
        Assert.True(reports.FindIndex(r => r.Phase == ModelDownloadPhase.Retrying) > reports.FindIndex(r => r.Phase == ModelDownloadPhase.Downloading));
        Assert.True(reports.FindIndex(r => r.Phase == ModelDownloadPhase.Verifying) > reports.FindLastIndex(r => r.Phase == ModelDownloadPhase.Downloading));
        var retry = reports.First(r => r.Phase == ModelDownloadPhase.Retrying);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(100_000, retry.BytesDone);
        Assert.Equal(Data.Length, reports.Where(r => r.Phase == ModelDownloadPhase.Downloading).Max(r => r.BytesDone));
        Assert.Equal(1.0, reports.Where(r => r.Phase == ModelDownloadPhase.Verifying).Max(r => r.VerifyFraction));
    }

    [Fact]
    public async Task WhisperModel_ASecondDownloadWhileOneRuns_JoinsIt()
    {
        // 09-29: closing the Studio mid-download and opening it again offered Download again, and
        // the second download failed on the first one's open .part ("used by another process").
        var gate = new TaskCompletionSource();
        var server = new FakeServer(Data, Step.Serve()) { Gate = gate.Task };
        var manager = new WhisperModelManager(_root, server.Client(), Fast, ModelOf(Data));

        var first = manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None);
        for (var i = 0; i < 200 && server.RangeStarts.Count == 0; i++) await Task.Delay(10);
        Assert.True(manager.IsDownloading);
        var second = manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(server.RangeStarts);
        Assert.Equal(Data, await File.ReadAllBytesAsync(manager.ModelPath));
        Assert.Equal(WhisperModelState.Ready, manager.State);
    }

    [Fact]
    public async Task WhisperModel_Cancel_KeepsThePart_ForAResume()
    {
        var server = new FakeServer(Data, Step.StallAfter(120_000), Step.Serve());
        var manager = new WhisperModelManager(_root, server.Client(), Fast with { StallTimeout = TimeSpan.FromSeconds(30) }, ModelOf(Data));

        var download = manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None);
        for (var i = 0; i < 300 && manager.CurrentDownload?.BytesDone != 120_000; i++) await Task.Delay(10);
        Assert.Equal(ModelDownloadPhase.Downloading, manager.CurrentDownload?.Phase);
        manager.CancelDownload();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.False(manager.IsDownloading);
        Assert.Equal(WhisperModelState.Partial, manager.State);
        Assert.Equal(120_000, manager.PartialBytes);

        await manager.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None);
        Assert.Equal(new long?[] { null, 120_000 }, server.RangeStarts);
        Assert.Equal(WhisperModelState.Ready, manager.State);
    }

    [Fact]
    public void WhisperModelUrl_IsTheFixedLullabyRelease()
    {
        Assert.Equal("https://github.com/heartached/Noctis/releases/download/lullaby-v1/lullaby.bin", WhisperModelManager.ModelUrl(WhisperModelSize.Medium));
        // Retired sizes are the one model.
        Assert.Equal(WhisperModelManager.ModelUrl(WhisperModelSize.Medium), WhisperModelManager.ModelUrl(WhisperModelSize.Base));
        Assert.Equal(WhisperModelManager.ModelUrl(WhisperModelSize.Medium), WhisperModelManager.ModelUrl(WhisperModelSize.Tiny));
    }

    [Fact]
    public async Task YtDlpInstall_SurvivesADroppedConnection()
    {
        var server = new FakeServer(Data, Step.DropAfter(64_000), Step.Serve());
        var tool = new YtDlpTool(server.Client(), _root, () => string.Empty) { DownloadOptions = Fast };

        var installed = await tool.InstallAsync(null, CancellationToken.None);

        Assert.Equal(Data, await File.ReadAllBytesAsync(installed));
        Assert.Equal(new long?[] { null, 64_000 }, server.RangeStarts);
        Assert.False(File.Exists(installed + ".part"));
    }

    // ── Fake server ──────────────────────────────────────────────────────────

    private static HttpRequestMessage Get() => new(HttpMethod.Get, "https://example.test/file.bin");

    private sealed record Step(HttpStatusCode? Code, int? DropAt, int? StallAt, bool IgnoreRange)
    {
        public static Step Serve(bool ignoreRange = false) => new(null, null, null, ignoreRange);
        public static Step DropAfter(int bytes) => new(null, bytes, null, false);
        public static Step StallAfter(int bytes) => new(null, null, bytes, false);
        public static Step Status(HttpStatusCode code) => new(code, null, null, false);
    }

    private sealed class FakeServer(byte[] data, params Step[] steps) : HttpMessageHandler
    {
        private int _next;
        /// <summary>Bodies hold their first byte until this completes (a download kept in flight).</summary>
        public Task? Gate { get; init; }
        public List<long?> RangeStarts { get; } = new();
        public List<string> Urls { get; } = new();

        public HttpClient Client() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = steps[Math.Min(_next++, steps.Length - 1)];
            var from = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(from);
            Urls.Add(request.RequestUri!.ToString());

            if (step.Code is { } code)
                return Task.FromResult(new HttpResponseMessage(code));

            var start = step.IgnoreRange ? 0 : (int)(from ?? 0);
            if (start >= data.Length && from is not null)
            {
                var full = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                full.Content.Headers.ContentRange = new ContentRangeHeaderValue(data.Length);
                return Task.FromResult(full);
            }

            var body = new ScriptedStream(data, start, step.DropAt, step.StallAt, Gate);
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentLength = data.Length - start;
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>Serves data[start..]; after <c>dropAfter</c> bytes throws like a reset socket, after <c>stallAfter</c> bytes hangs.</summary>
    private sealed class ScriptedStream(byte[] data, int start, int? dropAfter, int? stallAfter, Task? gate = null) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (gate is not null) await gate.WaitAsync(ct);
            var limit = data.Length - start;
            if (dropAfter is { } d) limit = Math.Min(limit, d);
            if (stallAfter is { } s) limit = Math.Min(limit, s);
            if (_sent >= limit)
            {
                if (stallAfter is not null) { await Task.Delay(Timeout.Infinite, ct); }
                if (dropAfter is not null) throw new IOException("Connection reset by peer (simulated).");
                return 0;
            }
            var n = Math.Min(buffer.Length, limit - _sent);
            data.AsMemory(start + _sent, n).CopyTo(buffer);
            _sent += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
