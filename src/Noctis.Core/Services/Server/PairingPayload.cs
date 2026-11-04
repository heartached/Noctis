using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Noctis.Services.Server;

/// <summary>
/// What the desktop's "Connect a phone" QR code carries, and the phone's strict reader of it:
/// <c>noctis-pair:1?h=192.168.1.20,10.0.0.5&amp;p=4747&amp;f=&lt;64 hex&gt;&amp;s=&lt;43 base64url&gt;</c>.
/// <list type="bullet">
/// <item><c>h</c>: the computer's LAN addresses (1–4 private IPv4 addresses, most likely first);</item>
/// <item><c>p</c>: the server's port;</item>
/// <item><c>f</c>: the full SHA-256 fingerprint of the server's TLS certificate, the pin the
/// phone checks before it sends anything;</item>
/// <item><c>s</c>: the one-time pairing secret (256 random bits, see the server's PairingCodes).</item>
/// </list>
/// Never a password, an API key or a device key. A scanned code is untrusted input: anything
/// that is not exactly this shape (another scheme, a field missing, repeated or out of order, a
/// public or malformed address, a short fingerprint or secret, extra text) is refused whole.
/// <see cref="ToString"/> leaves the secret out, so a log line cannot leak it.
/// </summary>
public sealed class PairingPayload
{
    public const string Prefix = "noctis-pair:1?";

    /// <summary>Most addresses a code carries (each one the phone may try in turn).</summary>
    public const int MaxHosts = 4;

    /// <summary>Longest payload read; a real one is under 200 characters.</summary>
    public const int MaxLength = 400;

    /// <summary>Base64url of 32 bytes, no padding.</summary>
    public const int SecretLength = 43;

    private static readonly Regex Ipv4 = new(
        @"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Port = new(@"^[1-9][0-9]{0,4}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new(@"^[0-9A-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(@"^[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant);

    private PairingPayload(IReadOnlyList<string> hosts, int port, string fingerprint, string secret)
    {
        Hosts = hosts;
        ServerPort = port;
        Fingerprint = fingerprint;
        Secret = secret;
    }

    /// <summary>Private IPv4 addresses, as dotted quads.</summary>
    public IReadOnlyList<string> Hosts { get; }

    public int ServerPort { get; }

    /// <summary>"AB:CD:…" upper case, as the desktop shows it and the phone pins it.</summary>
    public string Fingerprint { get; }

    public string Secret { get; }

    /// <summary>"https://host:port" for each address, in the code's order.</summary>
    public IEnumerable<string> ServerUrls => Hosts.Select(h => $"https://{h}:{ServerPort}");

    /// <summary>The text to put in the QR code. Throws <see cref="ArgumentException"/> on any
    /// field the reader would refuse, so the desktop never shows a code the phone cannot use.</summary>
    public static string Build(IEnumerable<string> hosts, int port, string fingerprint, string secret)
    {
        var list = hosts.Distinct(StringComparer.Ordinal).Take(MaxHosts).ToList();
        var hex = (fingerprint ?? string.Empty).Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        var text = new StringBuilder(Prefix)
            .Append("h=").Append(string.Join(',', list))
            .Append("&p=").Append(port.ToString(CultureInfo.InvariantCulture))
            .Append("&f=").Append(hex)
            .Append("&s=").Append(secret)
            .ToString();
        if (!TryParse(text, out _)) throw new ArgumentException("Pairing payload fields are not valid.");
        return text;
    }

    /// <summary>Reads a scanned code; false (and null) for anything not exactly a Noctis pairing code.</summary>
    public static bool TryParse(string? text, out PairingPayload? payload)
    {
        payload = null;
        if (text is null) return false;
        // Scanners can hand back a trailing newline; nothing else is forgiven.
        text = text.Trim(' ', '\r', '\n', '\t');
        if (text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var fields = text[Prefix.Length..].Split('&');
        if (fields.Length != 4) return false;
        if (!Field(fields[0], "h", out var h) || !Field(fields[1], "p", out var p)
            || !Field(fields[2], "f", out var f) || !Field(fields[3], "s", out var s))
            return false;

        var hosts = h.Split(',');
        if (hosts.Length is 0 or > MaxHosts || hosts.Distinct(StringComparer.Ordinal).Count() != hosts.Length) return false;
        foreach (var host in hosts)
        {
            if (!Ipv4.IsMatch(host) || !IPAddress.TryParse(host, out var ip) || !ClientAddressPolicy.IsPrivate(ip)) return false;
        }

        if (!Port.IsMatch(p) || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return false;
        if (!Hex64.IsMatch(f)) return false;
        if (!SecretPattern.IsMatch(s)) return false;

        var fingerprint = string.Join(':', f.Chunk(2).Select(c => new string(c)));
        payload = new PairingPayload(hosts, port, fingerprint, s);
        return true;
    }

    private static bool Field(string field, string name, out string value)
    {
        value = string.Empty;
        if (field.Length <= name.Length + 1 || !field.StartsWith(name + "=", StringComparison.Ordinal)) return false;
        value = field[(name.Length + 1)..];
        return true;
    }

    /// <summary>Never the secret, should this ever be written to a log or an exception message.</summary>
    public override string ToString() => $"PairingPayload {{ Hosts = {string.Join(',', Hosts)}, Port = {ServerPort} }}";
}
