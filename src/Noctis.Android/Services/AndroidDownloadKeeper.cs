using Android.App;
using Android.App.Job;
using Android.Content;
using Android.Net;
using Android.OS;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// Keeps the process running while downloads from the computer run with the app in the background
/// and nothing playing (before, Android froze or ended it and the downloads stopped). The download
/// loop stays the account service's managed code; this only keeps the process alive and lends the
/// download notification (AndroidDownloadNotifier) to what keeps it:
///
/// Android 14+ (API 34): a user-initiated data transfer job (<see cref="DownloadJobService"/>,
/// JobInfo.Builder.SetUserInitiated, RUN_USER_INITIATED_JOBS), the platform's API for transfers the
/// user starts. It must be scheduled while the app is visible, which it is: downloads start from a
/// tap. Its one constraint is a network with internet; Settings › Downloads › Wi-Fi only stays the
/// account service's own check per song, so turning it on or off mid-run needs no new job. When the
/// system stops it (the network went, the phone got too hot) onStopJob pauses the downloads and asks
/// for a reschedule; the system's next run resumes them (<see cref="IDownloadKeeper.Resumed"/>).
///
/// Android 8-13: a dataSync foreground service (<see cref="DownloadForegroundService"/>), started
/// with the downloads and stopped when they end. Not the media playback service.
///
/// A process that ended mid-download (swiped away and killed, out of memory: no onStopJob then) leaves
/// no job work: a job the system runs again in a new process finds nothing wanted and finishes at
/// once, and the next start cancels any job left pending and its card (the notifier clears it).
/// The songs still asked for are the account service's (downloads.json): the next start, with the
/// app on screen, begins a new job and resumes them (DownloadActivity.ResumeUnfinishedAsync;
/// finished files are kept, partial ones deleted at start). "Make sure that your app can persist
/// the data transfer state even if onStopJob() isn't called" and "User initiated jobs must be
/// scheduled while the application is visible to the user":
/// https://developer.android.com/develop/background-work/background-tasks/uidt
/// </summary>
internal sealed class AndroidDownloadKeeper : IDownloadKeeper
{
    internal const int JobId = 4302;

    private readonly Context _context;
    private readonly AndroidDownloadNotifier _notifier;
    private readonly object _gate = new();
    /// <summary>Begun and not ended: a job or service that starts now keeps running.</summary>
    private bool _wanted;
    /// <summary>The system stopped the job mid-run: its next run resumes the downloads.</summary>
    private bool _paused;
    private DownloadJobService? _job;
    private JobParameters? _jobParams;
    private DownloadForegroundService? _service;

    /// <summary>The app's keeper, for the job and the service (which Android creates on its own).
    /// Null in a process the app's UI never started: a job run there finds nothing to keep.</summary>
    internal static AndroidDownloadKeeper? Current { get; private set; }

    public event EventHandler? Paused;
    public event EventHandler? Resumed;

    public AndroidDownloadKeeper(Context context, AndroidDownloadNotifier notifier)
    {
        _context = context;
        _notifier = notifier;
        _notifier.PostThrough = PostThroughJob;
        Current = this;
        // A job left by an ended process has nothing to keep.
        if (UsesJob) Try(() => Scheduler?.Cancel(JobId), "cancel a stale job");
    }

    /// <summary>The download notification the job and the service show.</summary>
    internal AndroidDownloadNotifier Notifier => _notifier;

    private static bool UsesJob => Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake;

    private JobScheduler? Scheduler => _context.GetSystemService(Context.JobSchedulerService) as JobScheduler;

    public void Begin(long estimatedBytes)
    {
        lock (_gate)
        {
            _wanted = true;
            if (UsesJob)
            {
                // Scheduling again replaces a job the system paused (not running): from a tap the app
                // is visible, so it may start a new one.
                if (_job != null) return;
                Try(() =>
                {
                    var network = new NetworkRequest.Builder().AddCapability(NetCapability.Internet)!.Build()!;
                    var info = new JobInfo.Builder(JobId, new ComponentName(_context, Java.Lang.Class.FromType(typeof(DownloadJobService))))
                        .SetUserInitiated(true)!
                        .SetRequiredNetwork(network)!
                        .SetEstimatedNetworkBytes(estimatedBytes > 0 ? estimatedBytes : JobInfo.NetworkBytesUnknown, 0)!
                        .Build()!;
                    var result = Scheduler?.Schedule(info) ?? JobScheduler.ResultFailure;
                    // Refused (the app not visible after all): the downloads still run while the process does.
                    if (result != JobScheduler.ResultSuccess) DebugLog.Write("Android", "Download job not scheduled");
                }, "schedule the download job");
            }
            else if (_service == null)
            {
                Try(() => _context.StartForegroundService(new Intent(_context, typeof(DownloadForegroundService))), "start the download service");
            }
        }
    }

    public void End()
    {
        DownloadJobService? job;
        JobParameters? jobParams;
        DownloadForegroundService? service;
        lock (_gate)
        {
            _wanted = false;
            _paused = false;
            (job, jobParams, service) = (_job, _jobParams, _service);
            (_job, _jobParams, _service) = (null, null, null);
        }
        if (job != null && jobParams != null) Try(() => job.JobFinished(jobParams, false), "finish the download job");
        else if (UsesJob) Try(() => Scheduler?.Cancel(JobId), "cancel the download job");
        service?.StopKeeping();
        // The job's notification stays (detached) as last posted; a card the notifier cleared or
        // finished while the job held it is put right now that nothing holds it.
        _notifier.Reassert();
    }

