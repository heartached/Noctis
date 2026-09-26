using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Noctis.Services;

/// <summary>
/// Developer Mode's UI-thread stall detector. A 50 ms dispatcher timer measures its own
/// lateness, so a UI thread blocked for over 100 ms (a slow layout, a synchronous load,
/// a lock wait) logs "UI.Stall" with how long on the next tick. The timer exists only
/// while Developer Mode is on. <see cref="ReportIfSlow"/> is the attribution half: named
/// operations time themselves and log "Slow.&lt;Op&gt;" past the same 100 ms.
/// Both also go to the session log, which UI entries do not reach on their own (only
/// Playback entries are mirrored there).
/// </summary>
internal static class UiStallWatchdog
{
    private const int IntervalMs = 50;
    private const double StallMs = 100;

    private static DispatcherTimer? _timer;
    private static long _lastTick;
    private static long _lastStallLog; // Stopwatch timestamp of the last UI.Stall line
    private static int _suppressedStalls;

    /// <summary>Starts or stops the watchdog; called when Developer Mode toggles. Any thread.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetEnabled(enabled));
            return;
        }
        if (enabled)
        {
            // Desktop app only: headless tests pump the dispatcher in bursts, and every
            // burst would read as a stall.
            if (_timer != null || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
                return;
            _lastTick = 0;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        else if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var last = _lastTick;
        _lastTick = now;
        if (last == 0 || !DebugLogger.IsEnabled) return;
        // A suspended process (system sleep) shows up here as one very long stall.
        var blockedMs = (now - last) * 1000.0 / Stopwatch.Frequency - IntervalMs;
        if (blockedMs <= StallMs) return;
        // One line a second, the rest counted: every line refreshes the Developer Mode
        // log pane, and a refresh slow enough to read as a stall must not log itself
        // again (a self-feeding loop); sustained load would also flush the session ring.
        if (now - _lastStallLog < Stopwatch.Frequency)
        {
            _suppressedStalls++;
            return;
        }
        _lastStallLog = now;
        var suppressed = _suppressedStalls;
        _suppressedStalls = 0;
        Write("UI.Stall", suppressed == 0 ? $"blockedMs={blockedMs:0}" : $"blockedMs={blockedMs:0}, suppressed={suppressed}");
    }

    /// <summary>
    /// Logs "Slow.{op}" when the operation begun at <paramref name="startTimestamp"/>
    /// (<see cref="Stopwatch.GetTimestamp"/>) took over 100 ms; one clock read otherwise.
    /// </summary>
    public static void ReportIfSlow(string op, long startTimestamp, string? detail = null)
    {
        if (!DebugLogger.IsEnabled) return;
        var ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (ms <= StallMs) return;
        Write("Slow." + op, detail == null ? $"ms={ms:0}" : $"ms={ms:0}, {detail}");
    }

    /// <summary>Runs <paramref name="action"/> and reports it through <see cref="ReportIfSlow"/>.</summary>
    public static void Time(string op, Action action)
    {
        var start = Stopwatch.GetTimestamp();
        action();
        ReportIfSlow(op, start);
    }

    private static void Write(string action, string meta)
    {
        DebugLogger.Warn(DebugLogger.Category.UI, action, meta);
        DebugLog.Write("UI", $"{action} | {meta}");
    }
}
