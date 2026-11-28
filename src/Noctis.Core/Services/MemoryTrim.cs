using System.Runtime;
using System.Runtime.InteropServices;

namespace Noctis.Services;

/// <summary>
/// Gives memory back after a burst of large garbage, without ever pausing the app for it.
///
/// The startup pipeline (library parse, tag reads during the scan, artwork
/// backfill) leaves hundreds of MB of dead Large Object Heap arrays behind —
/// measured at 236 MB of unrooted byte[] on a 7k-track library, most of them
/// embedded cover files TagLib had materialised. Nothing allocates afterwards, so
/// the GC has no reason to run a gen2 and the memory stays committed for the whole
/// session: that is the "Noctis sits at 500+ MB doing nothing" report.
///
/// The trim is a background gen2 (the dead objects are freed while every thread keeps
/// running), and on Windows, once the app is in the background and idle, a working-set
/// trim that hands its physical pages back to the OS. It never asks for a blocking
/// collection: one suspends every managed thread, the audio render thread and the UI
/// thread that takes a Play click included. The compacting trim this replaced paused
/// 306 ms on a 575 MB harness heap, and a press landing in it waited 345 ms for audio
/// (11-27). See <see cref="BackgroundCollect"/> for why even the background request is
/// made in <see cref="GCLatencyMode.SustainedLowLatency"/>.
///
/// Requests are debounced: every producer (startup, a scan, a backfill) calls
/// <see cref="RequestAfterIdle(string)"/> when it finishes, and the collection runs once
/// <see cref="Quiet"/> has passed since the last request, so overlapping producers
/// pay for one collection, after all of them are done.
/// </summary>
public static class MemoryTrim
{
    /// <summary>How long after the last request the trim runs.</summary>
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Set by the app: true while music plays.</summary>
    public static Func<bool>? IsAudioPlaying { get; set; }

    /// <summary>
    /// Test seam: what runs when the quiet period elapses, given whether the working set is
    /// trimmed after the background collection. Null = the real trim.
    /// </summary>
    internal static Action<bool>? Collector { get; set; }

    private static readonly object Gate = new();
    private static int _pending;
    private static string _reason = "";
    private static bool _playing;
    private static int _lowLatencyHolds; // background collections in flight (see BackgroundCollect)
    private static bool _deferred;       // a working-set trim is owed (desktop)
    private static GCLatencyMode _restoreMode = GCLatencyMode.Interactive;
    private static bool? _appInBackground; // null: the host does not report it (server, phone)
    private static long _idleSince = Environment.TickCount64;

    /// <summary>
    /// Below this much allocated (live and dead) the collection is skipped: there is little to give
    /// back, and on a small heap the runtime runs a requested non-blocking gen2 as a blocking one
    /// anyway (harness 11-28: heaps of ~1-25 MB, 3-5 of 6 requests became full blocking gen2s of up
    /// to 11.5 ms, even in SustainedLowLatency; from 53 MB on, 6 of 6 stayed background, 0.1-0.3 ms).
    /// </summary>
    internal const long MinHeapToCollect = 64L * 1024 * 1024;

    /// <summary>Whether a heap of <paramref name="allocatedBytes"/> is worth a collection. Pure; internal for tests.</summary>
    internal static bool ShouldCollect(long allocatedBytes) => allocatedBytes >= MinHeapToCollect;

