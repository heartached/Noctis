using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Noctis.Services.MediaServer;

/// <summary>
/// Base-URL validation shared by the media-server clients, plus small id helpers.
///
/// Transport policy: https is always allowed. Plain http is allowed only when the
/// host is clearly on the local network (loopback, RFC1918/link-local addresses,
/// single-label LAN names, or mDNS-style suffixes) — the typical NAS/home-server
/// setup this feature exists for. Plain http to a public host is refused: Subsonic
/// auth tokens are offline-dictionary-attackable and Jellyfin sends the password in
/// the login body, so neither may cross the open internet unencrypted. (The stricter
/// loopback-only rule in <see cref="NavidromeMediaSourceConnector"/> predates this
/// feature; a LAN carve-out is required for real home-server use.)
/// </summary>
public static class MediaServerUrl
{
    /// <summary>
    /// Validates and normalizes a user-typed server URL. Returns null and an error
    /// category when the URL is unusable; otherwise the base URL without a trailing slash.
    /// </summary>
    public static string? TryNormalizeBase(string? input, out MediaServerError error, out string message)
    {
        error = MediaServerError.None;
        message = string.Empty;

        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = MediaServerError.InvalidUrl;
            message = "Enter the server URL.";
            return null;
        }

        // Accept bare "host:port" style input. Private/LAN hosts assume http —
        // Jellyfin's default port 8096 (and most NAS setups) speak plain http, and
        // an https attempt against it dies in the TLS handshake as a bare
        // "couldn't reach the server". Public hosts assume https, matching the
        // transport policy below.
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            var scheme = Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var probe) && IsPrivateHost(probe)
                ? "http://"
                : "https://";
            trimmed = scheme + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = MediaServerError.InvalidUrl;
            message = "The server URL must be a valid http(s) address.";
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsPrivateHost(uri))
        {
            error = MediaServerError.InsecureUrl;
            message = "Plain http is only allowed for local/private servers. Use https for remote servers.";
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>True when the host is loopback, a private/link-local address, or a LAN-style name.</summary>
    public static bool IsPrivateHost(Uri uri)
    {
        if (uri.IsLoopback) return true;

        var host = uri.Host;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return IsPrivateAddress(ip);

        // Non-IP hostnames: single-label LAN names ("mynas") and mDNS/router suffixes.
        if (!host.Contains('.')) return true;
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254); // link-local
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }

    /// <summary>
    /// Stable per-connection track id so re-browsing the same server track yields the
    /// same <see cref="Noctis.Models.Track.Id"/> (favorites/queue stay coherent within
    /// a session). Same scheme as the Navidrome connector baseline.
    /// </summary>
    public static Guid DeterministicTrackId(Guid connectionId, string serverTrackId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{serverTrackId}"));
        return new Guid(hash);
    }
}
