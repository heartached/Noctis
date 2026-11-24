using Android;
using Android.App;
using Android.App.Job;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// Runs the app again for downloads from the computer still asked for (DownloadActivity): songs
/// waiting for Wi-Fi with Wi-Fi only on, and the songs of a run the process ended in the middle of.
/// Before, they went on only when the app came to the front.
///
/// A regular JobScheduler job (<see cref="DownloadWakeupJobService"/>, id 4303), not the keeper's
/// user-initiated one (4302): that one "must be scheduled while the application is visible to the
/// user" (https://developer.android.com/develop/background-work/background-tasks/uidt), this one
/// may be scheduled from the background. Its constraint is the network: Wi-Fi or Ethernet with
/// Wi-Fi only on (and for songs waiting for the computer, which is at home), else NETWORK_TYPE_ANY
/// (ACCESS_NETWORK_STATE is held, which Android 14+ requires for a network job). Not
/// NETWORK_TYPE_UNMETERED: mobile data on an unlimited plan is unmetered too; from Android 9 a
/// NetworkRequest names the transports (any of them satisfies it), before it falls back to unmetered
/// and the run checks the transport itself (AndroidNetwork.IsOnWifi). It is persisted across reboots only if the app holds RECEIVE_BOOT_COMPLETED
/// (JobInfo.Builder.setPersisted "Requires RECEIVE_BOOT_COMPLETED"); the manifest does not declare
/// it, so a reboot drops the job and the next start of the app resumes the songs instead.
///
/// When it runs, the process may have been started for it: Avalonia's Application.OnCreate
/// (AvaloniaAndroidApplication.InitializeAppLifetime → SetupWithLifetime) runs AndroidApp's whole
/// start without an activity, so the account service, the library and the shell come up, the start's
/// sync runs and DownloadActivity resumes the songs with the app off screen (ShellViewModel.IsSuspended
/// stays true until MainActivity resumes). The downloads run under this job while it runs; the
/// usual progress card is posted by the notifier (a regular job has no notification of its own).
/// The keeper is begun as well: on Android 8-11 its dataSync foreground service may start from the
/// background; on 12+ ("can't start foreground services while the app is running in the background",
/// https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start) and for
/// the 14+ user-initiated job the system refuses, which the keeper logs and survives.
///
/// A regular job runs about 10 minutes at most when the system needs the resources ("jobs will still
/// be stopped after 10 minutes if the system is busy", JobScheduler), and within the app's quota:
/// stopped, DownloadActivity pauses the downloads (finished files are kept) and schedules the job
/// again for the songs left. Scheduling the id of a running job stops it, so a schedule asked for
/// while it runs waits for its finish.
/// </summary>
internal sealed class AndroidDownloadWakeup : IDownloadWakeup
{
    internal const int JobId = 4303;

    private readonly Context _context;
    private readonly object _gate = new();
    private DownloadWakeupJobService? _job;
    private JobParameters? _jobParams;
    /// <summary>The run started before anything listened (the job started the process): the first
    /// listener is told.</summary>
    private bool _undelivered;
    /// <summary>A schedule asked for while a run was under way, made when it finishes.</summary>
    private (bool WifiOnly, TimeSpan Delay)? _after;
    private EventHandler? _due;

    /// <summary>The app's wakeup, for the job (which Android creates on its own). Null in a process
    /// without an account service: a run there finds nothing to do.</summary>
    internal static AndroidDownloadWakeup? Current { get; private set; }

    public AndroidDownloadWakeup(Context context)
    {
        _context = context;
        Current = this;
    }

    public event EventHandler? Due
    {
        add
        {
            bool deliver;
            lock (_gate)
            {
                _due += value;
                deliver = _undelivered && value != null;
                if (deliver) _undelivered = false;
            }
            if (deliver) value!.Invoke(this, EventArgs.Empty);
        }
        remove
        {
            lock (_gate) _due -= value;
        }
    }

    public event EventHandler? Stopped;

    private JobScheduler? Scheduler => _context.GetSystemService(Context.JobSchedulerService) as JobScheduler;

