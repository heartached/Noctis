using Android.App;
using Android.Content;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// The manifest's MEDIA_BUTTON receiver: Media3's own (androidx.media3.session.MediaButtonReceiver),
/// declared as Media3's "Playback resumption" asks
/// (https://developer.android.com/media/media3/session/background-playback#resumption).
/// <list type="bullet">
/// <item>Apps that list "media apps" find the ones with a receiver for
/// android.intent.action.MEDIA_BUTTON: Samsung Sound Assistant's Favourite media app listed Signal,
/// Telegram and Samsung Music, which have one, and not Noctis, which had none (beta 6).</item>
/// <item>From Android 12 Media3 hands the platform session a media button receiver only when the
/// manifest has one (MediaSessionLegacyStub, 1.11.0): without it nothing could start Noctis again
/// from a headset or car once its session was gone.</item>
/// </list>
/// Android sends a key here only when no session is active (the service is not running). Media3
/// takes only Play / Play-Pause / headset hook from Android 8 on and starts
/// <see cref="NoctisPlaybackService"/> in the foreground with it; the service then has seconds to be
/// in the foreground, which it is once playback starts, or else it leaves safely itself
/// (<see cref="NoctisPlaybackService"/>'s media button watchdog). A process this key started runs
/// the whole app start first (Application.OnCreate, see AndroidDownloadWakeup), so the player exists.
/// </summary>
[BroadcastReceiver(Name = "com.heartached.noctis.MediaButtonReceiver", Exported = true)]
[IntentFilter(new[] { Intent.ActionMediaButton })]
public sealed class NoctisMediaButtonReceiver : AndroidX.Media3.Session.MediaButtonReceiver
{
    /// <summary>Only with the player there: a session cannot be built without it, and a foreground
    /// start with nothing to play would only have to be wound down again.</summary>
    protected override bool ShouldStartForegroundService(Context? context, Intent? intent)
    {
        if (PlaybackEngine.SessionPlayer != null) return true;
        DebugLog.Write("Audio", "Media button ignored: no player yet");
        return false;
    }

    /// <summary>Android refused the foreground start (12+, the app in the background without the
    /// media button's exemption): logged, nothing else to do.</summary>
    protected override void OnForegroundServiceStartNotAllowedException(Context? context, Intent? intent, ForegroundServiceStartNotAllowedException? e) =>
        DebugLog.Write("Audio", $"Media button could not start playback: {e?.Message}");
}
