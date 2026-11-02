using System.Diagnostics;
using Noctis.Plugins.Chords.Analysis;

namespace Noctis.Plugins.Chords;

/// <summary>What the Chords plugin needs from Noctis (the real one: <see cref="PluginChordsHost"/>; tests use a fake).</summary>
public interface IChordsHost
{
    string? TrackPath { get; }
    string TrackTitle { get; }
    string TrackArtist { get; }
    TimeSpan Position { get; }
    bool IsPlaying { get; }
    /// <summary>Raised on the UI thread.</summary>
    event EventHandler? TrackChanged;

    bool Sevenths { get; }
    bool AutoFind { get; }
    /// <summary>Raised on the UI thread.</summary>
    event EventHandler? SettingsChanged;

    /// <summary>False when Noctis cannot decode (no ffmpeg, or a Noctis without plugin API 1.6).</summary>
    bool CanDecode { get; }
    Task<DecodedAudio> DecodeAsync(string path, int sampleRate, TimeSpan maxDuration, CancellationToken ct);

    void Seek(TimeSpan position);
    string? DataDirectory { get; }
    void Log(string message);

    /// <summary>Runs <paramref name="action"/> on the UI thread, later.</summary>
    void Post(Action action);
}

public enum ChordsState
{
    /// <summary>Nothing is playing.</summary>
    NoSong,
    /// <summary>A song is playing and has not been analysed (or the analysis was cancelled).</summary>
    Idle,
    /// <summary>Decoding and analysing in the background.</summary>
    Finding,
    /// <summary><see cref="ChordsSession.Analysis"/> holds the song's chords.</summary>
    Ready,
    /// <summary>The analysis failed; <see cref="ChordsSession.Error"/> says why.</summary>
    Failed,
    /// <summary>Noctis has no decoder to offer.</summary>
    NoDecoder,
}

/// <summary>
/// The Chords plugin's state for the playing song: a cached result shows at once; otherwise
/// <see cref="Find"/> decodes the song (through Noctis) and analyses it on a below-normal-priority
/// background thread, cancellable, and caches the result. A new song cancels a running analysis.
/// While a Chords view is open and "Find chords automatically" is on, new songs are analysed by
/// themselves. All public members are for the UI thread; <see cref="Changed"/> is raised there.
/// </summary>
public sealed class ChordsSession : IDisposable
{
    /// <summary>Songs longer than this are analysed for their first 15 minutes.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(15);

    private readonly IChordsHost _host;
    private readonly ChordCache _cache;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private int _generation;
    private int _views;
    private TimeSpan _lastPosition;
    private TimeSpan _lastPositionAt;
    private bool _lastSevenths;
    private bool _disposed;

    public ChordsSession(IChordsHost host)
    {
        _host = host;
        _cache = new ChordCache(host.DataDirectory, host.Log);
        _lastSevenths = host.Sevenths;
        host.TrackChanged += OnTrackChanged;
        host.SettingsChanged += OnSettingsChanged;
        LoadForCurrentTrack();
    }

    public event EventHandler? Changed;

    public ChordsState State { get; private set; }
    public ChordAnalysis? Analysis { get; private set; }
    public string? Error { get; private set; }
    /// <summary>0..1 while <see cref="ChordsState.Finding"/>; below 0 while the song is still being decoded.</summary>
    public double Progress { get; private set; }
    public string? TrackPath { get; private set; }
    public string TrackTitle => _host.TrackTitle;
    public string TrackArtist => _host.TrackArtist;
    public ChordCache Cache => _cache;

    /// <summary>The task of the running (or last) analysis (tests).</summary>
    public Task? Running { get; private set; }

    /// <summary>
    /// The playback position between the host's ~100 ms updates: the last value it reported plus
    /// the time since it changed, while playing (capped at 250 ms ahead).
    /// </summary>
    public TimeSpan PredictedPosition
    {
        get
        {
            var now = _clock.Elapsed;
            var pos = _host.Position;
            if (pos != _lastPosition)
            {
                _lastPosition = pos;
                _lastPositionAt = now;
            }
            if (!_host.IsPlaying) return pos;
            var ahead = now - _lastPositionAt;
            return pos + (ahead > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : ahead);
        }
    }

    public void Seek(TimeSpan position) => _host.Seek(position);

    /// <summary>A Chords view opened: with "Find chords automatically" on, the song is analysed now.</summary>
    public void ViewOpened()
    {
        _views++;
        if (_host.AutoFind && State == ChordsState.Idle) Find();
    }

    public void ViewClosed() => _views = Math.Max(0, _views - 1);

