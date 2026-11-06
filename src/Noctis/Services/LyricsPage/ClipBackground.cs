using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// A video background (the user's clip or the cached animated cover), decoded by the bundled
/// ffmpeg as an endless loop of raw BGRA frames already scaled and cropped to fill the canvas.
/// A reader thread pulls exact frames off the pipe into a two-slot buffer, never decoding past
/// the frame last asked for.
/// <para>Export (<c>blocking</c>) asks for frame <c>floor(clipSeconds × fps)</c> and waits for exactly
/// that frame, so the video content depends only on the frame index. Preview never waits: it
/// shows the newest decoded frame not past the asked one. The waits are bounded only to stop
/// broken I/O: no frame progress within <see cref="FirstFrameTimeoutMs"/>, ffmpeg exiting, or
/// <see cref="Dispose"/> ends a wait. Any failure makes the background <see cref="Failed"/> and
/// it draws <c>fallback</c> (the artwork wash) for the rest of the run.</para>
/// </summary>
internal sealed class ClipBackground : IPageBackground
{
    internal const int FirstFrameTimeoutMs = 5000;

    private readonly string _ffmpegPath;
    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _frameBytes;
    private readonly SKImageInfo _info;
    private readonly bool _blocking;
    private readonly IPageBackground _fallback;
    private readonly SKPaint _overlayPaint = new() { BlendMode = SKBlendMode.SrcOver };
    private readonly SKColor _tint;
    private volatile int _strength;
    private readonly object _gate = new();

    private Decode? _run;
    private int _generation;
    private SKImage? _image;
    private int _imageGeneration = -1;
    private long _imageIndex = -1;
    private bool _failed;
    private bool _disposed;
    private int? _processId;
    private bool _lastRunExited = true;

    /// <summary>One ffmpeg process and the reader thread draining it. Guarded by <c>_gate</c>.</summary>
    private sealed class Decode
    {
        public required Process Process { get; init; }
        public required Stream Stdout { get; init; }
        public required int Generation { get; init; }
        public required byte[] Front { get; set; }
        public required byte[] Back { get; set; }
        public Thread? Reader { get; set; }
        public long Decoded { get; set; } = -1;
        public long Want { get; set; }
        public bool Ended { get; set; }
        public bool Stop { get; set; }
    }

