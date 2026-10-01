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
    AudioCd = 6,
    /// <summary>A song in the signed-in desktop's library (phone app). FilePath is
    /// <c>noctis-remote://tr-&lt;32 hex&gt;</c>, never a file; Id is the desktop's track Guid,
    /// so sync ids match. Library scans carry these over untouched.</summary>
    NoctisServer = 7
}

