namespace Noctis.Models;

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
}

