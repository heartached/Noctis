using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Where the downloads from the computer stand, wherever they were started (an album's or a song's
/// menu, Select, Account › Download Everything): the album page's "Downloading 3 of 12" row
/// (<see cref="ProgressOf"/>), the Account page's counts when it opens mid-run (<see cref="Last"/>)
/// and the system notification (<see cref="IDownloadNotifier"/>). The service counts every running
/// batch together; each start is remembered here with its songs and a title, so a page can tell
/// its own songs apart and the notification can name the album.
/// With the app in the background and nothing playing, the process runs on under the
/// <see cref="IDownloadKeeper"/> (Android's user-initiated job / dataSync service), begun with the
/// first start and ended with the last; when the system stops it, the downloads pause and the songs
/// left go on when it runs again. Songs a run left when the app ended go on at the next start
/// (<see cref="ResumeUnfinishedAsync"/>), and songs waiting (for Wi-Fi, or a run the process ended
/// in the middle of) go on when the system runs the <see cref="IDownloadWakeup"/>, even with the app
/// in the background: it is scheduled while any song is asked for and cancelled when none is.
/// </summary>
public sealed partial class DownloadActivity : ObservableObject
{
    /// <summary>One start: its title and the desktop songs it asked for that were not downloaded yet.</summary>
    private sealed record Job(string Title, HashSet<Guid> Ids, IReadOnlyList<Track> Tracks);

    private readonly INoctisAccountService _account;
    private readonly IDownloadNotifier? _notifier;
    private readonly IDownloadKeeper? _keeper;
    private readonly IDownloadWakeup? _wakeup;
    private readonly List<Job> _jobs = new();
    /// <summary>The keeper was begun and not ended yet.</summary>
    private bool _keeping;
    /// <summary>The system stopped the keeper mid-run: each start's songs left, resumed when it runs
    /// again (or with the next tap). Null when not paused. UI thread.</summary>
    private List<(string Title, List<Track> Tracks)>? _paused;
    /// <summary>The notification shows a running download, so the next idle report ends it.</summary>
    private bool _notifying;
    /// <summary>Songs the last run left unfinished, waiting for Wi-Fi (Wi-Fi only on mobile data) or
    /// for the app to be in front; null when none wait. UI thread.</summary>
    private List<Track>? _waiting;
    /// <summary>The system's run of the wakeup is under way (<see cref="IDownloadWakeup.Due"/>, not
    /// finished): downloads it resumed with the app in the background run under it. UI thread.</summary>
    private bool _wakeRun;
    /// <summary>The keeper was begun with the app in front and not stopped by the system since: it,
    /// not a wakeup run, holds the running downloads. UI thread.</summary>
    private bool _keptInFront;
    /// <summary>Bumped by every wakeup decision: a schedule whose settings read lands after a newer
    /// decision is dropped. UI thread.</summary>
    private int _wakeupTurn;

    /// <summary>How long after downloads start the wakeup runs anyway: a process that ended mid-way
    /// (swiped away, out of memory) has its songs resumed then; a run that finds them still going in
    /// front schedules itself as far again.</summary>
    internal static readonly TimeSpan WatchDelay = TimeSpan.FromMinutes(15);

    public DownloadActivity(INoctisAccountService account, Action<Action> marshal, IDownloadNotifier? notifier, IDownloadKeeper? keeper = null,
        IDownloadWakeup? wakeup = null)
    {
        _account = account;
        _notifier = notifier;
        _keeper = keeper;
        _wakeup = wakeup;
        account.DownloadProgress += (_, progress) => marshal(() => Apply(progress));
        // The computer refused this phone's key (signed out locally): no report says so.
        account.StateChanged += (_, _) =>
        {
            if (!account.IsSignedIn && (_keeping || _notifying || _paused != null)) marshal(CallOff);
        };
        if (keeper != null)
        {
            keeper.Paused += (_, _) => marshal(() => _ = PauseAsync());
            // The system's job holds them again.
            keeper.Resumed += (_, _) => marshal(() =>
            {
                _keptInFront = true;
                ResumePaused();
            });
        }
        if (wakeup != null)
        {
            wakeup.Due += (_, _) => marshal(() => _ = WakeAsync());
            wakeup.Stopped += (_, _) => marshal(() => _ = WakeStoppedAsync());
        }
    }

    /// <summary>The wakeup ran with nothing downloading or paused (ShellViewModel): the start's own
    /// sync and resume in a process the job started, else a sync now, then the songs still asked for
    /// (<see cref="ResumeUnfinishedAsync"/>). Null: those songs are read without a sync.</summary>
    public Func<Task>? ResumeAfterSync { get; init; }

    /// <summary>Whether the app is on screen (ShellViewModel.IsSuspended); null = always. Android 14+
    /// starts the keeper's user-initiated job only while the app is visible.</summary>
    public Func<bool>? IsInFront { get; init; }

    /// <summary>The service's last report, null before the first; the Account page starts from it.</summary>
    public NoctisDownloadProgress? Last { get; private set; }

    /// <summary>Songs are downloading (any batch).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Raised on the UI thread after every report and every start or end: pages re-read <see cref="ProgressOf"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs <paramref name="download"/> (the service's DownloadAsync or DownloadAllAsync for
    /// <paramref name="tracks"/>) as one start named <paramref name="title"/>; the album page and the
    /// notification follow it until it ends.</summary>
    public Task RunAsync(string? title, IEnumerable<Track> tracks, Func<Task> download) => RunAsync(title, tracks, download, resumed: false);

    /// <param name="resumed">The system ran the paused keeper again: it is running, so not begun anew.</param>
    private async Task RunAsync(string? title, IEnumerable<Track> tracks, Func<Task> download, bool resumed)
    {
        var pending = tracks.Where(t => _account.IsRemote(t) && !_account.IsDownloaded(t)).ToList();
        var job = new Job(title?.Trim() ?? string.Empty, pending.Select(t => t.Id).ToHashSet(), pending);
        bool first;
        lock (_jobs)
        {
            first = _jobs.Count == 0;
            _jobs.Add(job);
        }
        if (!resumed)
        {
            // A tap while paused: the paused songs go on too, under a keeper begun again from here
            // (the app is on screen, which a new job needs).
            var wasPaused = _paused != null;
            if (wasPaused) ResumePaused();
            // Songs waiting for Wi-Fi go on with a tap that downloads.
            if (_waiting != null) _ = ResumeWaitingAsync();
            // Not only the first start: one after a call-off may find a stopped start still ending.
            if (!_keeping || wasPaused)
            {
                _keeping = true;
                // From the background (a wakeup run) Android may refuse it: the run holds them then.
                _keptInFront = IsInFront?.Invoke() != false;
                _keeper?.Begin(pending.Sum(t => Math.Max(0, t.FileSize)));
            }
        }
        // Should the process end mid-way, the wakeup resumes them; a run resuming them in the
        // background holds them until they end instead.
        if (first && (!_wakeRun || _keptInFront)) SetWakeup(WatchDelay);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await download();
        }
        finally
        {
            bool last;
            lock (_jobs)
            {
                _jobs.Remove(job);
                last = _jobs.Count == 0;
            }
            // Paused: the keeper waits for the system to run it again.
            if (last && _keeping && _paused == null)
            {
                _keeping = false;
                _keeper?.End();
            }
            // Songs paused or waiting are resumed by a later run; with none left, no run is.
            if (last) SetWakeup(_paused != null ? WatchDelay : _waiting != null ? TimeSpan.Zero : null);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The system stopped the keeper: the running downloads stop where they are (no song
    /// fails for it) and each start's songs left are kept for <see cref="ResumePaused"/>.</summary>
    private async Task PauseAsync()
    {
        Job[] jobs;
        lock (_jobs) jobs = _jobs.ToArray();
        var left = jobs.Select(j => (j.Title, Tracks: j.Tracks.Where(t => !_account.IsDownloaded(t)).ToList()))
            .Where(j => j.Tracks.Count > 0).ToList();
        if (left.Count == 0) return;
        // Before the downloads stop: their starts end as they do, and must not end the keeper.
        _paused = left;
        _keptInFront = false;
        try { await _account.PauseDownloadsAsync(); }
        catch (Exception ex) { DebugLog.Write("Account", $"Pausing downloads failed: {ex.GetType().Name}"); }
    }

    /// <summary>Signed out, Remove All Downloads, or the computer refused this phone: nothing is left
    /// to download, whatever state the downloads were in. The card goes and the keeper ends now;
    /// paused, nothing else ended them, and the card stood until its 10-minute stale timeout.</summary>
    private void CallOff()
    {
        _paused = null;
        _waiting = null;
        SetWakeup(null);
        if (_notifying)
        {
            _notifying = false;
            _notifier?.Clear();
        }
        // After the clear: the keeper's end puts the card right once nothing holds it.
        if (_keeping)
        {
            _keeping = false;
            _keeper?.End();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Goes on with the paused songs, each start as before (its title, the album page's row).</summary>
    private void ResumePaused()
    {
        var paused = _paused;
        _paused = null;
        if (paused == null) return;
        foreach (var (title, tracks) in paused) _ = ResumeAsync(title, tracks);
    }

    private async Task ResumeAsync(string title, List<Track> tracks)
    {
        try
        {
            await RunAsync(title, tracks, () => _account.ResumeDownloadsAsync(tracks), resumed: true);
        }
        catch (Exception ex)
        {
            // Wi-Fi only on mobile data, signed out meanwhile: the songs stay to download with a tap.
            DebugLog.Write("Account", $"Resumed downloads failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
        }
    }

    /// <summary>At start, after the sync with the computer (ShellViewModel): the songs a run left
    /// unfinished when the app ended go on by themselves, with the usual card and under the keeper,
    /// begun now that the app is on screen. On mobile data with Wi-Fi only on they wait for Wi-Fi
    /// instead of failing (<see cref="ResumeWaitingAsync"/>).</summary>
    public async Task ResumeUnfinishedAsync()
    {
        if (!_account.IsSignedIn) return;
        IReadOnlyList<Track> tracks;
        try { tracks = await _account.UnfinishedDownloadsAsync(); }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Unfinished downloads unreadable: {ex.GetType().Name}");
            return;
        }
        if (tracks.Count == 0 || !_account.IsSignedIn) return;
        DebugLog.Write("Account", $"Resuming {tracks.Count} unfinished downloads");
        _waiting = tracks.ToList();
        await ResumeWaitingAsync();
    }

    /// <summary>The app came to the front (ShellViewModel.IsSuspended), or a tap downloads: the
    /// waiting songs go on when the app is in front on Wi-Fi (or with Wi-Fi only off).</summary>
    public async Task ResumeWaitingAsync()
    {
        if (_waiting is not { } waiting) return;
        if (!_account.IsSignedIn)
        {
            _waiting = null;
            SetWakeup(null);
            return;
        }
        // In the background, a wakeup run resumes them (it may start the downloads from there).
        if (IsInFront?.Invoke() == false && !_wakeRun)
        {
            SetWakeup(TimeSpan.Zero);
            return;
        }
        bool wifiWait;
        try { wifiWait = await _account.DownloadsWaitForWifiAsync(); }
        catch (Exception) { wifiWait = false; }
        if (wifiWait)
        {
            DebugLog.Write("Account", "Unfinished downloads wait for Wi-Fi");
            // Ends a run that found mobile data, and asks for one on Wi-Fi.
            SetWakeup(TimeSpan.Zero);
            return;
        }
        // Taken meanwhile (another return to the front) or called off.
        if (!ReferenceEquals(_waiting, waiting)) return;
        _waiting = null;
        var left = waiting.Where(t => !_account.IsDownloaded(t)).ToList();
        try
        {
            await RunAsync(null, left, () => _account.ResumeDownloadsAsync(left));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Resumed downloads failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
        }
    }

    /// <summary>The system ran the wakeup (UI thread): the songs still asked for go on, in the
    /// background if need be, and the run holds them until they end. Downloads running already under
    /// the keeper begun in front need no run: it ends, and the watch is scheduled again.</summary>
    private async Task WakeAsync()
    {
        _wakeRun = true;
        DebugLog.Write("Account", "Download wakeup ran");
        try
        {
            if (!_account.IsSignedIn) { }
            else if (HasRunning())
            {
                if (!_keptInFront) return;   // resumed in the background: held until they end
            }
            else if (_paused != null)
            {
                ResumePaused();
                return;
            }
            // Read again, not the list in memory: songs removed, gone from the computer or another
            // account's since are left out (UnfinishedDownloadsAsync).
            else if (ResumeAfterSync is { } resume) await resume();
            else await ResumeUnfinishedAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Download wakeup failed: {ex.GetType().Name}");
        }
        // Holding what it started; else done (unless the downloads' end finished it already).
        if (!_wakeRun || (HasRunning() && !_keptInFront)) return;
        SetWakeup(HasRunning() || _paused != null ? WatchDelay : _waiting != null ? TimeSpan.Zero : null);
    }

    /// <summary>The system stopped the wakeup run (its time limit, the network went): downloads it
    /// held stop where they are (finished files are kept) and wait for the next run, scheduled as
    /// they end. Ones the keeper holds go on.</summary>
    private async Task WakeStoppedAsync()
    {
        if (!_wakeRun) return;
        _wakeRun = false;
        DebugLog.Write("Account", "Download wakeup stopped by the system");
        Job[] jobs;
        lock (_jobs) jobs = _jobs.ToArray();
        if (jobs.Length == 0)
        {
            SetWakeup(_paused != null ? WatchDelay : _waiting != null ? TimeSpan.Zero : null);
            return;
        }
        if (_keptInFront) return;
        var left = jobs.SelectMany(j => j.Tracks).Where(t => !_account.IsDownloaded(t)).DistinctBy(t => t.Id).ToList();
        if (left.Count == 0) return;
        // Before the downloads stop: their starts end as they do, and schedule the next run.
        _waiting = left;
        try { await _account.PauseDownloadsAsync(); }
        catch (Exception ex) { DebugLog.Write("Account", $"Pausing downloads failed: {ex.GetType().Name}"); }
    }

    private bool HasRunning()
    {
        lock (_jobs) return _jobs.Count > 0;
    }

    /// <summary>UI thread. Ends a wakeup run under way, then: songs asked for, a run after
    /// <paramref name="delay"/> on a network for them (unmetered with Wi-Fi only on); null, none.</summary>
    private void SetWakeup(TimeSpan? delay)
    {
        if (_wakeup is not { } wakeup) return;
        var turn = ++_wakeupTurn;
        if (_wakeRun)
        {
            _wakeRun = false;
            wakeup.Finish();
        }
        if (delay is not { } after || !_account.IsSignedIn)
        {
            wakeup.Cancel();
            return;
        }
        _ = ScheduleAsync();

        async Task ScheduleAsync()
        {
            bool wifiOnly;
            try { wifiOnly = await _account.DownloadsWifiOnlyAsync(); }
            catch (Exception) { wifiOnly = false; }
            if (turn != _wakeupTurn || !_account.IsSignedIn) return;
            wakeup.Schedule(wifiOnly, after);
        }
    }

    /// <summary>How far the running downloads are among <paramref name="tracks"/> (an album's): of
    /// its songs a running start asked for, how many have landed. Null when none of them is
    /// downloading, or all of them have landed while the rest of the batch runs on.</summary>
    public (int Done, int Total)? ProgressOf(IEnumerable<Track> tracks)
    {
        Job[] jobs;
        lock (_jobs) jobs = _jobs.ToArray();
        if (jobs.Length == 0) return null;
        int done = 0, total = 0;
        foreach (var track in tracks)
        {
            if (!jobs.Any(j => j.Ids.Contains(track.Id))) continue;
            total++;
            if (_account.IsDownloaded(track)) done++;
        }
        return total == 0 || done == total ? null : (done, total);
    }

    /// <summary>" · 2 missing on your computer · 1 failed", or empty: the Account page's status
    /// line and the notification say the same.</summary>
    internal static string FailureParts(NoctisDownloadProgress p)
    {
        // Songs whose file is gone from the computer are told apart: a retry cannot fetch them.
        var otherFailures = p.Failed - p.Missing;
        return (p.Missing > 0 ? " · " + Loc.T("Mobile.Account.MissingOnComputer", p.Missing) : string.Empty)
               + (otherFailures > 0 ? " · " + Loc.T("Mobile.Account.Failed", otherFailures) : string.Empty);
    }

    private void Apply(NoctisDownloadProgress progress)
    {
        if (progress.CalledOff) CallOff();
        Last = progress;
        IsActive = progress.Pending > 0;
        if (progress.Pending > 0)
        {
            _notifying = true;
            _notifier?.ShowProgress(NotificationTitle(),
                Loc.T("Mobile.Account.Downloading", progress.Completed, progress.Total, FailureParts(progress)), progress.Handled, progress.Total);
        }
        else if (_notifying && _paused == null)
        {
            _notifying = false;
            // Sign-out's last report, Remove All Downloads (counts back at zero) or a batch whose songs
            // were all removed mid-way: nothing landed and nothing failed, so nothing to tell.
            if (_notifier is { } notifier)
            {
                if (!_account.IsSignedIn || progress is { Completed: 0, Failed: 0 }) notifier.Clear();
                else notifier.ShowFinished(
                    progress.Completed > 0 ? Loc.Plural("Mobile.Download.Done", progress.Completed) : Loc.T("Mobile.Account.DownloadsFinished", string.Empty),
                    FailureParts(progress).TrimStart(' ', '·'));
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The album (or song, or list) when one start runs, else "Downloading from your computer".</summary>
    private string NotificationTitle()
    {
        lock (_jobs)
            return _jobs.Count == 1 && _jobs[0].Title.Length > 0 ? _jobs[0].Title : Loc.T("Mobile.Download.FromComputer");
    }
}
