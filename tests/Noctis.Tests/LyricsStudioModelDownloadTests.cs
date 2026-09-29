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

    // ── Banner states ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void NoModel_ShowsOneDownloadAction_WithTheSize()
    {
        var vm = Studio(new ModelOnlyEngine(new WhisperModelManager(_root)));

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.NotInstalled, vm.ModelBanner);
        Assert.True(vm.ShowModelBanner);
        Assert.True(vm.ShowModelDownload);
        Assert.False(vm.ShowModelCancel);
        Assert.False(vm.CanStart);
        Assert.Equal("Download (1.4 GB)", vm.ModelActionText);
        Assert.Contains("Whisper Medium", vm.ModelBannerDetail);
    }

    [AvaloniaFact]
    public void AnInstalledModel_HidesTheBanner()
    {
        var vm = Studio(new ModelOnlyEngine(StudioTestModel.Installed(_root)));

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Hidden, vm.ModelBanner);
        Assert.False(vm.ShowModelBanner);
        Assert.True(vm.IsModelInstalled);
    }

    [AvaloniaFact]
    public void AStoppedDownload_OffersResume_WithWhatIsOnDisk()
    {
        var models = StudioTestModel.Create(_root, out var bytes);
        File.WriteAllBytes(models.ModelPath + ".part", bytes.AsSpan(0, 1024).ToArray());

        var vm = Studio(new ModelOnlyEngine(models));

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Paused, vm.ModelBanner);
        Assert.Equal("Resume download", vm.ModelActionText);
        Assert.Equal(0.25, vm.ModelBannerProgress, 3);
        Assert.Equal("25%", vm.ModelBannerPercent);
    }

    [AvaloniaFact]
    public async Task ADamagedFile_AsksForAFreshDownload()
    {
        var models = StudioTestModel.Create(_root, out var bytes);
        bytes[0] ^= 1;
        File.WriteAllBytes(models.ModelPath, bytes);
        Assert.False(await models.VerifyAsync(null, CancellationToken.None));

        var vm = Studio(new ModelOnlyEngine(models));

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Damaged, vm.ModelBanner);
        Assert.True(vm.ModelBannerIsError);
        Assert.Equal("Download again", vm.ModelActionText);
        Assert.False(vm.IsModelInstalled);
    }

    [AvaloniaFact]
    public async Task AFailedDownload_SaysWhy_AndOffersRetry()
    {
        var handler = new GatedHandler(Task.CompletedTask) { Status = HttpStatusCode.NotFound };
        var models = StudioTestModel.Create(_root, out var bytes, new HttpClient(handler), Fast);
        handler.Body = bytes;
        var vm = Studio(new ModelOnlyEngine(models));

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Failed, vm.ModelBanner);
        Assert.True(vm.ModelBannerIsError);
        Assert.Equal("Retry", vm.ModelActionText);
        Assert.Contains("404", vm.ModelBannerDetail);
        Assert.False(vm.IsDownloadingModel);
    }

    [AvaloniaFact]
    public async Task ACorruptDownload_IsReportedAsSuch()
    {
        var handler = new GatedHandler(Task.CompletedTask);
        var models = StudioTestModel.Create(_root, out var bytes, new HttpClient(handler), Fast);
        var corrupt = bytes.ToArray();
        corrupt[100] ^= 0x20;
        handler.Body = corrupt;
        var vm = Studio(new ModelOnlyEngine(models));

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Failed, vm.ModelBanner);
        Assert.Contains("did not match the published model", vm.ModelBannerDetail);
        Assert.Equal("Retry", vm.ModelActionText);
        Assert.False(vm.IsModelInstalled);
    }

    [AvaloniaFact]
    public async Task Downloading_ShowsBytesSpeedAndTimeLeft_OnlyOnTheTick()
    {
        var feed = new SteppedHandler();
        var models = StudioTestModel.Create(_root, out var bytes, new HttpClient(feed), Fast);
        feed.Body = bytes;
        var vm = Studio(new ModelOnlyEngine(models));
        var now = TimeSpan.Zero;
        vm.Clock = () => now;

        var download = vm.DownloadModelCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Connecting, vm.ModelBanner);
        Assert.True(vm.ModelBannerIndeterminate);
        Assert.True(vm.ShowModelCancel);

        // 512 bytes a second for three seconds, sampled on the tick.
        for (var second = 0; second <= 3; second++)
        {
            await feed.Send(512);
            await Until(() => models.CurrentDownload?.BytesDone == 512 * (second + 1));
            now = TimeSpan.FromSeconds(second);
            vm.Tick();
        }
        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Downloading, vm.ModelBanner);
        Assert.False(vm.ModelBannerIndeterminate);
        Assert.Equal(0.5, vm.ModelBannerProgress, 3);
        Assert.Equal("50%", vm.ModelBannerPercent);
        // 2 KB of 4 KB · 512 B/s · 4 s left, rounded up to 5.
        Assert.Equal("2 KB of 4 KB · 1 KB/s · about 5 s left", vm.ModelBannerDetail);

        // More bytes arrive, but nothing on screen changes until the next tick (~15 Hz).
        await feed.Send(1024);
        await Until(() => models.CurrentDownload?.BytesDone == 3072);
        Assert.Equal("50%", vm.ModelBannerPercent);
        vm.Tick();
        Assert.Equal("75%", vm.ModelBannerPercent);

        await feed.Send(1024);
        await download;
        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Hidden, vm.ModelBanner);
        Assert.True(vm.IsModelInstalled);
    }

    [AvaloniaFact]
    public async Task Cancel_KeepsWhatArrived_AndOffersResume()
    {
        var feed = new SteppedHandler();
        var models = StudioTestModel.Create(_root, out var bytes, new HttpClient(feed), Fast);
        feed.Body = bytes;
        var vm = Studio(new ModelOnlyEngine(models));

        var download = vm.DownloadModelCommand.ExecuteAsync(null);
        await feed.Send(2048);
        await Until(() => models.CurrentDownload?.BytesDone == 2048);
        vm.CancelModelDownloadCommand.Execute(null);
        await download;

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Paused, vm.ModelBanner);
        Assert.Equal("Resume download", vm.ModelActionText);
        Assert.Equal("50%", vm.ModelBannerPercent);
    }

    // ── Meter ──────────────────────────────────────────────────────────────

    [Fact]
    public void Meter_HoldsSpeedBackUntilItSettles_ThenMeasuresTheRecentWindow()
    {
        var meter = new DownloadMeter();
        const long total = 100L << 20;
        ModelDownloadProgress At(long bytes) => new(ModelDownloadPhase.Downloading, bytes, total);

        meter.Sample(At(0), TimeSpan.Zero);
        meter.Sample(At(1L << 20), TimeSpan.FromSeconds(1));
        Assert.Null(meter.Read(TimeSpan.FromSeconds(1)).BytesPerSecond); // under 1.5 s of data

        for (var s = 2; s <= 20; s++) meter.Sample(At(s * (1L << 20)), TimeSpan.FromSeconds(s));
        var r = meter.Read(TimeSpan.FromSeconds(20));
        Assert.Equal(1 << 20, r.BytesPerSecond!.Value, 1);
        Assert.Equal(80, r.Remaining!.Value.TotalSeconds, 1);
        Assert.Equal("about 1 min left", DownloadMeter.RemainingText(r.Remaining.Value));

        // The link speeds up: the window follows within its five seconds.
        for (var s = 21; s <= 26; s++) meter.Sample(At((20 + (s - 20) * 4) * (1L << 20)), TimeSpan.FromSeconds(s));
        Assert.InRange(meter.Read(TimeSpan.FromSeconds(26)).BytesPerSecond!.Value, 3.5 * (1 << 20), 4.5 * (1 << 20));
    }

    [Fact]
    public void Meter_AStall_ShowsTheSpeedFalling()
    {
        var meter = new DownloadMeter();
        for (var s = 0; s <= 5; s++) meter.Sample(new(ModelDownloadPhase.Downloading, s * 1000L, 100_000), TimeSpan.FromSeconds(s));
        var steady = meter.Read(TimeSpan.FromSeconds(5)).BytesPerSecond!.Value;
        var stalled = meter.Read(TimeSpan.FromSeconds(9)).BytesPerSecond!.Value;
        Assert.True(stalled < steady * 0.7, $"{stalled} vs {steady}");
    }

    [Fact]
    public void Meter_FractionNeverStepsBack_ExceptWhenTheFileStartsOver()
    {
        var meter = new DownloadMeter();
        meter.Sample(new(ModelDownloadPhase.Downloading, 600, 1000), TimeSpan.Zero);
        Assert.Equal(0.6, meter.Read(TimeSpan.Zero).Fraction, 3);

        // A retry report carries the offset it resumes from: never below what was shown.
        meter.Sample(new(ModelDownloadPhase.Retrying, 600, 1000, Attempt: 1, RetryIn: TimeSpan.FromSeconds(4)), TimeSpan.FromSeconds(1));
        var retry = meter.Read(TimeSpan.FromSeconds(2.5));
        Assert.Equal(0.6, retry.Fraction, 3);
        Assert.Equal(2.5, retry.RetryIn.TotalSeconds, 3);
        Assert.Null(retry.BytesPerSecond);

        meter.Sample(new(ModelDownloadPhase.Downloading, 650, 1000), TimeSpan.FromSeconds(6));
        meter.Sample(new(ModelDownloadPhase.Downloading, 50, 1000), TimeSpan.FromSeconds(7)); // server ignored the range
        Assert.Equal(0.05, meter.Read(TimeSpan.FromSeconds(7)).Fraction, 3);

        meter.Sample(new(ModelDownloadPhase.Verifying, 1000, 1000, 0.3), TimeSpan.FromSeconds(8));
        Assert.Equal(1, meter.Read(TimeSpan.FromSeconds(8)).Fraction);
    }

    [Theory]
    [InlineData(3, "about 5 s left")]
    [InlineData(41, "about 45 s left")]
    [InlineData(89, "about 1 min left")]
    [InlineData(150, "about 3 min left")]
    [InlineData(3900, "about 1 h 5 min left")]
    public void RemainingText_IsRoundedToStayStill(double seconds, string expected)
        => Assert.Equal(expected, DownloadMeter.RemainingText(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(300 * 1024.0, "300 KB/s")]
    [InlineData(4.24 * 1048576, "4.2 MB/s")]
    [InlineData(12.6 * 1048576, "13 MB/s")]
    public void RateText_IsReadable(double bytesPerSecond, string expected)
        => Assert.Equal(expected, DownloadMeter.RateText(bytesPerSecond));

    /// <summary>Serves <see cref="Body"/> only as fast as the test releases it.</summary>
    internal sealed class SteppedHandler : HttpMessageHandler
    {
        private readonly SemaphoreSlim _allowance = new(0);
        private int _allowed;
        public byte[] Body { get; set; } = Array.Empty<byte>();

        /// <summary>Lets <paramref name="bytes"/> more bytes through.</summary>
        public Task Send(int bytes)
        {
            Interlocked.Add(ref _allowed, bytes);
            _allowance.Release();
            return Task.CompletedTask;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            var content = new StreamContent(new SteppedStream(this, from));
            content.Headers.ContentLength = Body.Length - from;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (from > 0) content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, Body.Length - 1, Body.Length);
            return Task.FromResult(response);
        }

        private sealed class SteppedStream(SteppedHandler owner, int position) : Stream
        {
            private int _sent;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (position >= owner.Body.Length) return 0;
                while (Volatile.Read(ref owner._allowed) - _sent <= 0)
                    await owner._allowance.WaitAsync(ct);
                var n = Math.Min(buffer.Length, Math.Min(owner.Body.Length - position, Volatile.Read(ref owner._allowed) - _sent));
                owner.Body.AsMemory(position, n).CopyTo(buffer);
                position += n;
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

    /// <summary>Serves <see cref="Body"/>, holding the first byte until the gate opens.</summary>
    internal sealed class GatedHandler(Task gate) : HttpMessageHandler
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
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