    /// <summary>Analyses the playing song (again, when <paramref name="force"/>), unless it is already being analysed.</summary>
    public void Find(bool force = false)
    {
        if (_disposed || TrackPath is not { } path || State == ChordsState.Finding) return;
        if (State == ChordsState.Ready && !force) return;
        if (!_host.CanDecode)
        {
            Set(ChordsState.NoDecoder);
            return;
        }
        _cts?.Cancel(); // not disposed: the old run may still be registering on its token
        var cts = _cts = new CancellationTokenSource();
        var generation = ++_generation;
        var sevenths = _host.Sevenths;
        Analysis = null;
        Error = null;
        Progress = -1;
        Set(ChordsState.Finding);
        Running = Task.Run(() => RunAsync(path, sevenths, generation, cts.Token));
    }

    /// <summary>Stops a running analysis.</summary>
    public void Cancel()
    {
        if (State != ChordsState.Finding) return;
        _generation++;
        _cts?.Cancel();
        Progress = 0;
        Set(ChordsState.Idle);
    }

    private async Task RunAsync(string path, bool sevenths, int generation, CancellationToken ct)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var audio = await _host.DecodeAsync(path, ChordAnalyzer.SampleRate, MaxLength, ct).ConfigureAwait(false);
            var decoded = watch.ElapsedMilliseconds;
            var progress = new ProgressSink(this, generation);
            var analysis = await RunBelowNormal(() => ChordAnalyzer.Analyze(audio.Samples, audio.SampleRate,
                new ChordRecognizerOptions { Sevenths = sevenths }, progress, ct)).ConfigureAwait(false);
            _cache.Save(path, analysis);
            _host.Log($"{Path.GetFileName(path)}: {analysis.Segments.Count} chords, key {analysis.Key?.Name ?? "-"}, " +
                      $"{audio.Duration.TotalSeconds:0} s decoded in {decoded} ms, analysed in {watch.ElapsedMilliseconds - decoded} ms");
            _host.Post(() =>
            {
                if (generation != _generation || _disposed) return;
                Analysis = analysis;
                Progress = 1;
                Set(ChordsState.Ready);
            });
        }
        catch (OperationCanceledException) { /* a new song or Cancel: already handled */ }
        catch (Exception ex)
        {
            _host.Log("analysis failed: " + ex.Message);
            _host.Post(() =>
            {
                if (generation != _generation || _disposed) return;
                Error = ex is FileNotFoundException ? "The song file is not there anymore." : ex.Message;
                Set(ChordsState.Failed);
            });
        }
    }

    /// <summary>The number crunching on its own below-normal thread, so playback and the UI keep the CPU first.</summary>
    private static Task<T> RunBelowNormal<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (OperationCanceledException ex) { done.SetCanceled(ex.CancellationToken); }
            catch (Exception ex) { done.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Chords analysis",
        };
        thread.Start();
        return done.Task;
    }

    private sealed class ProgressSink : IProgress<double>
    {
        private readonly ChordsSession _session;
        private readonly int _generation;
        private int _shown = -1;

        public ProgressSink(ChordsSession session, int generation)
        {
            _session = session;
            _generation = generation;
        }

        public void Report(double value)
        {
            var percent = (int)(value * 100);
            if (percent == _shown) return;
            _shown = percent;
            _session._host.Post(() =>
            {
                if (_generation != _session._generation || _session.State != ChordsState.Finding) return;
                _session.Progress = Math.Clamp(value, 0, 1);
                _session.Changed?.Invoke(_session, EventArgs.Empty);
            });
        }
    }

    private void OnTrackChanged(object? sender, EventArgs e) => LoadForCurrentTrack();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_host.Sevenths == _lastSevenths) return;
        _lastSevenths = _host.Sevenths;
        LoadForCurrentTrack(sameSong: true);
    }

    /// <summary>The song changed (or the Sevenths setting): drop the old result, show a cached one, or start over.</summary>
    private void LoadForCurrentTrack(bool sameSong = false)
    {
        if (_disposed) return;
        var path = _host.TrackPath;
        if (!sameSong && path == TrackPath && State != ChordsState.NoSong) return;
        _generation++;
        _cts?.Cancel();
        TrackPath = string.IsNullOrEmpty(path) ? null : path;
        Analysis = null;
        Error = null;
        Progress = 0;
        if (TrackPath is null)
        {
            Set(ChordsState.NoSong);
            return;
        }
        if (_cache.TryLoad(TrackPath, _host.Sevenths) is { } cached)
        {
            Analysis = cached;
            Set(ChordsState.Ready);
            return;
        }
        Set(ChordsState.Idle);
        if (_views > 0 && _host.AutoFind) Find();
    }

    private void Set(ChordsState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _cts?.Cancel();
        _cts = null;
        _host.TrackChanged -= OnTrackChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        Changed = null;
    }
}
