namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The id shapes the phone exchanges with the desktop. A desktop song lives in the phone
/// library with FilePath <c>noctis-remote://tr-&lt;32 lowercase hex&gt;</c> (the desktop's track
/// Guid in "N" form), which is also its sync id and, with the <c>tr-</c> prefix, its server id.
/// Everything the server or a stored path hands back goes through these parsers first.
/// </summary>
public static class NoctisRemoteIds
{
    public const string PathPrefix = "noctis-remote://tr-";

    /// <summary>The FilePath of the desktop song with this id.</summary>
    public static string ToPath(Guid id) => PathPrefix + id.ToString("N");

    /// <summary>Server track id ("tr-" + hex).</summary>
    public static string ToServerTrackId(Guid id) => "tr-" + id.ToString("N");

    /// <summary>Server album id ("al-" + hex), the cover-art id.</summary>
    public static string ToServerAlbumId(Guid id) => "al-" + id.ToString("N");

    /// <summary>
    /// Strict parse of a desktop-song path: the prefix, then exactly 32 lowercase hex
    /// characters and nothing after. Anything else (upper case, dashes, "../", a query,
    /// a trailing slash) is not a desktop song.
    /// </summary>
    public static bool TryParsePath(string? filePath, out Guid id)
    {
        id = Guid.Empty;
        if (filePath is null || filePath.Length != PathPrefix.Length + 32) return false;
        if (!filePath.StartsWith(PathPrefix, StringComparison.Ordinal)) return false;
        var hex = filePath.AsSpan(PathPrefix.Length);
        return IsLowerHex32(hex) && Guid.TryParseExact(hex, "N", out id);
    }

    /// <summary>Parses a server id with the given prefix ("tr-", "al-"): prefix plus 32 hex
    /// (either case, the server writes lower). Returns false for anything else.</summary>
    public static bool TryParseServerId(string? value, string prefix, out Guid id)
    {
        id = Guid.Empty;
        if (value is null || value.Length != prefix.Length + 32) return false;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var hex = value.AsSpan(prefix.Length);
        return IsHex32(hex) && Guid.TryParseExact(hex, "N", out id) && id != Guid.Empty;
    }

    /// <summary>A sync id: 32 hex characters (either case), not the empty Guid.</summary>
    public static bool TryParseSyncId(string? value, out Guid id)
    {
        id = Guid.Empty;
        return value is { Length: 32 } && IsHex32(value) && Guid.TryParseExact(value, "N", out id) && id != Guid.Empty;
    }

    internal static bool IsLowerHex32(ReadOnlySpan<char> s)
    {
        if (s.Length != 32) return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool IsHex32(ReadOnlySpan<char> s)
    {
        if (s.Length != 32) return false;
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
