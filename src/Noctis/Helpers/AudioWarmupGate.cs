namespace Noctis.Helpers;

/// <summary>
/// Startup ordering around the LibVLC warm-up (Program.Main builds the audio player on a
/// background task while Avalonia initializes).
///
/// The main window's view-model graph needs the player, so building it waits for that task.
/// Normally the task is done before Avalonia is (libvlc-warm ~130-260 ms). On the first start
/// after an update it is not: libvlc rebuilds its plugins cache (VlcPluginCache) by loading
/// every freshly written plugin DLL, and one report (Discord 11-16, 1.6.7 -> 1.6.9) logged
/// libvlc-warm-done at 38.9 s while Avalonia was ready at 10.6 s. The UI thread sat blocked in
/// that resolve the whole time, so no window existed for ~41 s and the start looked failed.
///
/// Now a small "starting" window shows when the warm-up is still running after a short grace,
/// and the main window is built once the warm-up ends, on the UI thread, exactly as before.
/// Everything after the warm-up keeps its order: the player still exists before any view
/// model, so playback, the restored queue, the keep-alive and exclusive mode see no change.
/// </summary>
internal static class AudioWarmupGate
{
    /// <summary>How long a normal start may wait for the warm-up before the starting
    /// window shows, so a fast start never flashes it.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// True when the warm-up is still running after <paramref name="grace"/>, so the starting
    /// window should show. False with no warm-up, when it finished (or failed: the main window
    /// resolve then reports the error as before), and for a start minimized to the tray, which
    /// shows no window either way.
    /// </summary>
    internal static bool ShouldShowStartingWindow(Task? warmup, bool startMinimized, TimeSpan grace)
    {
        if (warmup == null || startMinimized) return false;
        try { return !warmup.Wait(grace); }
        catch { return false; }
    }

    /// <summary>
    /// Runs <paramref name="build"/> through <paramref name="postToUi"/> once
    /// <paramref name="warmup"/> has ended, however it ended. An exception from the build goes
    /// to <paramref name="fail"/>: the UI dispatcher marks faults handled, which would leave the
    /// starting window up forever instead of the "failed to start" message.
    /// </summary>
    internal static void RunWhenWarm(Task warmup, Action<Action> postToUi, Action build, Action<Exception> fail)
    {
        warmup.ContinueWith(_ => postToUi(() =>
        {
            try { build(); }
            catch (Exception ex) { fail(ex); }
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
