using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Tmds.DBus.Protocol;

namespace Noctis.Services;

/// <summary>
/// Linux only: notices that the machine woke from sleep and has the window layer refresh
/// its windows.
///
/// Discord (Mistery, 2026-09-22/23): on a Wayland session with the NVIDIA driver the Noctis
/// window came back from sleep see-through and stayed that way until the app was restarted;
/// NOCTIS_SOFTWARE_RENDER=1 changed nothing, so this is not only a lost GL context. Without
/// NVreg_PreserveVideoMemoryAllocations the driver keeps only essential video memory across a
/// suspend ("rendering corruption", NVIDIA README, Power Management chapter), and the buffers
/// behind our X11 window under XWayland live there, while a fresh window (a restart) gets fresh
/// buffers. The driver-side setting remains the real fix.
///
/// 1.5.3/1.5.4 shipped a watcher that never refreshed anything. Its handler read
/// Notification.Exception, which Tmds.DBus.Protocol throws from on every ordinary signal (it
/// is only valid on completions), and a throwing handler makes Tmds disconnect the bus — so
/// the PrepareForSleep(true) sent BEFORE the suspend took the subscription down and the
/// wake-up was never seen. Its log lines went to DebugLogger's UI category only, which never
/// reaches the session log a user can send, so nothing showed it either.
///
/// Wake-ups now come from two sources: login1's PrepareForSleep(false) on the system bus, and
/// a D-Bus-independent clock check — the wall clock (CLOCK_REALTIME) keeps counting through a
/// suspend while the monotonic one behind Stopwatch (CLOCK_MONOTONIC) stops, so a tick that
/// finds the wall clock far ahead means the machine was asleep. One wake-up triggers one
/// refresh sequence of two passes: nothing orders login1's signal after the compositor's and
/// the driver's own resume work, so the second pass catches a first one that came too early.
/// Every step is written to the session log under [Resume].
/// </summary>
public sealed class LinuxResumeWatcher : IDisposable
{
    private const string Login1Bus = "org.freedesktop.login1";
    private const string Login1Path = "/org/freedesktop/login1";
    private const string Login1Manager = "org.freedesktop.login1.Manager";
    private const string PrepareForSleep = "PrepareForSleep";

    /// <summary>How often the wall clock is compared with the monotonic clock.</summary>
    private static readonly TimeSpan ClockCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Wall-clock lead over the monotonic clock that counts as a suspend. Well above
    /// timer jitter; an NTP step this large is rare and only costs one extra refresh.</summary>
    internal static readonly TimeSpan SuspendGapThreshold = TimeSpan.FromSeconds(10);

    /// <summary>login1 and the clock check both report the same wake-up, a few seconds apart
    /// (awake time — the monotonic clock does not count the suspend itself).</summary>
    internal static readonly TimeSpan SameWakeUpWindow = TimeSpan.FromSeconds(20);

