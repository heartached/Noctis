namespace Noctis.Models;

/// <summary>How a Subsonic-family server accepts credentials on each request.</summary>
public enum SubsonicAuthMode
{
    /// <summary>API ≥ 1.13: <c>t=md5(password+salt)&amp;s=salt</c>. The password never travels.</summary>
    Token = 0,
    /// <summary>
    /// Legacy <c>p=enc:&lt;hex&gt;</c>. Only used when the server refuses tokens (error 41,
    /// e.g. LDAP users) or predates API 1.13 — hex is obfuscation, not encryption, which
    /// is why the transport policy still insists on https off the LAN.
    /// </summary>
    Password = 1,
}

/// <summary>
/// Connection settings for external media sources.
/// </summary>
public class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public SourceType Type { get; set; } = SourceType.Local;
    public string BaseUriOrPath { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string TokenOrPassword { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Server-side user id, for APIs that scope requests per user (Jellyfin).
    /// Subsonic-family servers leave this empty.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Subsonic REST version negotiated at connect time. A server older than the client
    /// answers error 30 to a newer <c>v=</c>, so the version we send is whatever the
    /// server said it speaks (never above <see cref="Services.MediaServer.SubsonicClient.DefaultApiVersion"/>).
    /// Jellyfin ignores this. Missing in older settings files → the default.
    /// </summary>
    public string ApiVersion { get; set; } = "1.16.1";

    /// <summary>Negotiated at connect time; see <see cref="SubsonicAuthMode"/>.</summary>
    public SubsonicAuthMode AuthMode { get; set; } = SubsonicAuthMode.Token;
}
