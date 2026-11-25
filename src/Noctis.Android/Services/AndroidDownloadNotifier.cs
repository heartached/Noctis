using Android.App;
using Android.Content;
using Android.OS;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// The downloads-from-the-computer notification: its own low-importance channel (no sound, no
/// pop-up), an ongoing card with a determinate bar while songs download, then a short "Downloaded
/// 12 songs" that dismisses itself. A tap brings the (single-task) activity back.
///
/// While downloads run, AndroidDownloadKeeper keeps the process alive under this same card (a
/// user-initiated job's or a dataSync service's notification, same id): posts go through the job
/// while one holds it (<see cref="PostThrough"/>). Should the process still end (killed for memory),
/// the card would say "3 of 12" over a download that is not moving, so each progress card times out
/// on its own after <see cref="StaleAfterMs"/> without an update, and one left by an ended process
/// is cleared at the next start (the constructor).
/// POST_NOTIFICATIONS (Android 13+) is the permission MainActivity already asks for the media
/// notification; refused, NotificationManager drops these silently and the in-app progress remains.
/// </summary>
internal sealed class AndroidDownloadNotifier : IDownloadNotifier
{
    private const string ChannelId = "downloads";
    internal const int NotificationId = 4301;
    /// <summary>Android drops a package's updates past ~5 a second, the last one included: progress
    /// is posted at most this often, the latest held back until then.</summary>
    private const long MinUpdateMs = 1000;
    private const long StaleAfterMs = 10 * 60 * 1000;
    private const long FinishedShownMs = 8 * 1000;

    private readonly Context _context;
    private readonly NotificationManager? _manager;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly object _gate = new();
    private long _lastPostAt = long.MinValue / 2;
    /// <summary>The newest progress not posted yet, and whether a delayed post for it is queued.</summary>
    private (string Title, string Text, int Done, int Total)? _pending;
    private bool _queued;
    /// <summary>Bumped by every finish or clear: a delayed progress post queued before it is dropped.</summary>
    private int _generation;
    private bool _channelMade;
    /// <summary>The newest progress card built, which the keeper's job or service shows when it starts.</summary>
    private Notification? _lastProgress;
    /// <summary>The card as it should stand: a finished card (to post again), cleared (null, and
    /// <see cref="_cleared"/>), or the progress (both unset).</summary>
    private Notification? _finished;
    private bool _cleared = true;

    /// <summary>Set by AndroidDownloadKeeper: posts a card through the job that holds it (true), else
    /// the card is posted as usual.</summary>
    internal Func<Notification, bool>? PostThrough { get; set; }

    public AndroidDownloadNotifier(Context context)
    {
        _context = context;
        _manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        Cancel();
    }

    public void ShowProgress(string title, string text, int done, int total)
    {
        lock (_gate)
        {
            _pending = (title, text, done, total);
            if (_queued) return;
            var wait = _lastPostAt + MinUpdateMs - SystemClock.ElapsedRealtime();
            if (wait <= 0)
            {
                PostPending();
                return;
            }
            _queued = true;
            var generation = _generation;
            _handler.PostDelayed(() =>
            {
                lock (_gate)
                {
                    _queued = false;
                    if (generation == _generation) PostPending();
                }
            }, wait);
        }
    }

    public void ShowFinished(string title, string text)
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
            var builder = Builder(title, text)
                .SetAutoCancel(true)
                .SetTimeoutAfter(FinishedShownMs)!;
            _finished = Post(builder);
            _cleared = false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
            _finished = null;
            _cleared = true;
            Cancel();
        }
    }

    /// <summary>The card the keeper's job or service starts with: the newest progress, else "Starting
    /// downloads…" (it starts before the first report).</summary>
    internal Notification CurrentNotification()
    {
        lock (_gate)
        {
            if (_lastProgress != null) return _lastProgress;
            var builder = Builder(Loc.T("Mobile.Download.FromComputer"), Loc.T("Mobile.Account.StartingDownloads"))
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetProgress(0, 0, true)!;
            return Decorate(builder, progress: true).Build()!;
        }
    }

    /// <summary>A bare card for a service started with no notifier around (it stops at once).</summary>
    internal static Notification Placeholder(Context context)
    {
        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager != null) EnsureChannel(manager);
        return new Notification.Builder(context, ChannelId)
            .SetContentTitle(Loc.T("Mobile.Download.FromComputer"))!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)!
            .Build()!;
    }

    /// <summary>The job or service let go of the card (it stays as last posted): a finished card is
    /// posted again and a cleared one cancelled, which the job may have kept from happening.</summary>
    internal void Reassert()
    {
        lock (_gate)
        {
            if (_finished != null) Notify(_finished);
            else if (_cleared) Cancel();
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private void PostPending()
    {
        if (_pending is not { } p) return;
        _pending = null;
        _lastPostAt = SystemClock.ElapsedRealtime();
        var builder = Builder(p.Title, p.Text)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetProgress(Math.Max(1, p.Total), Math.Clamp(p.Done, 0, Math.Max(1, p.Total)), false)!
            .SetTimeoutAfter(StaleAfterMs)!;
        _finished = null;
        _cleared = false;
        _lastProgress = Post(builder, progress: true);
    }

    private Notification.Builder Builder(string title, string text)
    {
        EnsureChannel();
        var open = PendingIntent.GetActivity(_context, NotificationId, new Intent(_context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = new Notification.Builder(_context, ChannelId)
            .SetContentTitle(title)!
            .SetContentIntent(open)!
            .SetShowWhen(false)!;
        if (!string.IsNullOrEmpty(text)) builder.SetContentText(text);
        return builder;
    }

    private static Notification.Builder Decorate(Notification.Builder builder, bool progress)
    {
        builder.SetSmallIcon(progress ? global::Android.Resource.Drawable.StatSysDownload : global::Android.Resource.Drawable.StatSysDownloadDone);
        builder.SetCategory(progress ? Notification.CategoryProgress : Notification.CategoryStatus);
        return builder;
    }

    /// <summary>Under <see cref="_gate"/>. The card built, null when building failed.</summary>
    private Notification? Post(Notification.Builder builder, bool progress = false)
    {
        try
        {
            var notification = Decorate(builder, progress).Build()!;
            Notify(notification);
            return notification;
        }
        catch (Exception ex)
        {
            // Never fail a download over its notification.
            DebugLog.Write("Android", $"Download notification failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Through the keeper's job when one holds the card, else to the manager.</summary>
    private void Notify(Notification notification)
    {
        try
        {
            if (PostThrough?.Invoke(notification) == true) return;
            _manager?.Notify(NotificationId, notification);
        }
        catch (Exception ex) { DebugLog.Write("Android", $"Download notification failed: {ex.GetType().Name}"); }
    }

    private void Cancel()
    {
        try { _manager?.Cancel(NotificationId); }
        catch (Exception ex) { DebugLog.Write("Android", $"Download notification cancel failed: {ex.GetType().Name}"); }
    }

    /// <summary>Low importance: shown in the shade without sound, vibration or a heads-up pop-up.
    /// Named in the app's language at the time it is made (Settings › Notifications shows it).</summary>
    private void EnsureChannel()
    {
        if (_channelMade || _manager == null) return;
        EnsureChannel(_manager);
        _channelMade = true;
    }

    private static void EnsureChannel(NotificationManager manager)
    {
        var channel = new NotificationChannel(ChannelId, Loc.T("Mobile.Settings.Downloads"), NotificationImportance.Low);
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }
}
