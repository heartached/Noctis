using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// PNG probe of the Lyrics Studio's model card and run progress (09-29), for an eye check.
/// Runs only with NOCTIS_TEST_SKIA=1 (real Skia rendering); PNGs go to NOCTIS_RENDER_OUT or
/// D:\NoctisLyricsLab\ui-shots. Every state is driven through the real view model: a fake
/// server for the download, a stepped engine for the songs.
/// </summary>
public class LyricsStudioUiProbeTests : IDisposable
{
    // The download state leaves a 612 MB sparse .part while it renders: on the lab drive when there is one.
    private readonly string _root = Path.Combine(
        HeadlessTestApp.RealRendering && Directory.Exists(@"D:\NoctisLyricsLab") ? @"D:\NoctisLyricsLab\probe-tmp" : Path.GetTempPath(),
        "studio-probe-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _out;

    public LyricsStudioUiProbeTests()
    {
        if (HeadlessTestApp.RealRendering) Directory.CreateDirectory(_root);
        var outDir = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        _out = string.IsNullOrWhiteSpace(outDir) ? @"D:\NoctisLyricsLab\ui-shots" : outDir;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static readonly ResumableDownload.Options Fast = new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        StallTimeout = TimeSpan.FromSeconds(30),
        MaxConsecutiveFailures = 3,
    };

    [AvaloniaFact]
    public async Task Probe_RenderStudioStatesToPng()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        Directory.CreateDirectory(_out);
        EnsureAppResources();

        // 1. No model yet.
        {
            var vm = Studio(new ProbeEngine(new WhisperModelManager(Path.Combine(_root, "none"))), Songs(4));
            Capture(vm, "model-not-installed");
        }

        // 2. Downloading, 43% in, a steady 4.4 MB/s over the fake clock.
        {
            var dir = Path.Combine(_root, "dl");
            var feed = new VirtualServer(WhisperModelManager.Medium.Bytes);
            var models = new WhisperModelManager(dir, new HttpClient(feed), Fast);
            Directory.CreateDirectory(models.Directory);
            using (var part = new FileStream(models.ModelPath + ".part", FileMode.Create)) part.SetLength(612L << 20);
            var vm = Studio(new ProbeEngine(models), Songs(4));
            var now = TimeSpan.Zero;
            vm.Clock = () => now;
            var download = vm.DownloadModelCommand.ExecuteAsync(null);
            Capture(vm, "model-connecting", pumpMs: 400);
            for (var s = 0; s <= 4; s++)
            {
                var target = (612L << 20) + s * (long)(4.4 * (1 << 20));
                feed.Allow(target);
                await Until(() => models.CurrentDownload?.BytesDone >= target);
                now = TimeSpan.FromSeconds(s);
                vm.Tick();
            }
            Capture(vm, "model-downloading");
            vm.CancelModelDownloadCommand.Execute(null);
            await download;
            Capture(vm, "model-paused");
        }

        // 2b. The link drops at 900 MB: the card counts down to the retry.
        {
            var dir = Path.Combine(_root, "retry");
            var feed = new VirtualServer(WhisperModelManager.Medium.Bytes) { DropAt = 900L << 20 };
            var slowRetry = Fast with { RetryDelay = TimeSpan.FromSeconds(20), MaxRetryDelay = TimeSpan.FromSeconds(20) };
            var models = new WhisperModelManager(dir, new HttpClient(feed), slowRetry);
            Directory.CreateDirectory(models.Directory);
            using (var part = new FileStream(models.ModelPath + ".part", FileMode.Create)) part.SetLength(880L << 20);
            var vm = Studio(new ProbeEngine(models), Songs(4));
            var now = TimeSpan.Zero;
            vm.Clock = () => now;
            vm.AutoTick = false;
            var download = vm.DownloadModelCommand.ExecuteAsync(null);
            feed.Allow(WhisperModelManager.Medium.Bytes);
            await Until(() => models.CurrentDownload?.Phase == ModelDownloadPhase.Retrying);
            vm.Tick();
            now = TimeSpan.FromSeconds(8);
            vm.Tick();
            Capture(vm, "model-retrying");
            vm.CancelModelDownloadCommand.Execute(null);
            await download;
        }

        // 3. Download failed: the server keeps answering 503.
        {
            var models = new WhisperModelManager(Path.Combine(_root, "fail"), new HttpClient(new VirtualServer(0) { Status = HttpStatusCode.ServiceUnavailable }), Fast);
            var vm = Studio(new ProbeEngine(models), Songs(4));
            await vm.DownloadModelCommand.ExecuteAsync(null);
            Capture(vm, "model-download-failed");
        }

        // 4. The model loading at the start of a run.
        {
            var gate = new ManualResetEventSlim();
            var engine = new ProbeEngine(StudioTestModel.Installed(Path.Combine(_root, "loading"))) { OpenGate = gate };
            var vm = Studio(engine, Songs(4));
            var run = vm.StartCommand.ExecuteAsync(null);
            Capture(vm, "model-loading", pumpMs: 500);
            vm.StopCommand.Execute(null);
            gate.Set();
            await run;
        }

        // 5. Songs running: decoding, listening, transcribing, and the batch with a review open.
        foreach (var (name, stage, fraction, transcribe, selectReady) in new[]
        {
            ("song-decoding", LyricsStudioStage.Decoding, 0.55, false, false),
            ("song-listening", LyricsStudioStage.Listening, 0.45, false, false),
            ("song-transcribing", LyricsStudioStage.Listening, 0.3, true, false),
            ("batch-in-progress", LyricsStudioStage.Listening, 0.6, false, true),
        })
        {
            var engine = new ProbeEngine(StudioTestModel.Installed(Path.Combine(_root, name)));
            var songs = Songs(5);
            var vm = Studio(engine, songs);
            vm.TranscribeOnly = transcribe;
            var now = TimeSpan.Zero;
            vm.Clock = () => now;
            vm.Selected = null;
            var run = vm.StartCommand.ExecuteAsync(null);
            // Songs one and two finish; song three stops mid-way.
            for (var i = 0; i < 2; i++)
            {
                await Wait(engine.Started);
                engine.Finish();
            }
            await Wait(engine.Started);
            engine.Report(new LyricsStudioProgress(LyricsStudioStage.Decoding, stage == LyricsStudioStage.Decoding ? fraction : 1));
            if (stage != LyricsStudioStage.Decoding) engine.Report(new LyricsStudioProgress(stage, fraction));
            vm.Tick();
            vm.Selected = selectReady ? vm.Queue.First(q => q.Status == LyricsStudioViewModel.StudioStatus.Ready) : vm.Queue[2];
            Capture(vm, name);
            vm.StopCommand.Execute(null);
            engine.Finish();
            await run;
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private List<Track> Songs(int count)
    {
        var titles = new[] { "Midnight Garden", "Paper Planes", "Slow Motion Summer", "Northern Lights", "After Hours" };
        var artists = new[] { "Luna Park", "The Wires", "Ada Vale", "Kite Theory", "Mono Rio" };
        var list = new List<Track>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_root, "songs", $"{i:00}.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (i % 2 == 0) File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]la la la\n[00:09.00]na na na\n");
            list.Add(new Track { Title = titles[i], Artist = artists[i], FilePath = path, Duration = TimeSpan.FromMinutes(3 + i * 0.4) });
        }
        return list;
    }