    /// <summary>The job started (main thread). False: nothing to keep, the job ends at once.</summary>
    internal bool OnJobStarted(DownloadJobService job, JobParameters jobParams)
    {
        bool resumed;
        lock (_gate)
        {
            if (!_wanted) return false;
            _job = job;
            _jobParams = jobParams;
            resumed = _paused;
            _paused = false;
        }
        // A user-initiated job must show its notification as soon as it starts.
        Try(() => job.SetNotification(jobParams, AndroidDownloadNotifier.NotificationId, _notifier.CurrentNotification(),
            JobEndNotificationPolicy.Detach), "show the download job's notification");
        if (resumed) Resumed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The system stopped the job (main thread): the downloads pause. True: run it again.</summary>
    internal bool OnJobStopped(JobParameters jobParams)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_jobParams, jobParams) && _jobParams != null) return false;
            _job = null;
            _jobParams = null;
            if (!_wanted) return false;
            _paused = true;
        }
        var reason = Build.VERSION.SdkInt >= BuildVersionCodes.S ? jobParams.StopReason.ToString() : "unknown";
        DebugLog.Write("Android", $"Download job stopped by the system ({reason}); downloads paused");
        Paused?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The foreground service started (main thread). False: nothing to keep, it stops.</summary>
    internal bool OnServiceStarted(DownloadForegroundService service)
    {
        lock (_gate)
        {
            if (!_wanted) return false;
            _service = service;
            return true;
        }
    }

    internal void OnServiceDestroyed(DownloadForegroundService service)
    {
        lock (_gate)
            if (ReferenceEquals(_service, service)) _service = null;
    }

    /// <summary>The notifier's posts while a job holds the card go through the job (setNotification
    /// updates it); true when one did.</summary>
    private bool PostThroughJob(Notification notification)
    {
        DownloadJobService? job;
        JobParameters? jobParams;
        lock (_gate) (job, jobParams) = (_job, _jobParams);
        if (job == null || jobParams == null) return false;
        try
        {
            job.SetNotification(jobParams, AndroidDownloadNotifier.NotificationId, notification, JobEndNotificationPolicy.Detach);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Download job notification failed: {ex.GetType().Name}");
            return false;
        }
    }

    private static void Try(Action action, string what)
    {
        // Never fail a download over what keeps it running.
        try { action(); }
        catch (Exception ex) { DebugLog.Write("Android", $"Could not {what}: {ex.GetType().Name}"); }
    }
}

/// <summary>Android 14+: the user-initiated data transfer job the downloads run under (see
/// <see cref="AndroidDownloadKeeper"/>). It holds no work of its own: it keeps the process running
/// and shows the download notification until the keeper finishes it.</summary>
[Service(Name = "com.heartached.noctis.DownloadJobService", Exported = false, Permission = "android.permission.BIND_JOB_SERVICE")]
public sealed class DownloadJobService : JobService
{
    public override bool OnStartJob(JobParameters? jobParams) =>
        jobParams != null && AndroidDownloadKeeper.Current?.OnJobStarted(this, jobParams) == true;

    public override bool OnStopJob(JobParameters? jobParams) =>
        jobParams != null && AndroidDownloadKeeper.Current?.OnJobStopped(jobParams) == true;
}

/// <summary>Android 8-13: the dataSync foreground service the downloads run under (see
/// <see cref="AndroidDownloadKeeper"/>), showing the download notification until they end. Not
/// sticky: a process that ended does not bring it back.</summary>
[Service(Name = "com.heartached.noctis.DownloadForegroundService", Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class DownloadForegroundService : Service
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var keeper = AndroidDownloadKeeper.Current;
        try
        {
            // Within seconds of StartForegroundService, even when it is to stop at once.
            var notification = keeper?.Notifier.CurrentNotification() ?? AndroidDownloadNotifier.Placeholder(this);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(AndroidDownloadNotifier.NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeDataSync);
            else
                StartForeground(AndroidDownloadNotifier.NotificationId, notification);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Download service not in the foreground: {ex.GetType().Name}");
        }
        if (keeper?.OnServiceStarted(this) != true)
        {
            // The downloads ended before it started: the placeholder goes, the notifier's card stays right.
            StopKeeping(remove: true);
            keeper?.Notifier.Reassert();
        }
        return StartCommandResult.NotSticky;
    }

    /// <summary>The downloads ended: out of the foreground, the card left as last posted (the
    /// notifier finishes or clears it), and stopped.</summary>
    internal void StopKeeping(bool remove = false)
    {
        try
        {
            StopForeground(remove ? StopForegroundFlags.Remove : StopForegroundFlags.Detach);
            StopSelf();
        }
        catch (Exception ex) { DebugLog.Write("Android", $"Download service stop failed: {ex.GetType().Name}"); }
    }

    public override void OnDestroy()
    {
        AndroidDownloadKeeper.Current?.OnServiceDestroyed(this);
        base.OnDestroy();
    }
}