    public void Schedule(bool wifiOnly, TimeSpan delay)
    {
        lock (_gate)
        {
            if (_jobParams != null)
            {
                _after = (wifiOnly, delay);
                return;
            }
        }
        Try(() =>
        {
            var builder = new JobInfo.Builder(JobId, new ComponentName(_context, Java.Lang.Class.FromType(typeof(DownloadWakeupJobService))));
            if (!wifiOnly) builder.SetRequiredNetworkType(NetworkType.Any);
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                builder.SetRequiredNetwork(new NetworkRequest.Builder()
                    .AddTransportType(TransportType.Wifi)!.AddTransportType(TransportType.Ethernet)!.Build());
            else builder.SetRequiredNetworkType(NetworkType.Unmetered);
            if (delay > TimeSpan.Zero) builder.SetMinimumLatency((long)delay.TotalMilliseconds);
            // Not declared by the app (see the class): only should a library's manifest bring it.
            if (_context.CheckSelfPermission(Manifest.Permission.ReceiveBootCompleted) == Permission.Granted)
                builder.SetPersisted(true);
            var result = Scheduler?.Schedule(builder.Build()!) ?? JobScheduler.ResultFailure;
            if (result == JobScheduler.ResultSuccess) DebugLog.Write("Android", $"Download wakeup scheduled ({(wifiOnly ? "Wi-Fi" : "any network")}, after {delay.TotalMinutes:0} min)");
            else DebugLog.Write("Android", "Download wakeup not scheduled");
        }, "schedule the download wakeup");
    }

    public void Finish()
    {
        DownloadWakeupJobService? job;
        JobParameters? jobParams;
        (bool WifiOnly, TimeSpan Delay)? after;
        lock (_gate)
        {
            (job, jobParams, after) = (_job, _jobParams, _after);
            (_job, _jobParams, _after) = (null, null, null);
            _undelivered = false;
        }
        if (job != null && jobParams != null) Try(() => job.JobFinished(jobParams, false), "finish the download wakeup");
        if (after is { } next) Schedule(next.WifiOnly, next.Delay);
    }

    public void Cancel()
    {
        lock (_gate) _after = null;
        Finish();
        Try(() => Scheduler?.Cancel(JobId), "cancel the download wakeup");
    }

    /// <summary>The system ran the job (main thread): it runs until <see cref="Finish"/>.</summary>
    internal bool OnJobStarted(DownloadWakeupJobService job, JobParameters jobParams)
    {
        EventHandler? due;
        lock (_gate)
        {
            _job = job;
            _jobParams = jobParams;
            _after = null;
            due = _due;
            _undelivered = due == null;
        }
        DebugLog.Write("Android", "Download wakeup job started");
        due?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The system stopped the job (main thread). False: DownloadActivity schedules it again
    /// for the songs left (as they stop), not the system's backoff.</summary>
    internal bool OnJobStopped(JobParameters jobParams)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_jobParams, jobParams)) return false;
            _job = null;
            _jobParams = null;
            _after = null;
            _undelivered = false;
        }
        var reason = Build.VERSION.SdkInt >= BuildVersionCodes.S ? jobParams.StopReason.ToString() : "unknown";
        DebugLog.Write("Android", $"Download wakeup stopped by the system ({reason})");
        Stopped?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private static void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { DebugLog.Write("Android", $"Could not {what}: {ex.GetType().Name}"); }
    }
}

/// <summary>The regular job that runs the app again for downloads left waiting (see
/// <see cref="AndroidDownloadWakeup"/>). It holds no work of its own: the downloads run in the
/// account service while it keeps the process running, until the wakeup finishes it.</summary>
[Service(Name = "com.heartached.noctis.DownloadWakeupJobService", Exported = false, Permission = "android.permission.BIND_JOB_SERVICE")]
public sealed class DownloadWakeupJobService : JobService
{
    public override bool OnStartJob(JobParameters? jobParams) =>
        jobParams != null && AndroidDownloadWakeup.Current?.OnJobStarted(this, jobParams) == true;

    public override bool OnStopJob(JobParameters? jobParams) =>
        jobParams != null && AndroidDownloadWakeup.Current?.OnJobStopped(jobParams) == true;
}
