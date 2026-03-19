namespace Noctis.Models;

/// <summary>
/// Identifies where a track originates from.
/// </summary>
public enum SourceType
{
    Local = 0,
    Smb = 1,
    WebDav = 2,
    Navidrome = 3,
    Plex = 4,
    Jellyfin = 5
}

