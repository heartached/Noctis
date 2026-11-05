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
/// normal use.
/// </summary>
public static class FrameStallMonitor
{
    /// <summary>Gap between two frames that counts as a stall.</summary>
    public static TimeSpan Threshold { get; set; } = TimeSpan.FromMilliseconds(50);

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

    /// <summary>Starts watching the frames of <paramref name="top"/>.</summary>
    public static void Start(TopLevel top)
    {
        if (_top != null) return;
        _top = top;
        Snapshot();
        _lastTicks = Stopwatch.GetTimestamp();
        _windowStart = _lastTicks;
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
            DebugLog.Write("Stalls", Describe(gap));
        }
        Snapshot();
        // A summary every 5 s with frames, so a quiet log still says the monitor runs.
        var window = Stopwatch.GetElapsedTime(_windowStart, now);
        if (window >= TimeSpan.FromSeconds(5))
        {
            if (_stalls > 0 || WindowCounts.Count > 0)
                DebugLog.Write("Stalls", $"{_stalls} stalls in {window.TotalSeconds:0.0} s ({_frames} frames)"
                    + $", frames over 25 ms {_slowFrames}, longest {_maxGap:0} ms"
                    + string.Concat(WindowCounts.Select(c => $", {c.Key} {c.Value}"))
                    + string.Concat(WindowTimes.Select(c => $", {c.Key} {c.Value:0} ms")));
            WindowCounts.Clear();
            WindowTimes.Clear();
            _slowFrames = 0;
            _maxGap = 0;
            _stalls = 0;
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
        parts.Add($"alloc {(allocated - _lastAllocated) / 1024} KB");
        parts.Add($"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
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
