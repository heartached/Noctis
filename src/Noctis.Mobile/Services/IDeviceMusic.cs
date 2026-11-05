namespace Noctis.Mobile.Services;

/// <summary>
/// "All Music on This Phone": the songs Android's media index (MediaStore.Audio) knows about,
/// found without picking folders. Android implements it over READ_MEDIA_AUDIO (13+) or
/// READ_EXTERNAL_STORAGE (8-12); hosts without one leave it null and the option is hidden.
/// </summary>
public interface IDeviceMusic
{
    /// <summary>Whether the app may read the phone's audio right now (the permission can be
    /// taken away in the system settings at any time).</summary>
    bool HasPermission { get; }

    /// <summary>Asks for the permission (the system dialog); true once granted. Resolves false
    /// at once when Android no longer shows the dialog (refused twice).</summary>
    Task<bool> RequestPermissionAsync();

    /// <summary>Opens Noctis's page in the system settings, where a refused permission is allowed.</summary>
    void OpenAppSettings();

    /// <summary>The music folders the scan also walks (SAF tree URIs). A song inside one of them
    /// is left to that folder (<see cref="DeviceMusicScan.IsCovered"/>), so nothing is listed twice.</summary>
    IReadOnlyList<string> Folders { get; set; }
}
