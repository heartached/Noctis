using System.Net;
using System.Net.Sockets;

namespace Noctis.Services.Server;

/// <summary>
/// Which client addresses count as "this home network" for the desktop-hosted server
/// (<see cref="NoctisServer.PrivateClientsOnly"/>): loopback, RFC 1918, link-local, IPv6
/// unique-local, and 100.64.0.0/10 (carrier-grade NAT — Tailscale hands out these, so a phone
/// on the owner's tailnet still reaches the PC). IPv4-mapped IPv6 is judged as its IPv4 form.
/// </summary>
public static class ClientAddressPolicy
{
    public static bool IsPrivate(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
}
