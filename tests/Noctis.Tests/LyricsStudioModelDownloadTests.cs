using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>The speech model download as the Studio shows it.</summary>
public class LyricsStudioModelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-dl-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioModelDownloadTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static readonly Noctis.Services.ResumableDownload.Options Fast = new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        StallTimeout = TimeSpan.FromSeconds(5),
        MaxConsecutiveFailures = 3,
        BufferSize = 512,
    };

    private LyricsStudioViewModel Studio(ILyricsStudioEngine engine) =>
        new(Array.Empty<Track>(), engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AStudioOpenedMidDownload_FollowsIt_InsteadOfOfferingASecond()
    {
        var gate = new TaskCompletionSource();
        var handler = new GatedHandler(gate.Task);
        var models = StudioTestModel.Create(_root, out var bytes, new HttpClient(handler), Fast);
        handler.Body = bytes;
        var engine = new ModelOnlyEngine(models);

        var first = Studio(engine);
        Assert.True(first.ShowModelDownload);
        var download = first.DownloadModelCommand.ExecuteAsync(null);
        await Until(() => handler.Requests > 0);

        var second = Studio(engine); // the Studio closed and opened again
        Assert.True(second.IsDownloadingModel);
        Assert.False(second.ShowModelDownload);

        gate.SetResult();
        await download;
        await Until(() => second.IsModelInstalled);

        Assert.Equal(1, handler.Requests);
        Assert.True(first.IsModelInstalled);
        Assert.True(second.IsModelInstalled);
        Assert.False(second.IsDownloadingModel);
    }

    /// <summary>Serves <see cref="Body"/>, holding the first byte until the gate opens.</summary>
    internal sealed class GatedHandler(Task gate) : HttpMessageHandler
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            var content = new StreamContent(new GatedStream(new MemoryStream(Body, from, Body.Length - from), gate));
            content.Headers.ContentLength = Body.Length - from;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (from > 0) content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, Body.Length - 1, Body.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class GatedStream(Stream inner, Task gate) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            return await inner.ReadAsync(buffer, ct);
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

    /// <summary>An engine that only has a model manager (no song is run in these tests).</summary>
    internal sealed class ModelOnlyEngine(WhisperModelManager models) : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = models;
        public IDisposable OpenSession(WhisperModelSize model) => throw new NotSupportedException();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
