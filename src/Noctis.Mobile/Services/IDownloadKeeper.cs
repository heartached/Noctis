namespace Noctis.Mobile.Services;

/// <summary>
/// Keeps the app's process running while downloads from the computer run, with the app in the
/// background and nothing playing (AndroidDownloadKeeper: a user-initiated data transfer job on
/// Android 14+, a dataSync foreground service before). It only keeps the process alive and owns the
/// download notification while it runs; the downloads themselves stay the account service's loop.
/// Driven by <see cref="ViewModels.DownloadActivity"/> on the UI thread; null in tests unless injected.
/// </summary>
public interface IDownloadKeeper
{
    /// <summary>Downloads started, from a tap (so the app is on screen, which Android 14+ requires
    /// to start such a job). <paramref name="estimatedBytes"/>: what the songs weigh, 0 = unknown.</summary>
    void Begin(long estimatedBytes);

    /// <summary>Every download ended: landed, failed, removed, stopped or signed out.</summary>
    void End();

    /// <summary>The system stopped the keeping before the downloads ended (Android 14+: the network
    /// went, the phone got too hot): the downloads pause, and the system runs the job again later.</summary>
    event EventHandler? Paused;

    /// <summary>The system ran the paused job again: the paused downloads go on.</summary>
    event EventHandler? Resumed;
}
