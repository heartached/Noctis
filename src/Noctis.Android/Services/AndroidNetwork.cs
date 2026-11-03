using Android.Content;
using Android.Net;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>Whether the phone is on an unmetered network (Wi-Fi, Ethernet): what "Download on
/// Wi-Fi only" checks. Needs ACCESS_NETWORK_STATE (declared in the manifest).</summary>
public static class AndroidNetwork
{
    /// <summary>False only when the active network is known to be metered (mobile data, or a
    /// Wi-Fi the user marked metered); a lookup that fails counts as unmetered, so it never blocks.</summary>
    public static bool IsUnmetered(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager) return true;
            var capabilities = manager.GetNetworkCapabilities(manager.ActiveNetwork);
            return capabilities == null || capabilities.HasCapability(NetCapability.NotMetered);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Network lookup failed: {ex.Message}");
            return true;
        }
    }
}
