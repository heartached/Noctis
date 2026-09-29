using System.Diagnostics;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Silent test mode (NOCTIS_AOUT=dummy): an <see cref="IWavePlayer"/> with no audio
/// device behind it. It pulls its wave provider at real-time pace on its own thread and
/// throws the samples away, so the gapless engine's whole render chain (splice provider,
/// mute gate, beat tap, stall probe) runs exactly as it does under WasapiOut while nothing
/// is ever heard. VLC's --aout=dummy alone cannot give that: the engine renders through
/// its own WasapiOut, not through VLC's output. Never used outside <see cref="SilentMode"/>.
/// </summary>
internal sealed class NullWavePlayer : IWavePlayer
{
    /// <summary>Pull period, the same as WASAPI's default shared-mode device period.</summary>
    private const int PeriodMs = 10;

    // A render thread starved past this (or a process suspended by system sleep) drops the
    // backlog instead of pulling it all in one burst: WasapiOut can only ever pull what its
    // 100 ms buffer holds, so a longer catch-up would be behaviour the real sink never shows.
    private const int MaxBacklogMs = 100;

    /// <summary>
    /// True when <paramref name="env"/> (the NOCTIS_AOUT value) asks for no audio device at
    /// all: "dummy", case-insensitive, surrounding whitespace ignored. Pure; internal for tests.
    /// </summary>
    internal static bool IsSilentAudio(string? env) =>
        string.Equals(env?.Trim(), "dummy", StringComparison.OrdinalIgnoreCase);

    /// <summary>NOCTIS_AOUT=dummy for this process (read once): VLC gets --aout=dummy on every OS,
    /// the gapless engine renders here instead of WasapiOut, and the WASAPI sinks and the
    /// keep-alive stay off.</summary>
    internal static bool SilentMode { get; } = IsSilentAudio(Environment.GetEnvironmentVariable("NOCTIS_AOUT"));

    private readonly object _gate = new();
    private IWaveProvider? _source;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private volatile PlaybackState _state = PlaybackState.Stopped;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>Stored only; nothing is rendered for it to scale.</summary>
    public float Volume { get; set; } = 1f;

    public PlaybackState PlaybackState => _state;

    public WaveFormat OutputWaveFormat => _source?.WaveFormat!;

    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        lock (_gate)
        {
            if (_thread != null)
                throw new InvalidOperationException("Can't re-initialize during playback");
            _source = waveProvider;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            var source = _source ?? throw new InvalidOperationException("Must call Init first");
            if (_state == PlaybackState.Playing) return;
            _state = PlaybackState.Playing;
            if (_thread != null) return; // resuming from Pause: the thread is still there
            var stop = new CancellationTokenSource();
            _stop = stop;
            _thread = new Thread(() => RenderLoop(source, stop))
            {
                IsBackground = true,
                Name = "NoctisNullOutput",
            };
            _thread.Start();
        }
    }

    /// <summary>Stops pulling but keeps the thread, like WasapiOut.Pause.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state == PlaybackState.Playing)
                _state = PlaybackState.Paused;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _state = PlaybackState.Stopped;
            _stop?.Cancel();
            _stop = null;
        }
        // Joined like WasapiOut.Stop, so nothing is pulled after Stop returns. A
        // PlaybackStopped handler runs on the render thread and must not wait on itself.
        if (thread != null && thread != Thread.CurrentThread)
            thread.Join();
    }

    public void Dispose() => Stop();

    private void RenderLoop(IWaveProvider source, CancellationTokenSource stop)
    {
        Exception? error = null;
        try
        {
            var token = stop.Token;
            var format = source.WaveFormat;
            var rate = format.SampleRate;
            var blockAlign = Math.Max(1, format.BlockAlign);
            var periodFrames = Math.Max(1, rate * PeriodMs / 1000);
            var maxBacklogFrames = (long)rate * MaxBacklogMs / 1000;
            var buffer = new byte[periodFrames * blockAlign];
            var clock = new Stopwatch();
            long rendered = 0; // frames pulled since the clock last (re)started

            while (!token.IsCancellationRequested)
            {
                if (_state != PlaybackState.Playing)
                {
                    // Paused: pull nothing, and restart the pacing clock on resume so the
                    // paused time is not owed afterwards as a burst.
                    clock.Reset();
                    token.WaitHandle.WaitOne(PeriodMs);
                    continue;
                }
                if (!clock.IsRunning)
                {
                    clock.Start();
                    rendered = 0;
                }

                var due = (long)(clock.Elapsed.TotalSeconds * rate) - rendered;
                if (due > maxBacklogFrames)
                {
                    rendered += due - maxBacklogFrames;
                    due = maxBacklogFrames;
                }
                while (due > 0 && _state == PlaybackState.Playing && !token.IsCancellationRequested)
                {
                    var frames = (int)Math.Min(due, periodFrames);
                    // The data is discarded. A 0-byte read is end of stream for WasapiOut
                    // (it stops and raises PlaybackStopped); this sink does the same.
                    if (source.Read(buffer, 0, frames * blockAlign) == 0)
                        return;
                    rendered += frames;
                    due -= frames;
                }

                // Sleep until the next period is due (the OS timer may round it up).
                var nextMs = (rendered + periodFrames) * 1000.0 / rate - clock.Elapsed.TotalMilliseconds;
                token.WaitHandle.WaitOne(Math.Clamp((int)Math.Ceiling(nextMs), 1, PeriodMs));
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            lock (_gate)
            {
                // Stop() already reset these; an end of stream or a throwing provider did not.
                if (_thread == Thread.CurrentThread)
                {
                    _thread = null;
                    _state = PlaybackState.Stopped;
                    _stop = null;
                }
            }
            stop.Dispose();
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }
}
