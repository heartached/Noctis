namespace Noctis.Mobile.Services;

/// <summary>
/// Which player requests may carry the desktop's device key: https to the signed-in desktop's
/// own host and port, for its stream endpoint only. Anything else the player is ever handed —
/// another host, another port, cleartext, another API path (the key would authorise it) — is
/// fetched without the key.
/// </summary>
public static class StreamAuthScope
{
    /// <summary>The header the desktop reads the device key from.</summary>
    public const string HeaderName = "X-Noctis-Key";

    public static bool Allows(string? requestUrl, string? serverUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            return false;
        return request.Scheme == Uri.UriSchemeHttps
            && server.Scheme == Uri.UriSchemeHttps
            && request.UserInfo.Length == 0
            && string.Equals(request.Host, server.Host, StringComparison.OrdinalIgnoreCase)
            && request.Port == server.Port
            && request.AbsolutePath is "/rest/stream" or "/rest/stream.view";
    }
}
