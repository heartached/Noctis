using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Media3.Session;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// Hosts the MediaSession for the shared ExoPlayer. Media3 turns this into a foreground
/// service with the standard media notification (title, artist, artwork from the
/// MediaItem metadata, play/pause/skip) whenever the player is playing, and routes
/// lock-screen, Bluetooth and headset button events to the player. Started by
/// Media3AudioPlayer on the first Play; when Android restarts it without an engine (cold
/// start after process death, or MediaSessionService's own START_STICKY recreating it with
/// a null intent) there is no session to give, and it stops itself.
/// </summary>
[Service(Name = "com.heartached.noctis.PlaybackService", Exported = true,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
public sealed class NoctisPlaybackService : MediaSessionService
{
    private MediaSession? _session;

    public override void OnCreate()
    {
        base.OnCreate();
        // Wrap SessionPlayer (the ForwardingPlayer), not Player (the raw ExoPlayer) directly:
        // that is what routes the session's own Next/Previous through the queue instead of
        // ExoPlayer's item list. See PlaybackEngine.SessionPlayer / SessionForwardingPlayer.
        var player = PlaybackEngine.SessionPlayer;
        if (player == null)
        {
            // No engine to give a session for — either a cold start after process death, or
            // Android recreating this START_STICKY service with a null intent. OnGetSession
            // is the only other place StopSelf() lives, but a sticky restart never calls it
            // (nothing has bound or started an action yet), so without this the instance would
            // linger with _session permanently null, and a later StartService from a new
            // Media3AudioPlayer would hit this already-running instance and never build one.
            StopSelf();
            return;
        }

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

    public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
    {
        if (_session == null) StopSelf();
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
