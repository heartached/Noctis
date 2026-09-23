using Android.Content;
using Android.Media;
using Android.Provider;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// The system output switcher: MediaRouter2's own panel on Android 14+ (API 34), the
/// Bluetooth settings page below that. Never throws into the UI.
/// </summary>
public sealed class AndroidOutputSwitcher : IOutputSwitcher
{
    private readonly Context _context;

    public AndroidOutputSwitcher(Context context) => _context = context;

    public bool Show()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34) && MediaRouter2.GetInstance(_context).ShowSystemOutputSwitcher())
                return true;
            var intent = new Intent(Settings.ActionBluetoothSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Output switcher failed: {ex.Message}");
            return false;
        }
    }
}
