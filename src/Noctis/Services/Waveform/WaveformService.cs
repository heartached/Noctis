using System.Runtime.InteropServices;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.Waveform;

/// <summary>
/// Which files the waveform seek bar wants computed: the current track, then the next
/// <see cref="DefaultLookahead"/> queued tracks so a skip lands on a ready waveform.
/// Streams, audio-CD tracks, SMB/WebDAV sources and anything whose path is a URI are
/// left out (plain bar): reading a whole file over the network would compete with the
/// playback stream for the same link.
/// </summary>
public static class WaveformPlanner
{
    public const int DefaultLookahead = 2;

    public static bool IsEligible(Track? track) =>
        track != null && !track.IsRemoteStream && track.HasFilesystemPath &&
        track.SourceType is not (SourceType.Smb or SourceType.WebDav);

    /// <summary>
    /// UNC paths and (on Windows) mapped network drives. Checked on the worker, not in
    /// <see cref="Plan"/>: resolving a drive's type can touch the system.
    /// </summary>
    public static bool IsNetworkLocation(string path)
    {
        try
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                path = path[4..];
            else if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                return true;
            if (!OperatingSystem.IsWindows()) return false;
            var root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Plan(Track? current, IReadOnlyList<Track> upNext, int lookahead = DefaultLookahead)
    {
        var plan = new List<string>(1 + lookahead);
        if (IsEligible(current)) plan.Add(current!.FilePath);
        for (var i = 0; i < upNext.Count && i < lookahead; i++)
        {
            var t = upNext[i];
            if (IsEligible(t) && !plan.Contains(t.FilePath, PathComparison.Comparer))
                plan.Add(t.FilePath);
        }
        return plan;
    }
}

/// <summary>
/// Computes waveforms in the background, one file at a time, in plan order (current
/// track first), serving cache hits immediately and handing results out through
/// <see cref="WaveformReady"/>.
///
/// Playback safety: a single dedicated worker thread (created on the first non-empty
/// plan, so nothing runs while the setting is off) at BelowNormal priority, in Windows
/// background mode (very low I/O priority); a decode starts only after the plan has been
/// stable for <see cref="Options.DecodeStartDelay"/> — the new track is open and
/// buffered by then, and skipping quickly through a queue decodes nothing; a decode whose
/// file drops out of the plan is cancelled (ffmpeg killed). A file that failed to decode
/// is not retried this session.
/// </summary>
public sealed class WaveformService : IDisposable
{
    public sealed record Options(TimeSpan DecodeStartDelay, bool LowPriorityThread)
    {
        public static Options Default { get; } = new(TimeSpan.FromSeconds(2), true);
    }

    private readonly IWaveformDecoder _decoder;
    private readonly WaveformCache _cache;
    private readonly Options _options;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);

    private string[] _plan = Array.Empty<string>();
    private int _planVersion;
    private CancellationTokenSource? _inflightCts;
    private string? _inflightPath;
    private Thread? _thread;
    private volatile bool _disposed;

    public WaveformService(IWaveformDecoder decoder, WaveformCache cache, Options? options = null)
    {
        _decoder = decoder;
        _cache = cache;
        _options = options ?? Options.Default;
    }

    /// <summary>
    /// A waveform for <c>path</c> is available (computed or loaded from the cache).
    /// Raised on the worker thread; handlers marshal to the UI thread themselves.
    /// </summary>
    public event Action<string, WaveformData>? WaveformReady;

    /// <summary>Paths currently planned (for diagnostics/tests).</summary>
    public IReadOnlyList<string> CurrentPlan
    {
        get { lock (_gate) return _plan; }
    }

