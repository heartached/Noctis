using System.Net;
using System.Net.Sockets;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// Finding the computer again after its address changed (a router handing it a new one): the
/// QR code's addresses are all the phone knows, and when none of them answers the computer is
/// most likely on the same network under another number. The computer announces itself nowhere
/// (no mDNS, no broadcast), so the phone asks the other addresses of the networks it already
/// knows (the /24 around each saved private address, on the same port), the same no-credential
/// ping a first sign-in sends. Only an answer that shows the pinned certificate counts, exactly
/// as for the saved addresses: another computer, or anything else on the network, is never
/// taken and is sent nothing but that ping.
/// </summary>
public sealed partial class NoctisAccountService
{
    /// <summary>A search runs at most this often: Remote asks again every few seconds while the
    /// computer cannot be reached, and a whole network's worth of pings each time is too many.</summary>
    internal static TimeSpan NearbySearchInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long one address may take to answer the ping: on a home network the computer
    /// answers in milliseconds, and an address with nothing at it is given up on quickly.</summary>
    internal static TimeSpan NearbyProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    private const int NearbyProbesAtOnce = 32;

    private long _lastNearbySearch = long.MinValue;

    /// <summary>The first nearby address where the pinned computer answers; null when none does,
    /// when the saved addresses are not on a private network, or when a search ran too recently.</summary>
    private async Task<string?> FindNearbyAsync(NoctisAccount account, CancellationToken ct)
    {
        var candidates = NearbyAddresses(account.OtherServerUrls.Prepend(account.ServerUrl)).ToList();
        if (candidates.Count == 0) return null;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastNearbySearch);
        if (last != long.MinValue && now - last < NearbySearchInterval.TotalMilliseconds) return null;
        if (Interlocked.CompareExchange(ref _lastNearbySearch, now, last) != last) return null;   // another search just began

        var pin = PinHex(account.Fingerprint);
        using var found = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? match = null;
        DebugLog.Write("Account", $"no saved address answers; asking {candidates.Count} nearby addresses");
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = NearbyProbesAtOnce, CancellationToken = found.Token },
                async (url, token) =>
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probe.CancelAfter(NearbyProbeTimeout);
                    string seen;
                    try
                    {
                        seen = await NoctisServerClient.ProbeFingerprintAsync(_handlerFactory, url, probe.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is NoctisServerException or OperationCanceledException or HttpRequestException)
                    {
                        return;
                    }
                    if (PinHex(seen) != pin) return;
                    if (Interlocked.CompareExchange(ref match, url, null) == null) found.Cancel();
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by the match.
        }
        DebugLog.Write("Account", match is null ? "the computer is not at any nearby address" : "found the computer at a nearby address");
        return match;
    }

    /// <summary>
    /// The addresses worth asking: for each saved address on a private (or loopback) IPv4 network,
    /// the rest of its /24 on the same scheme and port, saved addresses themselves left out. A
    /// name ("studio-pc.lan"), a public address or IPv6 gives none: there is no small neighbourhood
    /// to ask, and asking outside the home network is not this phone's business.
    /// </summary>
    internal static IEnumerable<string> NearbyAddresses(IEnumerable<string> saved)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nets = new List<(string Scheme, byte A, byte B, byte C, int Port)>();
        foreach (var url in saved)
        {
            known.Add(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork) continue;
            var b = ip.GetAddressBytes();
            if (!IsLan(b)) continue;
            var net = (uri.Scheme, b[0], b[1], b[2], uri.Port);
            if (!nets.Contains(net)) nets.Add(net);
        }
        foreach (var (scheme, a, b, c, port) in nets)
            for (var d = 1; d <= 254; d++)
            {
                var url = $"{scheme}://{a}.{b}.{c}.{d}:{port}";
                if (known.Add(url)) yield return url;
            }
    }

    /// <summary>10/8, 172.16/12, 192.168/16, and 127/8 (the tests' computer).</summary>
    private static bool IsLan(byte[] b) =>
        b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
}
