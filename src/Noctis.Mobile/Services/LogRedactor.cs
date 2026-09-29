using System.Text.RegularExpressions;

namespace Noctis.Mobile.Services;

/// <summary>
/// Masks account secrets in log text before it leaves the app: every line mirrored to
/// logcat and the Settings → Export logs file. Covers the device key however it appears
/// (an apiKey= query value, the X-Noctis-Key header, a bare "nk_…" key), a password query
/// value (p=) and the Subsonic "enc:" hex password form. Idempotent.
/// </summary>
public static partial class LogRedactor
{
    private const string Mask = "***";

    [GeneratedRegex(@"(apiKey=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyQuery();

    [GeneratedRegex(@"(X-Noctis-Key[""']?\s*[:=,]?\s*[""']?)[^\s""',;}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyHeader();

    [GeneratedRegex(@"nk_[A-Za-z0-9_\-]+")]
    private static partial Regex DeviceKey();

    [GeneratedRegex(@"([?&]p=)[^&\s""']+")]
    private static partial Regex PasswordQuery();

    [GeneratedRegex(@"enc:[0-9a-fA-F]+")]
    private static partial Regex EncodedPassword();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        text = DeviceKey().Replace(text, "nk_" + Mask);
        text = ApiKeyQuery().Replace(text, "$1" + Mask);
        text = KeyHeader().Replace(text, m => m.Value.EndsWith(Mask, StringComparison.Ordinal) ? m.Value : m.Groups[1].Value + Mask);
        text = PasswordQuery().Replace(text, "$1" + Mask);
        text = EncodedPassword().Replace(text, "enc:" + Mask);
        return text;
    }
}
