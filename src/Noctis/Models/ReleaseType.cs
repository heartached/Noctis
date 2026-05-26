namespace Noctis.Models;

/// <summary>
/// Album release classification, populated from RELEASETYPE / MUSICBRAINZ_ALBUM_TYPE
/// tags, the iTunes "- Single" / "- EP" naming convention, or a track-count fallback.
/// </summary>
public enum ReleaseType
{
    Album,
    Single,
    EP,
    Compilation,
    Live,
    Remix,
    Soundtrack,
    Other,
}
