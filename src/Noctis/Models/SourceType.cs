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
    Jellyfin = 5,
    /// <summary>A track on an audio CD in an optical drive; FilePath is a cdda:// MRL, never a file.</summary>
    AudioCd = 6
}

