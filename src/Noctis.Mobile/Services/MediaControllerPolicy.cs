namespace Noctis.Mobile.Services;

/// <summary>
/// Which media controllers may connect to the playback session. Android's playback service is
/// exported (the media notification, lock screen and Bluetooth need it), so without a check any
/// installed app could bind to it, read the queue (file paths, desktop song ids, titles) and
/// drive the player. Allowed: the platform session's clients (Android already limits those to
/// the system, apps with MEDIA_CONTENT_CONTROL and enabled notification listeners, and they
/// carry the lock screen, Bluetooth and headset buttons), this app itself (Media3's own
/// notification controller), controllers Media3 marks trusted (the same system/listener rule),
/// and the car/Android Auto controllers Media3 recognizes. Everything else is refused.
/// </summary>
public static class MediaControllerPolicy
{
    /// <summary>Media3's ControllerInfo.LEGACY_CONTROLLER_VERSION: a platform MediaController.</summary>
    public const int PlatformControllerVersion = 0;

    public static bool Allows(int controllerVersion, int controllerUid, int ownUid, bool isTrusted, bool isKnownSystemController) =>
        controllerVersion == PlatformControllerVersion
        || controllerUid == ownUid
        || isTrusted
        || isKnownSystemController;
}
