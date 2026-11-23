using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Where the downloads from the computer stand, wherever they were started (an album's or a song's
/// menu, Select, Account › Download Everything): the album page's "Downloading 3 of 12" row
/// (<see cref="ProgressOf"/>), the Account page's counts when it opens mid-run (<see cref="Last"/>)
/// and the system notification (<see cref="IDownloadNotifier"/>). The service counts every running
/// batch together; each start is remembered here with its songs and a title, so a page can tell
/// its own songs apart and the notification can name the album.
/// </summary>
public sealed partial class DownloadActivity : ObservableObject
{
    /// <summary>One start: its title and the desktop songs it asked for that were not downloaded yet.</summary>
    private sealed record Job(string Title, HashSet<Guid> Ids);

    private readonly INoctisAccountService _account;
    private readonly IDownloadNotifier? _notifier;
    private readonly List<Job> _jobs = new();
    /// <summary>The notification shows a running download, so the next idle report ends it.</summary>
    private bool _notifying;

    public DownloadActivity(INoctisAccountService account, Action<Action> marshal, IDownloadNotifier? notifier)
    {
        _account = account;
        _notifier = notifier;
        account.DownloadProgress += (_, progress) => marshal(() => Apply(progress));
    }

    /// <summary>The service's last report, null before the first; the Account page starts from it.</summary>
    public NoctisDownloadProgress? Last { get; private set; }

    /// <summary>Songs are downloading (any batch).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Raised on the UI thread after every report and every start or end: pages re-read <see cref="ProgressOf"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs <paramref name="download"/> (the service's DownloadAsync or DownloadAllAsync for
    /// <paramref name="tracks"/>) as one start named <paramref name="title"/>; the album page and the
    /// notification follow it until it ends.</summary>
    public async Task RunAsync(string? title, IEnumerable<Track> tracks, Func<Task> download)
    {
        var ids = tracks.Where(t => _account.IsRemote(t) && !_account.IsDownloaded(t)).Select(t => t.Id).ToHashSet();
        var job = new Job(title?.Trim() ?? string.Empty, ids);
        lock (_jobs) _jobs.Add(job);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await download();
        }
        finally
        {
            lock (_jobs) _jobs.Remove(job);
            Changed?.Invoke(this, EventArgs.Empty);
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
        Last = progress;
        IsActive = progress.Pending > 0;
        if (progress.Pending > 0)
        {
            _notifying = true;
            _notifier?.ShowProgress(NotificationTitle(),
                Loc.T("Mobile.Account.Downloading", progress.Completed, progress.Total, FailureParts(progress)), progress.Handled, progress.Total);
        }
        else if (_notifying)
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
