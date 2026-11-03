namespace Noctis.Services.Server;

/// <summary>
/// The server certificate's SHA-256 fingerprint as both apps show it for comparing: groups of
/// four hex digits, four groups to a line ("AB12 CD34 EF56 7890"), four lines. Every digit is
/// still there, in order, so it stays an exact match; it just reads like a phone number instead
/// of a wall of colons. The desktop and the phone use this one formatter so they line up.
/// </summary>
public static class SecurityCode
{
    public static string Format(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return string.Empty;
        var hex = fingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        // Not a fingerprint (no colons, odd length): shown as it came.
        if (hex.Length % 4 != 0 || !hex.All(char.IsAsciiHexDigit)) return fingerprint;
        var groups = hex.Chunk(4).Select(g => new string(g));
        return string.Join("\n", groups.Chunk(4).Select(line => string.Join(" ", line)));
    }
}
