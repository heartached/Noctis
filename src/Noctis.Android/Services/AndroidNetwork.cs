using Android.Content;
using Android.Net;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>What network the phone is on: Wi-Fi or Ethernet (what "Download on Wi-Fi only"
/// checks), and whether it is unmetered (animated covers). Needs ACCESS_NETWORK_STATE (declared in
/// the manifest).</summary>
public static class AndroidNetwork
{
    /// <summary>The active network is Wi-Fi or Ethernet. Not "unmetered": mobile data on an unlimited
    /// plan reports NOT_METERED, so Wi-Fi only let downloads run over it. No network: false (nothing
    /// would download anyway); a lookup that fails counts as Wi-Fi, so it never blocks.</summary>
    public static bool IsOnWifi(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager) return true;
            var capabilities = manager.GetNetworkCapabilities(manager.ActiveNetwork);
            return capabilities != null
                   && (capabilities.HasTransport(TransportType.Wifi) || capabilities.HasTransport(TransportType.Ethernet));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Network lookup failed: {ex.Message}");
            return true;
        }
    }

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