    /// <summary>
    /// Replaces the plan (current track first). An empty plan idles the worker. A decode
    /// in flight for a file no longer planned is cancelled; one still planned finishes.
    /// </summary>
    public void SetPlan(IReadOnlyList<string> paths)
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_plan.Length == paths.Count && _plan.SequenceEqual(paths, PathComparison.Comparer))
                return; // queue edits past the lookahead: nothing to redo
            _plan = paths.ToArray();
            _planVersion++;
            if (_inflightPath != null && !_plan.Contains(_inflightPath, PathComparison.Comparer))
                _inflightCts?.Cancel();
            if (_plan.Length > 0 && _thread == null)
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "waveform-worker",
                    Priority = _options.LowPriorityThread ? ThreadPriority.BelowNormal : ThreadPriority.Normal,
                };
                _thread.Start();
            }
        }
        _wake.Set();
    }

    private void Run()
    {
        if (_options.LowPriorityThread) EnterBackgroundIoMode();
        // The wake event is never disposed: the service lives as long as the app, and a
        // disposed handle would turn a late SetPlan/Dispose race into an exception.
        while (!_disposed)
        {
            _wake.WaitOne();
            if (_disposed) break;
            try
            {
                ProcessPlan();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.Worker", ex.Message);
            }
        }
    }

    private void ProcessPlan()
    {
        while (!_disposed)
        {
            string[] plan;
            int version;
            lock (_gate)
            {
                plan = _plan;
                version = _planVersion;
            }

            if (!RunPlanPass(plan, version)) continue; // plan changed mid-pass: start over
            return;
        }
    }

    /// <summary>One pass over <paramref name="plan"/>. False when the plan changed under
    /// it (the caller restarts with the new one).</summary>
    private bool RunPlanPass(string[] plan, int version)
    {
        var waited = false;
        foreach (var path in plan)
        {
            if (_disposed) return true;
            if (!IsCurrentVersion(version)) return false;

            if (WaveformPlanner.IsNetworkLocation(path)) continue;
            var id = WaveformFileIdentity.TryResolve(path);
            if (id is not { } identity || identity.Size > FfmpegWaveformDecoder.MaxFileBytes) continue;

            var cached = _cache.TryLoad(identity);
            if (cached != null)
            {
                Raise(path, cached);
                continue;
            }

            var key = WaveformCache.KeyFor(identity);
            lock (_gate)
            {
                if (_failedKeys.Contains(key)) continue;
            }

            // Debounce before the first decode of this plan: returns early (false) when a
            // newer plan arrives during the wait.
            if (!waited)
            {
                waited = true;
                if (_options.DecodeStartDelay > TimeSpan.Zero && _wake.WaitOne(_options.DecodeStartDelay))
                    return false;
            }

            using var cts = new CancellationTokenSource();
            lock (_gate)
            {
                if (version != _planVersion) return false;
                _inflightCts = cts;
                _inflightPath = path;
            }

            WaveformData? data = null;
            var cancelled = false;
            try
            {
                data = _decoder.Decode(path, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.Decode",
                    $"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _inflightCts = null;
                    _inflightPath = null;
                }
            }

            if (cancelled || _disposed) return _disposed;
            if (data == null)
            {
                lock (_gate) _failedKeys.Add(key);
                continue;
            }

            _cache.Store(identity, data);
            Raise(path, data);
        }
        return true;
    }

    private bool IsCurrentVersion(int version)
    {
        lock (_gate) return version == _planVersion;
    }

    private void Raise(string path, WaveformData data)
    {
        try
        {
            WaveformReady?.Invoke(path, data);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.ReadyHandler", ex.Message);
        }
    }

    // Windows background mode: the thread's I/O (the cache-priming read) and memory
    // priority drop to "very low", so the audio engine's reads of the same disk go first.
    private const int ThreadModeBackgroundBegin = 0x00010000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    private static void EnterBackgroundIoMode()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin))
                DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.BackgroundMode",
                    $"SetThreadPriority failed ({Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.BackgroundMode", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _inflightCts?.Cancel();
            _plan = Array.Empty<string>();
            _planVersion++;
        }
        _wake.Set();
    }
}