    /// <summary>When the refresh passes run, measured from the wake-up.</summary>
    internal static readonly TimeSpan[] RefreshPassDelays = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15) };

    /// <summary>How long a window is held at the nudged size / restored state before it is put back.</summary>
    internal static readonly TimeSpan RefreshHold = TimeSpan.FromMilliseconds(500);

    internal enum SleepSignal { None, GoingToSleep, WokeUp, SubscriptionEnded }

    internal enum WindowRefresh { LeaveHidden, LeaveMinimized, LeaveFullScreen, LeaveAutoSized, NudgeSize, Remaximize }

    private readonly Action<int> _refreshWindows;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _clockCheck;
    private DateTime _lastWallClock;
    private long _lastTimestamp;
    private long _lastWakeUpTimestamp; // 0 = none since the last PrepareForSleep(true)
    private string? _lastWakeUpSource;
    private DBusConnection? _connection;
    private IDisposable? _match;
    private volatile bool _disposed;

    private LinuxResumeWatcher(Action<int> refreshWindows)
    {
        _refreshWindows = refreshWindows;
        _lastWallClock = DateTime.UtcNow;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _clockCheck = new Timer(_ => CheckClocks(), null, ClockCheckInterval, ClockCheckInterval);
        _ = Task.Run(InitializeAsync);
    }

    /// <summary>
    /// Starts watching on the setups that need it (see <see cref="ShouldRemapAfterResume"/>);
    /// null everywhere else. <paramref name="refreshWindows"/> gets the pass number (1-based)
    /// on a background thread. Never throws — a missing system bus only costs the login1
    /// source, the clock check still runs.
    /// </summary>
    public static LinuxResumeWatcher? TryStart(Action<int> refreshWindows)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return null;
            var overrideEnv = Environment.GetEnvironmentVariable("NOCTIS_RESUME_REMAP");
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            var nvidia = File.Exists("/proc/driver/nvidia/version");
            var setup = $"XDG_SESSION_TYPE={sessionType ?? "(unset)"}, NVIDIA driver {(nvidia ? "loaded" : "not loaded")}, " +
                        $"NOCTIS_RESUME_REMAP={overrideEnv ?? "(unset)"}, " +
                        $"NOCTIS_SOFTWARE_RENDER={Environment.GetEnvironmentVariable("NOCTIS_SOFTWARE_RENDER") ?? "(unset)"}";
            if (!ShouldRemapAfterResume(overrideEnv, sessionType, nvidia))
            {
                Log("ResumeWatch.Off", $"{setup}; windows are not refreshed after sleep (NOCTIS_RESUME_REMAP=1 forces it)");
                return null;
            }
            Log("ResumeWatch.On", setup);
            return new LinuxResumeWatcher(refreshWindows);
        }
        catch (Exception ex)
        {
            Warn("ResumeWatch.Start", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The refresh is targeted at the reported setup — an X11 client under XWayland on the
    /// proprietary NVIDIA driver — because it briefly resizes (or un- and re-maximizes)
    /// windows, which nobody else should pay for.
    /// NOCTIS_RESUME_REMAP=1 forces it on for testing other setups; =0 turns it off.
    /// Pure; internal for tests.
    /// </summary>
    internal static bool ShouldRemapAfterResume(string? overrideEnv, string? sessionType, bool nvidiaDriverPresent)
    {
        if (overrideEnv == "1") return true;
        if (overrideEnv == "0") return false;
        return nvidiaDriverPresent && string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a PrepareForSleep notification means. Reads IsCompletion/HasValue before anything
    /// else: Notification.Exception throws unless IsCompletion, and Value throws unless
    /// HasValue — the 1.5.3/1.5.4 handler read Exception first and died on the first signal.
    /// Pure; internal for tests.
    /// </summary>
    internal static SleepSignal ClassifySleepSignal(Notification<bool> signal)
    {
        if (signal.IsCompletion) return SleepSignal.SubscriptionEnded;
        if (!signal.HasValue) return SleepSignal.None;
        // PrepareForSleep(b start): true right before suspend, false right after resume.
        return signal.Value ? SleepSignal.GoingToSleep : SleepSignal.WokeUp;
    }

    /// <summary>
    /// True when the wall clock ran at least <paramref name="threshold"/> further than the
    /// monotonic clock over the same interval — the monotonic clock stood still, so the
    /// machine was suspended. A wall clock set back (negative lead) never counts.
    /// Pure; internal for tests.
    /// </summary>
    internal static bool LooksLikeSuspend(TimeSpan wallElapsed, TimeSpan monotonicElapsed, TimeSpan threshold)
        => wallElapsed - monotonicElapsed >= threshold;

    /// <summary>
    /// True when a wake-up report belongs to one already handled (login1 and the clock check
    /// both see every wake-up). <paramref name="sinceLastWakeUp"/> is null when nothing was
    /// handled since the last going-to-sleep signal. Pure; internal for tests.
    /// </summary>
    internal static bool IsSameWakeUp(TimeSpan? sinceLastWakeUp, TimeSpan window)
        => sinceLastWakeUp is { } since && since < window;

    /// <summary>
    /// How a window is refreshed after a wake-up. A real size change is the refresh: it gives
    /// a redirected window a new backing pixmap in the X server (compReallocPixmap), and it is
    /// what makes Avalonia rebuild its render layer and redraw everything. KWin refuses
    /// client resizes of maximized windows, so those are restored and re-maximized. Hidden,
    /// minimized and F11 fullscreen windows are left alone, and so are windows that size to
    /// their content (setting Height on them would pin it). Pure; internal for tests.
    /// </summary>
    internal static WindowRefresh ChooseWindowRefresh(bool isVisible, WindowState state, SizeToContent sizeToContent)
    {
        if (!isVisible) return WindowRefresh.LeaveHidden;
        return state switch
        {
            WindowState.Minimized => WindowRefresh.LeaveMinimized,
            WindowState.FullScreen => WindowRefresh.LeaveFullScreen,
            WindowState.Maximized => WindowRefresh.Remaximize,
            _ => sizeToContent == SizeToContent.Manual ? WindowRefresh.NudgeSize : WindowRefresh.LeaveAutoSized,
        };
    }

    // DebugLogger is off unless the debug panel is on, and only Playback reaches "Copy Logs";
    // these lines go to the session log too, or a user's log shows nothing (1.5.3/1.5.4 did).
    internal static void Log(string action, string message)
    {
        DebugLogger.Info(DebugLogger.Category.UI, action, message);
        DebugLog.Write("Resume", $"{action}: {message}");
    }

    internal static void Warn(string action, string message)
    {
        DebugLogger.Warn(DebugLogger.Category.UI, action, message);
        DebugLog.Write("Resume", $"Warn: {action}: {message}");
    }

    private async Task InitializeAsync()
    {
        try
        {
            var address = DBusAddress.System;
            if (string.IsNullOrEmpty(address))
            {
                Warn("ResumeWatch.NoBus", "no system bus address; relying on the clock check alone");
                return;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync();

            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = Login1Bus,
                Path = Login1Path,
                Interface = Login1Manager,
                Member = PrepareForSleep,
            };
            // Completion flags: a dropped system bus is logged instead of silently ending the
            // subscription (the clock check keeps covering wake-ups either way).
            var match = await connection.AddMatchAsync(
                rule,
                static (Message message, object? _) => message.GetBodyReader().ReadBool(),
                (Notification<bool> signal) => OnSleepSignal(signal),
                emitOnCapturedContext: false,
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed);

            if (_disposed)
            {
                match.Dispose();
                connection.Dispose();
                return;
            }

            _connection = connection;
            _match = match;
            Log("ResumeWatch.Started", $"{Login1Manager}.{PrepareForSleep} on {address}, plus the clock check");
        }
        catch (Exception ex)
        {
            Warn("ResumeWatch.Init", $"{ex.Message}; relying on the clock check alone");
        }
    }

    // Runs on the D-Bus reader. Tmds disconnects the whole bus when a handler throws, so
    // nothing may escape from here.
    private void OnSleepSignal(Notification<bool> signal)
    {
        try
        {
            switch (ClassifySleepSignal(signal))
            {
                case SleepSignal.GoingToSleep:
                    lock (_gate)
                    {
                        _lastWakeUpTimestamp = 0;
                        _lastWakeUpSource = null;
                    }
                    Log("ResumeWatch.Sleeping", "login1 PrepareForSleep(true)");
                    break;
                case SleepSignal.WokeUp:
                    OnWokeUp("login1", "login1 PrepareForSleep(false)");
                    break;
                case SleepSignal.SubscriptionEnded:
                    if (!_disposed)
                        Warn("ResumeWatch.BusLost", $"{signal.Exception?.Message}; relying on the clock check alone");
                    break;
            }
        }
        catch (Exception ex)
        {
            Warn("ResumeWatch.Handler", ex.Message);
        }
    }

    private void CheckClocks()
    {
        try
        {
            TimeSpan lead;
            lock (_gate)
            {
                var wallClock = DateTime.UtcNow;
                var timestamp = Stopwatch.GetTimestamp();
                var wallElapsed = wallClock - _lastWallClock;
                var monotonicElapsed = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
                _lastWallClock = wallClock;
                _lastTimestamp = timestamp;
                if (!LooksLikeSuspend(wallElapsed, monotonicElapsed, SuspendGapThreshold))
                    return;
                lead = wallElapsed - monotonicElapsed;
            }
            OnWokeUp("clock check", $"wall clock ran {lead.TotalSeconds:0}s ahead of the monotonic clock");
        }
        catch (Exception ex)
        {
            Warn("ResumeWatch.Clock", ex.Message);
        }
    }

    private void OnWokeUp(string source, string detail)
    {
        if (_disposed) return;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            TimeSpan? since = _lastWakeUpTimestamp == 0 ? null : Stopwatch.GetElapsedTime(_lastWakeUpTimestamp, now);
            if (IsSameWakeUp(since, SameWakeUpWindow))
            {
                Log("ResumeWatch.Duplicate", $"{detail}: same wake-up as {_lastWakeUpSource} {since.GetValueOrDefault().TotalSeconds:0}s ago");
                return;
            }
            _lastWakeUpTimestamp = now;
            _lastWakeUpSource = source;
        }
        Log("ResumeWatch.Resumed",
            $"{detail}; refreshing visible windows after {RefreshPassDelays[0].TotalSeconds:0}s and {RefreshPassDelays[^1].TotalSeconds:0}s");
        _ = RunRefreshPassesAsync(_cts.Token);
    }

    private async Task RunRefreshPassesAsync(CancellationToken token)
    {
        try
        {
            var waited = TimeSpan.Zero;
            for (var i = 0; i < RefreshPassDelays.Length; i++)
            {
                await Task.Delay(RefreshPassDelays[i] - waited, token).ConfigureAwait(false);
                waited = RefreshPassDelays[i];
                _refreshWindows(i + 1);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Warn("ResumeWatch.Handler", ex.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _cts.Cancel(); } catch { /* best effort on shutdown */ }
        try { _clockCheck.Dispose(); } catch { /* best effort on shutdown */ }
        try { _match?.Dispose(); } catch { /* best effort on shutdown */ }
        try { _connection?.Dispose(); } catch { /* best effort on shutdown */ }
        _match = null;
        _connection = null;
    }
}
