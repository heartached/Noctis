using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>
/// Strips credential material out of log text before it reaches a log sink.
/// Media-server stream URLs carry their auth in the query string (Subsonic
/// <c>t</c>/<c>s</c> token+salt, Jellyfin <c>api_key</c>), and LibVLC quotes the
/// full MRL in its own warning/error lines — so one failed stream open, bridged
/// into the session log and shared via Settings → "Copy Logs", would hand the
/// token to whoever reads the bug report. Scheme, host and path survive so the
/// lines stay useful for debugging; only the query string (and bare token-style
/// parameters outside URLs) are removed.
/// </summary>
public static partial class LogRedaction
{
    [GeneratedRegex("""(https?://[^\s"'<>]+?)\?[^\s"'<>]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex("""\b(api_key|apikey|access_token|token)=("[^"]*"|[^&\s"'<>]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex BareSecretRegex();

    /// <summary>Returns <paramref name="message"/> with URL query strings and
    /// token-style key=value pairs replaced by <c>[redacted]</c>. Text without
    /// URLs or token markers passes through untouched (cheap contains gate).</summary>
    public static string Scrub(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        if (message.Contains("://", StringComparison.Ordinal))
            message = UrlQueryRegex().Replace(message, "$1?[redacted]");

        if (message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("apikey", StringComparison.OrdinalIgnoreCase))
            message = BareSecretRegex().Replace(message, "$1=[redacted]");

        return message;
    }
}
