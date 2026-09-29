using System.Net;
using System.Net.Http;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// X16: the installer download used to run under one absolute 5-minute deadline, so every
/// download below ~4-6 Mbit/s was cancelled at 5:00 and reported as "Download cancelled.".
/// The installer download (through ResumableDownload) now only gives up when no data arrives
/// for StallTimeout on several attempts in a row; that give-up is an IOException, not a user
/// cancel, and the partial file is always deleted.
/// </summary>
public class UpdateDownloadStallTests : IDisposable
{
    private const string AssetUrl = "https://api.github.com/repos/heartached/Noctis/releases/assets/1";
    private const int ChunkSize = 1024;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public UpdateDownloadStallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Destination => Path.Combine(_dir, "Noctis-test-Setup.exe");

    private static ResumableDownload.Options Options(TimeSpan stall) => new()
    {
        StallTimeout = stall,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxConsecutiveFailures = 3,
    };

    private static UpdateInfo Info(long size) => new()
    {
        TagName = "v9.9.9",
        Version = new Version(9, 9, 9),
        InstallerApiUrl = AssetUrl,
        InstallerSize = size,
        InstallerAssetName = "Noctis-v9.9.9-Setup.exe",
        ReleaseUrl = "https://github.com/heartached/Noctis/releases/tag/v9.9.9",
    };

    [Fact]
    public async Task SlowButSteadyStream_CompletesPastManyStallWindows()
    {
        // 20 chunks, 100 ms apart = ~2 s total, five times the 400 ms stall window.
        // Under the old absolute deadline this shape could never outlive the deadline.
        const int chunks = 20;
        var handler = new StreamHandler(ct => new PacedStream(chunks, TimeSpan.FromMilliseconds(100), stallAfter: null));
        var service = new UpdateService(new HttpClient(handler)) { DownloadOptions = Options(TimeSpan.FromMilliseconds(400)) };

        var path = await service.DownloadInstallerAsync(Info(chunks * ChunkSize), ct: TestContext.Current.CancellationToken, destinationPath: Destination);

        Assert.Equal(Destination, path);
        Assert.Equal(chunks * ChunkSize, new FileInfo(path).Length);
        Assert.Equal(1, handler.Requests); // never stalled, so never retried
    }

    [Fact]
    public async Task StreamThatKeepsGoingSilent_GivesUp_AndDeletesPartialFile()
    {
        var handler = new StreamHandler(ct => new PacedStream(20, TimeSpan.FromMilliseconds(10), stallAfter: 2));
        var service = new UpdateService(new HttpClient(handler)) { DownloadOptions = Options(TimeSpan.FromMilliseconds(200)) };

        var ex = await Assert.ThrowsAsync<IOException>(
            () => service.DownloadInstallerAsync(Info(20 * ChunkSize), ct: TestContext.Current.CancellationToken, destinationPath: Destination));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(3, handler.Requests); // retried before giving up
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task CallerCancel_IsACancel_NotAStall()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StreamHandler(ct => new PacedStream(50, TimeSpan.FromMilliseconds(20), stallAfter: null,
            afterFirstChunk: () => cts.Cancel()));
        var service = new UpdateService(new HttpClient(handler)) { DownloadOptions = Options(TimeSpan.FromSeconds(30)) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadInstallerAsync(Info(50 * ChunkSize), ct: cts.Token, destinationPath: Destination));

        Assert.Equal(1, handler.Requests); // a user cancel is never retried
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task HeadersThatNeverArrive_GiveUpAsAFailure_NotACancel()
    {
        var handler = new StreamHandler(null); // never answers
        var service = new UpdateService(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) })
        {
            DownloadOptions = Options(TimeSpan.FromSeconds(30))
        };

        await Assert.ThrowsAsync<IOException>(
            () => service.DownloadInstallerAsync(Info(ChunkSize), ct: TestContext.Current.CancellationToken, destinationPath: Destination));

        Assert.False(File.Exists(Destination));
    }

    /// <summary>Answers every request with a fresh streamed body, or never answers when the factory is null.</summary>
    private sealed class StreamHandler(Func<CancellationToken, Stream>? body) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            if (body is null)
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body(ct)) };
        }
    }

    /// <summary>Delivers fixed-size chunks at a fixed pace; after <c>stallAfter</c> chunks it goes
    /// silent until the reader gives up (a server that stopped sending).</summary>
    private sealed class PacedStream(int chunks, TimeSpan interval, int? stallAfter, Action? afterFirstChunk = null) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= chunks) return 0;
            if (stallAfter is { } n && _sent >= n)
                await Task.Delay(Timeout.Infinite, ct);
            if (_sent > 0) await Task.Delay(interval, ct);

            var len = Math.Min(ChunkSize, buffer.Length);
            buffer.Span[..len].Fill(0x5A);
            _sent++;
            if (_sent == 1) afterFirstChunk?.Invoke();
            return len;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

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
