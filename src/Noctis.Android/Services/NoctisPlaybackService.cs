using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Media3.Session;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// Hosts the MediaSession for the shared ExoPlayer. Media3 turns this into a foreground
/// service with the standard media notification (title, artist, artwork from the
/// MediaItem metadata, play/pause/skip) whenever the player is playing, and routes
/// lock-screen, Bluetooth and headset button events to the player. Started by
/// Media3AudioPlayer on the first Play, and by <see cref="NoctisMediaButtonReceiver"/> when a
/// headset or car presses Play while it is not running. The session is built as soon as the
/// engine exists (<see cref="EnsureSession"/>); a START_STICKY restart with a null intent and no
/// engine stops itself.
/// <para>The second action is the platform MediaBrowserService one, as Media3's manifest example
/// has it (https://developer.android.com/media/media3/session/background-playback, "Declare the
/// service in the manifest"): clients that look for media apps by it (Bluetooth players, legacy
/// MediaBrowserCompat apps) find Noctis, and MediaSessionService.onBind answers it with the
/// session's legacy binder. Those connections go through <see cref="ControllerGate"/> like every other.</para>
/// </summary>
[Service(Name = "com.heartached.noctis.PlaybackService", Exported = true,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "androidx.media3.session.MediaSessionService", "android.media.browse.MediaBrowserService" })]
public sealed class NoctisPlaybackService : MediaSessionService
{
    /// <summary>How long a media button start may wait for playback before the service winds
    /// itself down: Android wants startForeground() within seconds of startForegroundService()
    /// (5 s documented for Android 8), else it ends the app (ForegroundServiceDidNotStartInTimeException).</summary>
    private const int MediaButtonForegroundDeadlineMs = 4000;

    /// <summary>Media3's own shutdown notification id (MediaNotificationManager.SHUTDOWN_NOTIFICATION_ID,
    /// 1.11.0; the media notification is 1001): never shown, posted and removed at once.</summary>
    private const int ShutdownNotificationId = 20938;

    /// <summary>The channel Media3's DefaultMediaNotificationProvider posts the media notification
    /// on (DEFAULT_CHANNEL_ID), so the brief shutdown notice adds no channel of its own.</summary>
    private const string MediaChannelId = "default_channel_id";

