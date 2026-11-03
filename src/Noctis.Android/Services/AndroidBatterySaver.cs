using Android.Content;
using Android.OS;
using Noctis.Mobile.Services;

namespace Noctis.Android.Services;

/// <summary>Android's Battery Saver (PowerManager.IsPowerSaveMode), asked when the background's
/// Slow Motion starts and every few seconds while it runs.</summary>
internal sealed class AndroidBatterySaver : IBatterySaver
{
    private readonly PowerManager? _power;

    public AndroidBatterySaver(Context context) => _power = context.GetSystemService(Context.PowerService) as PowerManager;

    public bool IsOn => _power?.IsPowerSaveMode ?? false;
}
