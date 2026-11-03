using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Noctis.Services.Server;

/// <summary>
/// The computer's addresses a phone on the home network may reach, for the pairing QR code
/// (<see cref="PairingPayload"/>): the address the PC routes the internet through first (the one
/// shown as the server address), then other up adapters' private IPv4 addresses that have a
/// gateway (a second NIC, Wi-Fi beside Ethernet). Adapters without a gateway — Hyper-V, WSL,
/// VirtualBox host networks — are left out: no phone is on them, and each would cost the phone
/// a timeout. At most <see cref="PairingPayload.MaxHosts"/>.
/// </summary>
public static class LanAddresses
{
    public static IReadOnlyList<string> ForPairing(string? primary)
    {
        var candidates = new List<(IPAddress Address, bool HasGateway)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                 && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                    candidates.Add((unicast.Address, hasGateway));
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter list: the primary address alone.
        }
        return Order(primary, candidates);
    }

    /// <summary>Pure part of <see cref="ForPairing"/>, for tests.</summary>
    internal static IReadOnlyList<string> Order(string? primary, IEnumerable<(IPAddress Address, bool HasGateway)> candidates)
    {
        var result = new List<string>();
        if (IPAddress.TryParse(primary, out var p) && Usable(p)) result.Add(p.ToString());
        foreach (var (address, hasGateway) in candidates)
        {
            if (result.Count >= PairingPayload.MaxHosts) break;
            if (!hasGateway || !Usable(address)) continue;
            var text = address.ToString();
            if (!result.Contains(text)) result.Add(text);
        }
        return result;
    }

    private static bool Usable(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)
        && ClientAddressPolicy.IsPrivate(address);
}
