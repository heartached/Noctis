namespace Noctis.Mobile.Services;

/// <summary>
/// Runs the app again for downloads from the computer that are still asked for while it is in the
/// background or ended (AndroidDownloadWakeup: a regular JobScheduler job with a network
/// constraint): songs waiting for Wi-Fi (Wi-Fi only on mobile data), or a run the process ended in
/// the middle of. Before, they went on only when the app came to the front. Unlike the
/// <see cref="IDownloadKeeper"/>'s user-initiated job it may be scheduled from the background; the
/// system runs it when the network fits, within its time limit and quota. Driven by
/// <see cref="ViewModels.DownloadActivity"/> on the UI thread; null in tests unless injected.
/// </summary>
public interface IDownloadWakeup
{
    /// <summary>Songs are still asked for: the system runs the app (<see cref="Due"/>) once a network
    /// is up, unmetered when <paramref name="wifiOnly"/>, and not before <paramref name="delay"/>.
    /// Replaces the run scheduled before; asked while a run is under way, it is scheduled when that
    /// one finishes.</summary>
    void Schedule(bool wifiOnly, TimeSpan delay);

    /// <summary>The run under way is done with (its downloads ended, or nothing to do).</summary>
    void Finish();

    /// <summary>Nothing is asked for any more (all landed, removed, signed out): no run is left.</summary>
    void Cancel();

    /// <summary>The system ran it: the songs still asked for go on, in the background if need be. A
    /// run the system started before anything listened (it started the process) is told to the
    /// first listener.</summary>
    event EventHandler? Due;

    /// <summary>The system stopped the run under way (its time limit, the network went): the
    /// downloads it held pause, and the songs left are scheduled again.</summary>
    event EventHandler? Stopped;
}