    /// <summary>How long playback must have been stopped or paused before the working set is trimmed.</summary>
    public static TimeSpan LongIdle { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Test seam: the clock idle time is measured on (milliseconds).</summary>
    internal static Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// Set by the desktop app: true while nothing of it is on screen (main window hidden or
    /// minimized and no mini player). The working-set trim only runs then, see
    /// <see cref="MayTrimWorkingSet"/>.
    /// </summary>
    public static void SetAppInBackground(bool inBackground)
    {
        bool owed;
        lock (Gate)
        {
            _appInBackground = inBackground;
            owed = inBackground && _deferred && !_playing;
        }
        if (owed) RequestAfterIdle("app in the background (deferred trim)", LongIdle + Quiet);
    }

    /// <summary>Test seam: back to a host that never reported its windows.</summary>
    internal static void ForgetAppState()
    {
        lock (Gate)
        {
            _appInBackground = null;
            _deferred = false;
        }
    }

    /// <summary>
    /// Whether the working set is trimmed after the background collection: Windows hosts that
    /// report their windows (the desktop), with nothing playing, the whole app in the
    /// background and playback idle for <see cref="LongIdle"/>. The trim suspends no thread
    /// (harness: the call took 57-107 ms on the trimming thread, a gen1 right after it paused
    /// 0.9 ms), but pages touched later fault back in, so it is kept away from playback and
    /// from a window on screen. Pure; internal for tests.
    /// </summary>
    internal static bool MayTrimWorkingSet(bool playing, bool? appInBackground, long idleMs, TimeSpan longIdle) =>
        !playing && appInBackground == true && idleMs >= longIdle.TotalMilliseconds;

    /// <summary>
    /// Called by the app on every playback state change. While music plays the GC runs in
    /// <see cref="GCLatencyMode.SustainedLowLatency"/>: gen2 collections it starts on its own
    /// (allocation budget, GC.AddMemoryPressure from decoded covers) stay background ones
    /// instead of blocking compacting ones. The 11-27 "audio cut off" was one 760 ms blocking
    /// gen2 (gcs=1/1/1) that froze the render thread far past the 100 ms device buffer; a
    /// headless probe (270 MB fragmented heap, ArtworkCache-style AddMemoryPressure churn)
    /// gave a 281-309 ms blocking compacting gen2 in Interactive and none in
    /// SustainedLowLatency, the heap ending at the same 270 MB. Low system memory still gets
    /// full blocking collections from the runtime. When playback stops the previous mode
    /// comes back, and a working-set trim owed while playing is asked for.
    /// </summary>
    public static void SetAudioPlaying(bool playing)
    {
        var runDeferred = false;
        lock (Gate)
        {
            if (_playing == playing) return;
            _playing = playing;
            ApplyLatencyModeLocked();
            if (!playing)
            {
                _idleSince = Now();
                runDeferred = _deferred;
            }
        }
        // Not right away: the working-set trim needs playback idle for LongIdle, and is checked
        // again when it fires. Quiet on top so the timer never lands a clock tick short of it.
        if (runDeferred) RequestAfterIdle("playback stopped (deferred trim)", LongIdle + Quiet);
    }

    // SustainedLowLatency while music plays or a trim's background collection is in flight,
    // the mode the app had otherwise.
    private static void ApplyLatencyModeLocked()
    {
        try
        {
            var current = GCSettings.LatencyMode;
            if (_playing || _lowLatencyHolds > 0)
            {
                if (current == GCLatencyMode.SustainedLowLatency) return;
                _restoreMode = current;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            else if (current == GCLatencyMode.SustainedLowLatency)
            {
                GCSettings.LatencyMode = _restoreMode;
            }
        }
        catch
        {
            // Best effort: a runtime without concurrent GC keeps its mode.
        }
    }

    private static bool AudioPlaying => IsAudioPlaying?.Invoke() == true || Volatile.Read(ref _playing);

    public static void RequestAfterIdle(string reason) => RequestAfterIdle(reason, Quiet);

    /// <summary>As <see cref="RequestAfterIdle(string)"/>, waiting <paramref name="after"/> (at least <see cref="Quiet"/>).</summary>
    public static void RequestAfterIdle(string reason, TimeSpan after)
    {
        if (after < Quiet) after = Quiet;
        int ticket;
        lock (Gate)
        {
            ticket = ++_pending;
            _reason = reason;
        }
        _ = Task.Delay(after).ContinueWith(_ =>
        {
            lock (Gate)
            {
                if (ticket != _pending) return; // a later request restarted the quiet period
            }
            Run();
        }, TaskScheduler.Default);
    }

    private static void Run()
    {
        try
        {
            // Checked when the quiet period ends, not when the trim was asked for: playback
            // may have started since.
            bool trimWorkingSet;
            lock (Gate)
            {
                trimWorkingSet = MayTrimWorkingSet(AudioPlaying, _appInBackground, Now() - _idleSince, LongIdle);
                // Owed until such a moment (SetAudioPlaying, SetAppInBackground); only hosts
                // that report their windows ever get one.
                _deferred = !trimWorkingSet && _appInBackground is not null;
            }
            if (Collector is { } custom)
            {
                custom(trimWorkingSet);
                return;
            }
            var before = GC.GetTotalMemory(false);
            string line;
            if (ShouldCollect(before))
            {
                var collected = BackgroundCollect(TimeSpan.FromSeconds(5));
                var after = GC.GetTotalMemory(false);
                line = $"trim after {_reason}: managed {before / 1048576} MB -> {after / 1048576} MB, background collection" +
                       (collected ? "" : " (still running)");
            }
            else
            {
                line = $"trim after {_reason}: managed {before / 1048576} MB, too little to collect";
            }
            if (trimWorkingSet && OperatingSystem.IsWindows())
            {
                // Checked again: the collection took a moment, and a resume may have come in.
                lock (Gate)
                    trimWorkingSet = MayTrimWorkingSet(AudioPlaying, _appInBackground, Now() - _idleSince, LongIdle);
                if (trimWorkingSet)
                {
                    var wsBefore = Environment.WorkingSet;
                    SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                    line += $", working set {wsBefore / 1048576} MB -> {Environment.WorkingSet / 1048576} MB";
                }
                else
                {
                    lock (Gate) _deferred = true;
                }
            }
            DebugLog.Write("Memory", line);
        }
        catch
        {
            // A trim is best effort; never let it surface.
        }
    }

    /// <summary>
    /// A non-blocking gen2, requested in <see cref="GCLatencyMode.SustainedLowLatency"/> and held
    /// there until it ends (or <paramref name="timeout"/>). "blocking: false" alone is only a
    /// wish: in Interactive the runtime turned 4 of 48 such requests into full blocking gen2s of
    /// 392-841 ms (harness 11-27, 575 MB fragmented heap), the size of the owner's 760 ms cut;
    /// in SustainedLowLatency 0 of 42 did (36 lone, 6 overlapping a running background GC),
    /// worst pause 11.2 ms. Returns whether the collection was seen to finish.
    /// </summary>
    internal static bool BackgroundCollect(TimeSpan timeout)
    {
        lock (Gate)
        {
            _lowLatencyHolds++;
            ApplyLatencyModeLocked();
        }
        try
        {
            var index = GC.GetGCMemoryInfo(GCKind.Background).Index;
            // No compaction, and no LOH CompactOnce: that would ride along on the next blocking
            // gen2, which is exactly the pause this avoids.
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (GC.GetGCMemoryInfo(GCKind.Background).Index == index)
            {
                if (Environment.TickCount64 > deadline) return false;
                Thread.Sleep(20);
            }
            return true;
        }
        finally
        {
            lock (Gate)
            {
                _lowLatencyHolds--;
                ApplyLatencyModeLocked();
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
