using System.Diagnostics;
using Avalonia.Controls;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// A diagnostic for scroll freezes: while on, it asks for every animation frame and writes a
/// <see cref="DebugLog"/> line (logcat tag Noctis on the phone) whenever two frames are more
/// than <see cref="Threshold"/> apart, with what happened in between: managed collections per
/// generation, allocation, and whatever the app counted with <see cref="Count"/> (rows realized,
/// covers set, …). Off by default and costs nothing then; the Android head turns it on when the
/// system property debug.noctis.stalls is 1 (`adb shell setprop debug.noctis.stalls 1`, then
/// restart the app). Asking for every frame keeps the render loop awake, so it is never on in
/// normal use. (It does not keep the screen drawing: a frame where nothing changed sends nothing
/// to the display, so the panel can still drop to its idle rate under it.)
/// <para>Pages can also report what runs (<see cref="SetState"/>: the lyrics' frame loop, glide and
/// keep-alive): the 5-second summary says how long each was on, and a stall line says which ran
/// through it, so a stall while the lyrics glide (seen) tells apart from one on a still screen
/// (unseen: the panel coming back from idle).</para>
/// </summary>
public static class FrameStallMonitor
{
    /// <summary>Gap between two frames that counts as a stall.</summary>
    public static TimeSpan Threshold { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How often the summary line is written (tests shorten it).</summary>
    internal static TimeSpan SummaryEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether a monitor is running (counting is free when not).</summary>
    public static bool IsRunning => _top != null;

    private static TopLevel? _top;
    private static long _lastTicks;
    private static readonly int[] LastCollections = new int[3];
    private static long _lastAllocated;
    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static int _frames;
    private static long _windowStart;
    private static int _stalls;
    private static int _stallsInMotion;
    private static long _windowAllocated;
    private static int _windowGen0;

    /// <summary>The state that marks visible motion: a stall while it is on is one the user saw.</summary>
    public const string MotionState = "lyric glide";

    /// <summary>Starts watching the frames of <paramref name="top"/>.</summary>
    public static void Start(TopLevel top)
    {
        if (_top != null) return;
        _top = top;
        StateSince.Clear();
        WindowStateMs.Clear();
        GapStates.Clear();
        WindowCounts.Clear();
        WindowTimes.Clear();
        _stalls = _stallsInMotion = _frames = _slowFrames = 0;
        _maxGap = 0;
        Snapshot();
        _lastTicks = Stopwatch.GetTimestamp();
        _windowStart = _lastTicks;
        _windowAllocated = GC.GetTotalAllocatedBytes(precise: true);
        _windowGen0 = GC.CollectionCount(0);
        DebugLog.Write("Stalls", $"Frame stall monitor on (threshold {Threshold.TotalMilliseconds:0} ms)");
        top.RequestAnimationFrame(OnFrame);
    }

    public static void Stop() => _top = null;

    /// <summary>Counts one <paramref name="what"/> for the next stall line. UI thread only.</summary>
    public static void Count(string what)
    {
        if (_top == null) return;
        Counts[what] = Counts.TryGetValue(what, out var n) ? n + 1 : 1;
        WindowCounts[what] = WindowCounts.TryGetValue(what, out var w) ? w + 1 : 1;
    }

    private static readonly Dictionary<string, int> WindowCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> WindowTimes = new(StringComparer.Ordinal);
    private static int _slowFrames;
    private static double _maxGap;

    private static readonly Dictionary<string, double> Times = new(StringComparer.Ordinal);

    // On-states: since when each is on, its on-time in this window, and every state that was on at
    // any moment since the last frame (what a stall ran through).
    private static readonly Dictionary<string, long> StateSince = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> WindowStateMs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> GapStates = new(StringComparer.Ordinal);

    /// <summary>Marks <paramref name="what"/> on or off (a frame loop, a glide, the keep-alive) for
    /// the summary and the stall lines. UI thread only; free while the monitor is off.</summary>
    public static void SetState(string what, bool on)
    {
        if (_top == null) return;
        var now = Stopwatch.GetTimestamp();
        if (on)
        {
            StateSince.TryAdd(what, now);
            GapStates.Add(what);
        }
        else if (StateSince.Remove(what, out var since))
        {
            AddStateTime(what, since, now);
            GapStates.Add(what);
        }
    }

    private static void AddStateTime(string what, long since, long now)
    {
        var ms = Stopwatch.GetElapsedTime(since, now).TotalMilliseconds;
        WindowStateMs[what] = WindowStateMs.TryGetValue(what, out var t) ? t + ms : ms;
    }

    /// <summary>Adds <paramref name="ms"/> of UI-thread work under <paramref name="what"/> for the next stall line.</summary>
    public static void AddTime(string what, double ms)
    {
        if (_top == null) return;
        Times[what] = Times.TryGetValue(what, out var t) ? t + ms : ms;
        WindowTimes[what] = WindowTimes.TryGetValue(what, out var w) ? w + ms : ms;
    }

    private static void OnFrame(TimeSpan _)
    {
        var top = _top;
        if (top == null) return;
        var now = Stopwatch.GetTimestamp();
        var gap = Stopwatch.GetElapsedTime(_lastTicks, now);
        _lastTicks = now;
        _frames++;
        if (gap.TotalMilliseconds > 25) _slowFrames++;
        if (gap.TotalMilliseconds > _maxGap) _maxGap = gap.TotalMilliseconds;
        if (gap > Threshold)
        {
            _stalls++;
            foreach (var state in StateSince.Keys) GapStates.Add(state);
            if (GapStates.Contains(MotionState)) _stallsInMotion++;
            DebugLog.Write("Stalls", Describe(gap));
        }
        GapStates.Clear();
        Snapshot();
        // A summary every 5 s with frames, so a quiet log still says the monitor runs.
        var window = Stopwatch.GetElapsedTime(_windowStart, now);
        if (window >= SummaryEvery)
        {
            foreach (var (state, since) in StateSince.ToList())
            {
                AddStateTime(state, since, now);
                StateSince[state] = now;
            }
            // Every window, also on a quiet page: the allocation rate and collections say how
            // soon the next nursery collection (a 120-140 ms pause on the S23) comes. Precise: on
            // the phone's Mono runtime the cheap count moved only at a collection (S23 11-23:
            // 0 KB/s in every window but the one with the GC, then 3-6 MB/s), so it is read
            // precisely here, once a window, not on every frame.
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            DebugLog.Write("Stalls", $"{_stalls} stalls in {window.TotalSeconds:0.0} s ({_frames} frames)"
                + $", frames over 25 ms {_slowFrames}, longest {_maxGap:0} ms"
                + $", stalls while {MotionState} {_stallsInMotion}"
                + $", alloc {(allocated - _windowAllocated) / 1024.0 / window.TotalSeconds:0} KB/s, GC0 +{gen0 - _windowGen0}"
                + string.Concat(WindowStateMs.Select(c => $", {c.Key} on {c.Value / 1000:0.0} s"))
                + string.Concat(WindowCounts.Select(c => $", {c.Key} {c.Value}"))
                + string.Concat(WindowTimes.Select(c => $", {c.Key} {c.Value:0} ms")));
            _windowAllocated = allocated;
            _windowGen0 = gen0;
            WindowCounts.Clear();
            WindowTimes.Clear();
            WindowStateMs.Clear();
            _slowFrames = 0;
            _maxGap = 0;
            _stalls = 0;
            _stallsInMotion = 0;
            _frames = 0;
            _windowStart = now;
        }
        top.RequestAnimationFrame(OnFrame);
    }

    private static string Describe(TimeSpan gap)
    {
        var parts = new List<string> { $"stall {gap.TotalMilliseconds:0} ms" };
        var gcs = new string[3];
        var anyGc = false;
        for (var g = 0; g < 3; g++)
        {
            var d = GC.CollectionCount(g) - LastCollections[g];
            gcs[g] = d.ToString();
            anyGc |= d > 0;
        }
        parts.Add(anyGc ? $"GC0/1/2 +{gcs[0]}/+{gcs[1]}/+{gcs[2]}" : "no GC");
        var allocated = GC.GetTotalAllocatedBytes();
        // The cheap count, read every frame: on Mono it moves only at a collection, so it shows
        // the nursery's worth in a GC stall and 0 otherwise (the summary has the real rate).
        parts.Add($"alloc {(allocated - _lastAllocated) / 1024} KB");
        parts.Add($"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        if (GapStates.Count > 0) parts.Add("during " + string.Join(" + ", GapStates));
        foreach (var (what, n) in Counts.OrderByDescending(c => c.Value))
            parts.Add($"{what} {n}");
        foreach (var (what, ms) in Times.OrderByDescending(c => c.Value))
            parts.Add($"{what} {ms:0.0} ms");
        return string.Join(", ", parts);
    }

    private static void Snapshot()
    {
        for (var g = 0; g < 3; g++) LastCollections[g] = GC.CollectionCount(g);
        _lastAllocated = GC.GetTotalAllocatedBytes();
        Counts.Clear();
        Times.Clear();
    }
}