    internal static List<string> BuildArgs(string path, int width, int height, int fps)
    {
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        var f = fps.ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-stream_loop", "-1",
            "-i", path,
            "-an",
            "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},fps={f},format=bgra",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-",
        };
    }

    /// <param name="tint">The readability layer's colour, drawn over each frame at
    /// <see cref="PageBackgroundTint.OverlayAlpha"/> of <paramref name="strength"/>.</param>
    /// <param name="fallback">Drawn while no frame can be shown and after a failure; owned and disposed by this background.</param>
    internal ClipBackground(string ffmpegPath, string path, int width, int height, int fps,
        SKColor tint, int strength, bool blocking, IPageBackground fallback)
    {
        _ffmpegPath = ffmpegPath;
        _path = path;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _fps = Math.Max(1, fps);
        _frameBytes = _width * _height * 4;
        _info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        _blocking = blocking;
        _fallback = fallback;
        _tint = tint;
        _strength = strength;

        // Start right away so the first frame is (usually) ready when the first draw asks.
        lock (_gate)
        {
            if (!StartRun(0))
                _failed = true;
        }
    }

    public int Strength
    {
        set
        {
            _strength = value;
            _fallback.Strength = value;
        }
    }

    /// <summary>The background could not be decoded; it draws its fallback from now on.</summary>
    internal bool Failed
    {
        get { lock (_gate) return _failed; }
    }

    /// <summary>The id of the last ffmpeg process started (for tests).</summary>
    internal int? ProcessId
    {
        get { lock (_gate) return _processId; }
    }

    /// <summary>No ffmpeg process of this background is running (for tests).</summary>
    internal bool ProcessExited
    {
        get
        {
            lock (_gate)
            {
                if (_run == null) return _lastRunExited;
                try { return _run.Process.HasExited; }
                catch { return true; }
            }
        }
    }

    /// <summary>
    /// The frame for <paramref name="clipSeconds"/>, or null when there is none (preview before
    /// the first frame, failure, disposed). The image stays valid until the next call or
    /// <see cref="Dispose"/>.
    /// </summary>
    internal SKImage? FrameAt(double clipSeconds)
    {
        long index = double.IsFinite(clipSeconds) && clipSeconds > 0
            ? (long)Math.Floor(clipSeconds * _fps + 1e-6)
            : 0;

        Decode? stale = null;
        lock (_gate)
        {
            if (_failed || _disposed) return null;
            // A frame before the decoded one: the pipe only goes forward, so start over.
            if (_run != null && index < _run.Decoded)
                stale = Detach();
        }
        if (stale != null)
            Teardown(stale);

        Decode? broken = null;
        lock (_gate)
        {
            if (_failed || _disposed) return null;
            if (_run == null && !StartRun(index))
            {
                _failed = true;
                return null;
            }

            var run = _run!;
            if (run.Want != index)
            {
                run.Want = index;
                Monitor.PulseAll(_gate);
            }

            if (_blocking)
            {
                while (run.Decoded < index && !run.Ended && !run.Stop && !_disposed)
                {
                    // Every published frame pulses; a silent bound means ffmpeg stalled.
                    if (!Monitor.Wait(_gate, FirstFrameTimeoutMs))
                        break;
                }
                if (_disposed) return null;
                if (run.Decoded == index)
                    return ImageOf(run);
                _failed = true;
                broken = _run == run ? Detach() : null;
            }
            else
            {
                if (run.Ended)
                {
                    _failed = true;
                    broken = Detach();
                }
                else if (run.Decoded >= 0 && run.Decoded <= index)
                {
                    return ImageOf(run);
                }
                else
                {
                    return null;
                }
            }
        }

        if (broken != null)
            Teardown(broken);
        return null;
    }

    public void Draw(SKCanvas canvas, double clipSeconds)
    {
        var image = FrameAt(clipSeconds);
        lock (_gate)
        {
            if (_disposed) return;
            // Preview keeps the last frame on screen while a restarted decode catches up.
            if (image == null && !_blocking && !_failed)
                image = _image;
        }

        if (image == null)
        {
            _fallback.Draw(canvas, clipSeconds);
            return;
        }

        canvas.DrawImage(image, 0, 0);
        var alpha = PageBackgroundTint.OverlayAlpha(_strength);
        if (alpha > 0)
        {
            _overlayPaint.Color = _tint.WithAlpha(alpha);
            canvas.DrawRect(0, 0, _width, _height, _overlayPaint);
        }
    }

    public void Dispose()
    {
        Decode? run;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            run = Detach();
            Monitor.PulseAll(_gate);
        }
        if (run != null)
            Teardown(run);

        lock (_gate)
        {
            _image?.Dispose();
            _image = null;
        }
        _overlayPaint.Dispose();
        _fallback.Dispose();
    }

    /// <summary>Starts ffmpeg and its reader, wanting frames up to <paramref name="want"/>. Caller holds the gate.</summary>
    private bool StartRun(long want)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArgs(_path, _width, _height, _fps))
            psi.ArgumentList.Add(a);

        Process? process;
        try { process = Process.Start(psi); }
        catch { return false; }
        if (process == null) return false;

        // Drain stderr so a chatty ffmpeg can never block on that pipe.
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

        var run = new Decode
        {
            Process = process,
            Stdout = process.StandardOutput.BaseStream,
            Generation = ++_generation,
            Front = new byte[_frameBytes],
            Back = new byte[_frameBytes],
            Want = want,
        };
        run.Reader = new Thread(() => ReadLoop(run)) { IsBackground = true, Name = "Lyrics Page clip decode" };
        _run = run;
        _processId = process.Id;
        _lastRunExited = false;
        run.Reader.Start();
        return true;
    }

    /// <summary>Takes the current run out of service. Caller holds the gate, then calls <see cref="Teardown"/> outside it.</summary>
    private Decode? Detach()
    {
        var run = _run;
        _run = null;
        if (run != null)
        {
            run.Stop = true;
            Monitor.PulseAll(_gate);
        }
        return run;
    }

    /// <summary>Kills the run's ffmpeg (only that process and its children) and joins the reader.</summary>
    private void Teardown(Decode run)
    {
        try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); } catch { }
        bool exited;
        try { exited = run.Process.WaitForExit(2000); } catch { exited = true; }
        try { run.Stdout.Dispose(); } catch { }
        run.Reader?.Join(2000);
        try { run.Process.Dispose(); } catch { }
        lock (_gate)
        {
            if (_run == null)
                _lastRunExited = exited;
        }
    }

    private void ReadLoop(Decode run)
    {
        try
        {
            while (true)
            {
                byte[] target;
                lock (_gate)
                {
                    while (!run.Stop && run.Decoded >= run.Want)
                        Monitor.Wait(_gate);
                    if (run.Stop) return;
                    target = run.Back;
                }

                bool complete = ReadFrame(run.Stdout, target);

                lock (_gate)
                {
                    if (run.Stop) return;
                    if (!complete)
                    {
                        // The loop never ends on its own: end of stream means ffmpeg failed or died.
                        run.Ended = true;
                        Monitor.PulseAll(_gate);
                        return;
                    }
                    run.Back = run.Front;
                    run.Front = target;
                    run.Decoded++;
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                run.Ended = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool ReadFrame(Stream stdout, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n;
            try { n = stdout.Read(buffer, total, buffer.Length - total); }
            catch { n = 0; }
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>The run's newest frame as an image, copied once per frame. Caller holds the gate.</summary>
    private SKImage? ImageOf(Decode run)
    {
        if (_image == null || _imageGeneration != run.Generation || _imageIndex != run.Decoded)
        {
            _image?.Dispose();
            _image = SKImage.FromPixelCopy(_info, run.Front, _info.RowBytes);
            _imageGeneration = run.Generation;
            _imageIndex = run.Decoded;
        }
        return _image;
    }
}