    private static LyricsStudioViewModel Studio(ILyricsStudioEngine engine, IReadOnlyList<Track> tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        return vm;
    }

    private void Capture(LyricsStudioViewModel vm, string name, int pumpMs = 900)
    {
        var panel = new LyricsStudioPanel { DataContext = vm };
        var card = new Border
        {
            CornerRadius = new CornerRadius(20),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#252525")),
            Child = panel,
        };
        var win = new Window
        {
            Width = 1000, Height = 720, Content = card,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#161616")),
        };
        var accent = new SolidColorBrush(Color.Parse("#E74856"));
        win.Resources["AccentColorBrush"] = accent;
        win.Resources["AccentButtonBackground"] = accent;
        win.Show();
        try
        {
            PumpFor(pumpMs);
            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(_out, name + ".png"));
        }
        finally { win.Close(); }
    }

    private static void PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(8);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    private static async Task Wait(SemaphoreSlim semaphore)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!semaphore.Wait(0))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            if (!app.Resources.ContainsKey("InterSemiBold"))
                app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
            });
        }
    }

    /// <summary>Serves zeros for a file of <paramref name="length"/> bytes, only up to the byte the probe allows.</summary>
    private sealed class VirtualServer(long length) : HttpMessageHandler
    {
        private long _allowed;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        /// <summary>Resets the connection once when a body reaches this byte.</summary>
        public long? DropAt { get; set; }
        public void Allow(long upTo) => Interlocked.Exchange(ref _allowed, upTo);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status) { ReasonPhrase = "Service Unavailable" });
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            var content = new StreamContent(new Body(this, from));
            content.Headers.ContentLength = length - from;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (from > 0) content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, length - 1, length);
            return Task.FromResult(response);
        }

        private sealed class Body(VirtualServer owner, long position) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                while (Interlocked.Read(ref owner._allowed) <= position)
                    await Task.Delay(5, ct);
                if (owner.DropAt is { } drop && position >= drop)
                {
                    owner.DropAt = null;
                    throw new IOException("Connection reset by peer (probe).");
                }
                var limit = Math.Min(Interlocked.Read(ref owner._allowed), owner.DropAt ?? long.MaxValue);
                var n = (int)Math.Min(buffer.Length, limit - position);
                buffer.Span[..n].Clear();
                position += n;
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

    /// <summary>Holds each song until the probe says Finish; reports on demand.</summary>
    private sealed class ProbeEngine(WhisperModelManager models) : ILyricsStudioEngine
    {
        private TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<LyricsStudioProgress>? _progress;

        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = models;
        public ManualResetEventSlim? OpenGate { get; init; }
        public IDisposable OpenSession(WhisperModelSize model)
        {
            OpenGate?.Wait(TimeSpan.FromSeconds(10));
            return new Handle();
        }
        public SemaphoreSlim Started { get; } = new(0);
        public void Report(LyricsStudioProgress p) => _progress!.Report(p);
        public void Finish() => _finish.TrySetResult();

        public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            _progress = progress;
            _finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Release();
            await _finish.Task.WaitAsync(ct);
            var words = new[] { "la", "la", "la", "na", "na", "na" };
            var lines = Enumerable.Range(0, 14).Select(i =>
            {
                var start = TimeSpan.FromSeconds(8 + i * 4.2);
                var w = words.Select((t, j) => new AlignedWord(t, start + TimeSpan.FromSeconds(j * 0.5), start + TimeSpan.FromSeconds(j * 0.5 + 0.45))).ToList();
                return new AlignedLine(string.Join(' ', words), start, start + TimeSpan.FromSeconds(3.2), w, i % 5 == 3 ? 0.4 : 0.92, false);
            }).ToList();
            return new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.88, "en", 84);
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
