using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Linux output for the gapless engine (GitHub #127): one PulseAudio playback stream
/// (also served by PipeWire's pulse server) fed from the engine's render chain through
/// libpulse-simple, the client library VLC's own pulse output already depends on.
///
/// Without it Linux played every track through its own LibVLC player, so each track
/// change opened a new audio stream: a gap of tens to hundreds of ms on every boundary,
/// and on a high-latency Bluetooth sink the outgoing stream's buffered tail was cut while
/// the new stream started. Through this player the engine keeps a single stream open
/// across tracks, exactly as WasapiOut does on Windows, so the boundary is crossed
/// inside one render read.
///
/// The render thread pulls the provider one period at a time and hands it to the
/// blocking pa_simple_write, which paces it at the device rate. Stop() ends the thread
/// and closes the stream (the engine's idle park relies on that to let the device
/// suspend); Play() opens it again. A write error ends the thread with PlaybackStopped
/// carrying the error, and GaplessSink rebuilds the output from there.
/// </summary>
internal sealed class PulseSimpleWavePlayer : IWavePlayer
{
    private const string LibPulseSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";
    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_SAMPLE_FLOAT32LE = 5;

    /// <summary>Pull period: the same 10 ms as WASAPI's default shared-mode device period.</summary>
    private const int PeriodMs = 10;

    // How often the render thread asks the server for the stream's latency.
    private const int LatencyPollPeriods = 50;

    private readonly int _latencyMs;
    private readonly object _gate = new();
    private IWaveProvider? _source;
    private IntPtr _stream;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private long _latencyUs = -1; // last server-reported stream latency; -1 = none yet

    public PulseSimpleWavePlayer(int latencyMs)
    {
        _latencyMs = Math.Max(PeriodMs * 2, latencyMs);
    }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>Stored only: the engine applies the user level in its own render chain.</summary>
    public float Volume { get; set; } = 1f;

    public PlaybackState PlaybackState => _state;

    public WaveFormat OutputWaveFormat => _source?.WaveFormat!;

    /// <summary>
    /// Time from a sample being handed over to it being heard, as the sound server reports
    /// it: our buffered audio plus the sink's own latency — a Bluetooth sink adds a few
    /// hundred ms that a fixed figure would leave the lyrics clock ahead by. Null until the
    /// first report.
    /// </summary>
    public int? LatencyMs
    {
        get
        {
            var us = Interlocked.Read(ref _latencyUs);
            return us < 0 ? null : (int)Math.Min(int.MaxValue, us / 1000);
        }
    }

    /// <summary>
    /// Takes the IEEE-float stereo-or-more provider and connects the stream, so a
    /// missing pulse server (or library) fails here and the engine falls back to the
    /// per-track LibVLC output instead of rendering into nothing.
    /// </summary>
    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        if (waveProvider.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("PulseSimpleWavePlayer renders IEEE float only", nameof(waveProvider));
        lock (_gate)
        {
            if (_thread != null)
                throw new InvalidOperationException("Can't re-initialize during playback");
            _source = waveProvider;
            if (_stream == IntPtr.Zero)
                _stream = Connect(waveProvider.WaveFormat, _latencyMs);
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
            if (_stream == IntPtr.Zero)
                _stream = Connect(source.WaveFormat, _latencyMs); // reopened after a Stop
            _stopRequested = false;
            var stream = _stream;
            _thread = new Thread(() => RenderLoop(source, stream))
            {
                IsBackground = true,
                Name = "NoctisPulseOutput",
            };
            _thread.Start();
        }
    }

    /// <summary>Stops pulling but keeps the stream; the server plays out what it holds.</summary>
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
            _stopRequested = true;
            _state = PlaybackState.Stopped;
        }
        // Joined like WasapiOut.Stop, so nothing is pulled after Stop returns. The thread
        // closes its own stream on the way out. A PlaybackStopped handler runs on the
        // render thread and must not wait on itself.
        if (thread != null && thread != Thread.CurrentThread)
            thread.Join();
        lock (_gate)
        {
            if (_thread == null && _stream != IntPtr.Zero)
            {
                // Connected by Init but never played.
                Free(_stream, flush: true);
                _stream = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Stop();

    private void RenderLoop(IWaveProvider source, IntPtr stream)
    {
        Exception? error = null;
        try
        {
            var format = source.WaveFormat;
            var blockAlign = Math.Max(1, format.BlockAlign);
            var buffer = new byte[Math.Max(1, format.SampleRate * PeriodMs / 1000) * blockAlign];
            var untilLatencyPoll = 0;
            while (!_stopRequested)
            {
                if (_state != PlaybackState.Playing)
                {
                    Thread.Sleep(PeriodMs);
                    continue;
                }
                var read = source.Read(buffer, 0, buffer.Length);
                // A 0-byte read is end of stream for WasapiOut (it stops and raises
                // PlaybackStopped); this output does the same.
                if (read <= 0)
                    return;
                read -= read % blockAlign;
                if (read > 0 && pa_simple_write(stream, buffer, (UIntPtr)read, out var err) < 0)
                    throw new IOException($"PulseAudio write failed: {ErrorText(err)}");
                // Asked on this thread: the simple API is not to be used from two at once.
                if (--untilLatencyPoll <= 0)
                {
                    untilLatencyPoll = LatencyPollPeriods;
                    var latency = pa_simple_get_latency(stream, out var latencyErr);
                    if (latencyErr == 0 && latency != ulong.MaxValue)
                        Interlocked.Exchange(ref _latencyUs, (long)Math.Min(latency, long.MaxValue));
                }
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
                Free(stream, flush: true);
                if (_stream == stream)
                    _stream = IntPtr.Zero;
                if (_thread == Thread.CurrentThread)
                {
                    _thread = null;
                    _state = PlaybackState.Stopped;
                }
            }
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    /// <summary>
    /// Server-side buffer for a stream of <paramref name="latencyMs"/>: the target length
    /// is the latency, refilled a period at a time; the server picks the rest. pa_simple
    /// connects with ADJUST_LATENCY, so the target is the whole latency, sink included, and
    /// the server holds only part of it as buffer (see GaplessSink.LinuxOutputLatencyMs).
    /// Pure; internal for tests.
    /// </summary>
    internal static BufferAttr BufferAttrFor(int sampleRate, int blockAlign, int latencyMs) => new()
    {
        MaxLength = uint.MaxValue,
        TLength = (uint)(sampleRate * latencyMs / 1000 * blockAlign),
        PreBuf = uint.MaxValue,
        MinReq = (uint)(sampleRate * PeriodMs / 1000 * blockAlign),
        FragSize = uint.MaxValue,
    };

    private static IntPtr Connect(WaveFormat format, int latencyMs)
    {
        var spec = new SampleSpec
        {
            Format = PA_SAMPLE_FLOAT32LE,
            Rate = (uint)format.SampleRate,
            Channels = (byte)format.Channels,
        };
        var attr = BufferAttrFor(format.SampleRate, format.BlockAlign, latencyMs);
        // Client name "Noctis": the same application the LibVLC streams register under
        // (LibVLC.SetUserAgent), so the desktop mixer shows one app.
        var stream = pa_simple_new(null, "Noctis", PA_STREAM_PLAYBACK, null, "Music",
            ref spec, IntPtr.Zero, ref attr, out var err);
        if (stream == IntPtr.Zero)
            throw new IOException($"PulseAudio connect failed: {ErrorText(err)}");
        return stream;
    }

    private static void Free(IntPtr stream, bool flush)
    {
        if (stream == IntPtr.Zero) return;
        try
        {
            if (flush) pa_simple_flush(stream, out _);
        }
        catch { /* closing anyway */ }
        try { pa_simple_free(stream); } catch { }
    }

    private static string ErrorText(int error)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(pa_strerror(error));
            return string.IsNullOrEmpty(text) ? $"error {error}" : $"{text} ({error})";
        }
        catch
        {
            return $"error {error}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    [DllImport(LibPulseSimple)]
    private static extern IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        ref SampleSpec ss, IntPtr map, ref BufferAttr attr, out int error);

    [DllImport(LibPulseSimple)]
    private static extern int pa_simple_write(IntPtr s, byte[] data, UIntPtr bytes, out int error);

    [DllImport(LibPulseSimple)]
    private static extern int pa_simple_flush(IntPtr s, out int error);

    [DllImport(LibPulseSimple)]
    private static extern ulong pa_simple_get_latency(IntPtr s, out int error);

    [DllImport(LibPulseSimple)]
    private static extern void pa_simple_free(IntPtr s);

    [DllImport(LibPulse)]
    private static extern IntPtr pa_strerror(int error);
}