    private MediaSession? _session;
    private Handler? _handler;
    private bool _destroyed;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureSession();
    }

    /// <summary>Builds and hands over the session once the engine exists. Lazy, not only in
    /// OnCreate: an instance created before the engine (a bind, a sticky restart) builds it on
    /// the next start or connection instead of staying sessionless for good.</summary>
    private void EnsureSession()
    {
        if (_session != null || _destroyed) return;
        // Wrap SessionPlayer (the ForwardingPlayer), not Player (the raw ExoPlayer) directly:
        // that is what routes the session's own Next/Previous through the queue instead of
        // ExoPlayer's item list. See PlaybackEngine.SessionPlayer / SessionForwardingPlayer.
        var player = PlaybackEngine.SessionPlayer;
        if (player == null) return;

        // Tapping the notification brings the (single-task) activity back.
        var launch = new Intent(this, typeof(MainActivity));
        var sessionActivity = PendingIntent.GetActivity(this, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        _session = new MediaSession.Builder(this, player)
            .SetSessionActivity(sessionActivity)
            .SetCallback(new ControllerGate())
            .Build();

        // MediaSessionService only manages the sessions it has been handed. Normally that
        // happens by itself — MediaSessionServiceStub.connect() calls onGetSession() and then
        // addSession() when a MediaController binds — but this app drives ExoPlayer directly
        // and never builds a controller, so nothing ever bound and the session stayed private
        // to us. Device run 2026-09-22: media buttons worked (they reach the session through
        // the framework) while startForegroundCount stayed 0, there was no notification, no
        // lock-screen card, and the process sat at oom_score_adj=700 while playing.
        // addSession is the public API for exactly this: it hands the session to Media3's
        // MediaNotificationManager, which connects its own controller, renders the
        // notification from the MediaItem metadata and promotes this service to the
        // foreground while the player is playing.
        AddSession(_session);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureSession();
        if (intent == null && _session == null)
        {
            // Android recreating this START_STICKY service with no engine to serve: a plain
            // (not foreground) start, so a plain stop is safe.
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        // A media button start: Media3 hands the key to the session (or, with no session from
        // OnGetSession, winds the service down safely itself: MediaSessionService.stopSelfSafely).
        var result = base.OnStartCommand(intent, flags, startId);
        if (intent?.Action == Intent.ActionMediaButton && _session != null && !IsPlaybackOngoing)
            WatchMediaButtonStart();
        return result;
    }

    /// <summary>
    /// <see cref="NoctisMediaButtonReceiver"/> started this service in the foreground for a Play
    /// key. Playback normally starts and Media3 posts the media notification, which is the
    /// startForeground() Android waits for. When nothing plays (nothing to resume, or the key was
    /// not taken) Android would end the app instead, so after <see cref="MediaButtonForegroundDeadlineMs"/>
    /// the service goes to the foreground with a hidden notice, leaves it at once and stops: what
    /// Media3 does itself when an app gives no session (MediaSessionService.stopSelfSafely, 1.11.0).
    /// </summary>
    private void WatchMediaButtonStart()
    {
        _handler ??= new Handler(Looper.MainLooper!);
        _handler.PostDelayed(() =>
        {
            if (_destroyed || IsPlaybackOngoing) return;
            var player = _session?.Player;
            // Buffering or ready to play: Media3 is about to promote it (Player.STATE_BUFFERING 2, STATE_READY 3).
            if (player != null && player.PlayWhenReady && player.MediaItemCount > 0 && player.PlaybackState is 2 or 3) return;
            DebugLog.Write("Audio", "Media button start: nothing played, service stopped");
            StopSelfSafely();
        }, MediaButtonForegroundDeadlineMs);
    }

    private void StopSelfSafely()
    {
        try
        {
            var notification = ShutdownNotification();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(ShutdownNotificationId, notification, ForegroundService.TypeMediaPlayback);
            else
                StartForeground(ShutdownNotificationId, notification);
            StopForeground(StopForegroundFlags.Remove);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Audio", $"Media button start: foreground refused: {ex.GetType().Name}");
        }
        finally
        {
            StopSelf();
        }
    }

    /// <summary>Media3's shutdown notification, rebuilt (its own is package-private): secret on the
    /// lock screen, deferred so Android 12+ does not show it in the moment it exists.</summary>
    private Notification ShutdownNotification()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager != null && manager.GetNotificationChannel(MediaChannelId) == null)
            manager.CreateNotificationChannel(new NotificationChannel(MediaChannelId,
                GetString(Resource.String.default_notification_channel_name), NotificationImportance.Low));
        var builder = new Notification.Builder(this, MediaChannelId)
            .SetSmallIcon(Resource.Drawable.media3_notification_small_icon)!
            .SetVisibility(NotificationVisibility.Secret)!
            .SetOnlyAlertOnce(true)!
            .SetOngoing(false)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Deferred);
        return builder.Build()!;
    }

    public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
    {
        // No StopSelf here: on a media button start Media3 stops the service itself when this
        // returns null, after first going to the foreground (stopSelfSafely); a plain stopSelf()
        // before that would make Android end the app. A bind with no session just fails.
        EnsureSession();
        return _session;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiped away from recents: keep playing if we are, otherwise go away with the task.
        var player = _session?.Player;
        if (player == null || !player.PlayWhenReady || player.MediaItemCount == 0)
            StopSelf();
    }

    public override void OnDestroy()
    {
        _destroyed = true;
        _handler?.RemoveCallbacksAndMessages(null);
        // Mirrors the AddSession in OnCreate: drop it before releasing it, so Media3's
        // notification manager tears its controller down against a live session.
        if (_session != null) RemoveSession(_session);
        _session?.Release();
        _session = null;
        base.OnDestroy();
    }

    /// <summary>
    /// The service is exported, so any installed app can bind to it: only the system's clients,
    /// this app and trusted controllers connect (MediaControllerPolicy). Accepted ones get exactly
    /// Media3's default (onConnectAsync's AcceptedResultBuilder(session, controller)).
    /// </summary>
    private sealed class ControllerGate : Java.Lang.Object, MediaSession.ICallback
    {
        public MediaSession.ConnectionResult? OnConnect(MediaSession? session, MediaSession.ControllerInfo? controller)
        {
            if (session == null || controller == null) return MediaSession.ConnectionResult.Reject();
            var knownSystem = session.IsMediaNotificationController(controller)
                              || session.IsAutoCompanionController(controller)
                              || session.IsAutomotiveController(controller);
            if (MediaControllerPolicy.Allows(controller.ControllerVersion, controller.Uid, global::Android.OS.Process.MyUid(),
                    controller.IsTrusted, knownSystem))
                return new MediaSession.ConnectionResult.AcceptedResultBuilder(session, controller).Build();
            DebugLog.Write("Audio", $"Media controller refused: {controller.PackageName}");
            return MediaSession.ConnectionResult.Reject();
        }
    }
}
