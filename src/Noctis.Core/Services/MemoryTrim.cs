using System.Runtime;

namespace Noctis.Services;

/// <summary>
/// Gives the managed heap back after a burst of large garbage.
///
/// The startup pipeline (library parse, tag reads during the scan, artwork
/// backfill) leaves hundreds of MB of dead Large Object Heap arrays behind —
/// measured at 236 MB of unrooted byte[] on a 7k-track library, most of them
/// embedded cover files TagLib had materialised. Nothing allocates afterwards, so
/// the GC has no reason to run a gen2 and the memory stays committed for the whole
/// session: that is the "Noctis sits at 500+ MB doing nothing" report. A single
/// compacting full collection once the burst is over returns it.
///
/// Requests are debounced: every producer (startup, a scan, a backfill) calls
/// <see cref="RequestAfterIdle"/> when it finishes, and the collection runs once
/// <see cref="Quiet"/> has passed since the last request, so overlapping producers
/// pay for one collection, after all of them are done.
/// </summary>
public static class MemoryTrim
{
    /// <summary>How long after the last request the trim runs.</summary>
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Set by the app: true while music plays. A blocking compacting gen2 suspends
    /// every managed thread for its whole pause, the audio render thread included
    /// (a trim after the startup scan put a 34 ms GC inside a 39 ms render gap), so
    /// while this is true the trim runs as a background collection instead.
    /// </summary>
    public static Func<bool>? IsAudioPlaying { get; set; }

    /// <summary>Test seam: what runs when the quiet period elapses, given whether it may block. Null = the real collection.</summary>
    internal static Action<bool>? Collector { get; set; }

    private static readonly object Gate = new();
    private static int _pending;
    private static string _reason = "";
    private static bool _playing;
    private static bool _deferred;
    private static GCLatencyMode _restoreMode = GCLatencyMode.Interactive;
    private static bool? _appInBackground; // null: the host does not report it (server, phone)
    private static long _idleSince = Environment.TickCount64;

    /// <summary>
    /// How long playback must have been stopped or paused before a blocking compacting trim
    /// may run. Play pressed during that collection waits for it: a headless harness (575 MB
    /// heap, resume landing inside the trim) measured up to 68.7 ms from resume to the next
    /// render with the blocking trim (pauses 59-99 ms) against 15.3 ms with a background one
    /// (pauses 0.1 ms; the rest is the 10 ms render period).
    /// </summary>
    public static TimeSpan LongIdle { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Test seam: the clock idle time is measured on (milliseconds).</summary>
    internal static Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// Set by the desktop app: true while nothing of it is on screen (main window hidden or
    /// minimized and no mini player). Once reported, a blocking compacting trim only runs
    /// then, see <see cref="MayBlock"/>; a play button on screen never waits behind one.
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
    /// Whether the trim may be a blocking compacting collection. Never while music plays (the
    /// render thread would freeze with it). Where the host reports its windows (the desktop),
    /// only with the whole app in the background and playback idle for <see cref="LongIdle"/>:
    /// a resume pressed while a blocking trim runs waits for it to end. Otherwise it is a
    /// background collection, which frees the dead objects without the pause; the compaction
    /// (which also gives the emptied regions back to the OS) stays owed until such a moment.
    /// Hosts that never report (server, phone) keep the old rule: block whenever not playing.
    /// Pure; internal for tests.
    /// </summary>
    internal static bool MayBlock(bool playing, bool? appInBackground, long idleMs, TimeSpan longIdle) =>
        !playing && (appInBackground is not { } background || (background && idleMs >= longIdle.TotalMilliseconds));

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
    /// comes back, and a compacting trim skipped while playing runs then.
    /// </summary>
    public static void SetAudioPlaying(bool playing)
    {
        var runDeferred = false;
        lock (Gate)
        {
            if (_playing == playing) return;
            _playing = playing;
            try
            {
                if (playing)
                {
                    var current = GCSettings.LatencyMode;
                    if (current != GCLatencyMode.SustainedLowLatency) _restoreMode = current;
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                }
                else
                {
                    GCSettings.LatencyMode = _restoreMode;
                }
            }
            catch
            {
                // Best effort: a runtime without concurrent GC keeps its mode.
            }
            if (!playing)
            {
                _idleSince = Now();
                runDeferred = _deferred;
            }
        }
        // Not right away: the compacting trim needs playback idle for LongIdle, and is checked
        // again when it fires (a resume in between keeps it a background collection). Quiet on
        // top so the timer never lands a clock tick short of LongIdle.
        if (runDeferred) RequestAfterIdle("playback stopped (deferred trim)", LongIdle + Quiet);
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
            var playing = AudioPlaying;
            bool blocking;
            lock (Gate)
            {
                blocking = MayBlock(playing, _appInBackground, Now() - _idleSince, LongIdle);
                // The compacting trim waits for its moment (SetAudioPlaying, SetAppInBackground).
                _deferred = !blocking;
            }
            if (Collector is { } custom)
            {
                custom(blocking);
                return;
            }
            var before = GC.GetTotalMemory(false);
            if (!blocking)
            {
                // Background gen2: dead objects are freed while the render thread keeps
                // running (only short suspensions). No compaction, and no CompactOnce, which
                // would otherwise ride along on the next blocking gen2 during playback.
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
                DebugLog.Write("Memory",
                    $"trim after {_reason}: managed {before / 1048576} MB, background collection " +
                    $"({(playing ? "audio playing" : "app on screen or playback idle under " + LongIdle.TotalSeconds + " s")}; compacting trim deferred)");
                return;
            }
            // Aggressive, not Forced + LOH CompactOnce: with regions (.NET 7+) a Forced
            // compacting gen2 frees the objects but keeps the emptied regions committed, so
            // the process kept its footprint (11-03, Debug, 4.4k-track library: GC committed
            // 68 -> 70 MB, private bytes 365 -> 366 MB, 60 ms pause). Aggressive also
            // decommits the free regions (committed 66 -> 51 MB, private 365 -> 349 MB) for
            // the same pause (58 ms). It compacts the LOH too, so CompactOnce is not needed.
            var committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var after = GC.GetTotalMemory(false);
            var committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
            DebugLog.Write("Memory",
                $"trim after {_reason}: managed {before / 1048576} MB -> {after / 1048576} MB, " +
                $"committed {committedBefore / 1048576} MB -> {committedAfter / 1048576} MB");
        }
        catch
        {
            // A trim is best effort; never let it surface.
        }
    }
}
