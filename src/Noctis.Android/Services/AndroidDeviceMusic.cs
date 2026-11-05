using Android.Content;
using Android.Content.PM;
using Android.OS;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// IDeviceMusic over Android's audio permission: READ_MEDIA_AUDIO from API 33, READ_EXTERNAL_STORAGE
/// before (the manifest caps that one at 32). The scan itself is AndroidFileSystemSource's
/// MediaStore root, which reads <see cref="Folders"/> to leave a picked folder's songs to it.
/// </summary>
public sealed class AndroidDeviceMusic : IDeviceMusic
{
    private readonly Context _context;

    public AndroidDeviceMusic(Context context) => _context = context;

    /// <summary>The one instance the app made (the scan's file source asks it for the folders).</summary>
    public static AndroidDeviceMusic? Current { get; private set; }

    public static AndroidDeviceMusic Create(Context context) => Current = new AndroidDeviceMusic(context);

    internal static string Permission => Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
        ? global::Android.Manifest.Permission.ReadMediaAudio
        : global::Android.Manifest.Permission.ReadExternalStorage;

    public static bool HasPermissionIn(Context context)
    {
        try
        {
            return context.CheckSelfPermission(Permission) == global::Android.Content.PM.Permission.Granted;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Music permission check failed: {ex.Message}");
            return false;
        }
    }

    public bool HasPermission => HasPermissionIn(_context);

    public Task<bool> RequestPermissionAsync() =>
        HasPermission ? Task.FromResult(true)
        : MainActivity.Current?.RequestPermissionAsync(Permission) ?? Task.FromResult(false);

    public void OpenAppSettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.FromParts("package", _context.PackageName, null));
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Opening the app's settings failed: {ex.Message}");
        }
    }

    private IReadOnlyList<string> _folders = Array.Empty<string>();

    public IReadOnlyList<string> Folders
    {
        get => Volatile.Read(ref _folders);
        set => Volatile.Write(ref _folders, value);
    }
}
