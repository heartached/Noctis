using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Noctis.Services;
using Tmds.DBus.Protocol;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Mistery, 2026-09-22): Noctis window see-through after sleep on Wayland + NVIDIA,
/// unchanged by software rendering. The resume-time window refresh is aimed at exactly that
/// setup, with an env override for testing elsewhere.
/// </summary>
public class LinuxResumeWatcherTests
{
    [Theory]
    [InlineData(null, "wayland", true, true)]    // the reported setup
    [InlineData(null, "Wayland", true, true)]
    [InlineData(null, "x11", true, false)]       // native X11 was not reported; no flash for them
    [InlineData(null, "wayland", false, false)]  // Mesa/AMD/Intel under XWayland keep their memory
    [InlineData(null, null, true, false)]
    [InlineData("1", "x11", false, true)]        // forced on for testing
    [InlineData("0", "wayland", true, false)]    // forced off
    [InlineData("", "wayland", true, true)]      // empty override = unset
    public void ShouldRemapAfterResume_TargetsXWaylandOnNvidia_UnlessOverridden(
        string? overrideEnv, string? sessionType, bool nvidia, bool expected)
    {
        Assert.Equal(expected, LinuxResumeWatcher.ShouldRemapAfterResume(overrideEnv, sessionType, nvidia));
    }

    [Fact]
    public void TryStart_OffLinux_ReturnsNull()
    {
        if (OperatingSystem.IsLinux()) return; // the Linux path needs a system bus; covered by hand
        Assert.Null(LinuxResumeWatcher.TryStart(_ => { }));
    }

    /// <summary>
    /// A PrepareForSleep notification exactly as Tmds.DBus.Protocol builds it for the handler
    /// (DBusConnection: new Notification&lt;T&gt;(observer, value, isCompletion: ex is not null)).
    /// The constructor is internal; the observer is never touched by the members under test.
    /// </summary>
    private static Notification<bool> TmdsNotification(bool value, bool isCompletion)
    {
        var observerType = typeof(DBusConnection).Assembly.GetType("Tmds.DBus.Protocol.InnerConnection+Observer", throwOnError: true)!;
        var ctor = typeof(Notification<bool>).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            new[] { observerType, typeof(bool), typeof(bool) }, modifiers: null)!;
        return (Notification<bool>)ctor.Invoke(new[] { RuntimeHelpers.GetUninitializedObject(observerType), value, isCompletion });
    }

    [Fact]
    public void NotificationException_ThrowsOnAnOrdinarySignal_WhichIsWhy153NeverSawAWakeUp()
    {
        // The 1.5.3/1.5.4 handler did `signal.Exception == null && !signal.Value`. On every real
        // signal that throws, and Tmds disconnects the bus when a handler throws.
        var wokeUp = TmdsNotification(value: false, isCompletion: false);
        Assert.True(wokeUp.HasValue);
        Assert.Throws<InvalidOperationException>(() => wokeUp.Exception);
    }

    [Fact]
    public void ClassifySleepSignal_ReadsTheValueWithoutTouchingException()
    {
        Assert.Equal(LinuxResumeWatcher.SleepSignal.WokeUp,
            LinuxResumeWatcher.ClassifySleepSignal(TmdsNotification(value: false, isCompletion: false)));
        Assert.Equal(LinuxResumeWatcher.SleepSignal.GoingToSleep,
            LinuxResumeWatcher.ClassifySleepSignal(TmdsNotification(value: true, isCompletion: false)));
        Assert.Equal(LinuxResumeWatcher.SleepSignal.SubscriptionEnded,
            LinuxResumeWatcher.ClassifySleepSignal(TmdsNotification(value: false, isCompletion: true)));
        Assert.Equal(LinuxResumeWatcher.SleepSignal.None,
            LinuxResumeWatcher.ClassifySleepSignal(default));
    }

    [Theory]
    [InlineData(5.0, 5.0, false)]      // awake: both clocks moved together
    [InlineData(5.2, 5.0, false)]      // timer jitter
    [InlineData(14.9, 5.0, false)]     // 9.9s lead: under the threshold
    [InlineData(15.0, 5.0, true)]      // 10s lead: at the threshold
    [InlineData(3605.0, 5.0, true)]    // an hour asleep
    [InlineData(5.0, 3605.0, false)]   // wall clock set back: never a suspend
    public void LooksLikeSuspend_NeedsTheWallClockWellAheadOfTheMonotonicClock(
        double wallSeconds, double monotonicSeconds, bool expected)
    {
        Assert.Equal(expected, LinuxResumeWatcher.LooksLikeSuspend(
            TimeSpan.FromSeconds(wallSeconds), TimeSpan.FromSeconds(monotonicSeconds),
            LinuxResumeWatcher.SuspendGapThreshold));
    }

    [Theory]
    [InlineData(null, false)]   // nothing handled since the last going-to-sleep
    [InlineData(0.5, true)]     // login1, then the clock check on its next tick
    [InlineData(6.0, true)]
    [InlineData(19.9, true)]
    [InlineData(20.0, false)]   // a later wake-up of its own
    [InlineData(600.0, false)]
    public void IsSameWakeUp_CollapsesBothSourcesIntoOneRefresh(double? sinceSeconds, bool expected)
    {
        TimeSpan? since = sinceSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Assert.Equal(expected, LinuxResumeWatcher.IsSameWakeUp(since, LinuxResumeWatcher.SameWakeUpWindow));
    }

    [Theory]
    [InlineData(true, WindowState.Normal, SizeToContent.Manual, "NudgeSize")]
    [InlineData(true, WindowState.Maximized, SizeToContent.Manual, "Remaximize")]   // KWin refuses client resizes
    [InlineData(true, WindowState.Minimized, SizeToContent.Manual, "LeaveMinimized")]
    [InlineData(true, WindowState.FullScreen, SizeToContent.Manual, "LeaveFullScreen")]
    [InlineData(false, WindowState.Normal, SizeToContent.Manual, "LeaveHidden")]    // tray-hidden
    [InlineData(false, WindowState.Maximized, SizeToContent.Manual, "LeaveHidden")]
    [InlineData(true, WindowState.Normal, SizeToContent.WidthAndHeight, "LeaveAutoSized")]
    [InlineData(true, WindowState.Normal, SizeToContent.Height, "LeaveAutoSized")]
    public void ChooseWindowRefresh_HandlesEveryWindowStateDeliberately(
        bool isVisible, WindowState state, SizeToContent sizeToContent, string expected)
    {
        Assert.Equal(expected, LinuxResumeWatcher.ChooseWindowRefresh(isVisible, state, sizeToContent).ToString());
    }

    [Fact]
    public void RefreshPasses_ComeInOrder_AndEachHoldEndsBeforeTheNextPass()
    {
        var delays = LinuxResumeWatcher.RefreshPassDelays;
        Assert.True(delays.Length >= 2);
        Assert.True(delays[0] > TimeSpan.Zero);
        for (var i = 1; i < delays.Length; i++)
            Assert.True(delays[i] - delays[i - 1] > LinuxResumeWatcher.RefreshHold);
    }
}
