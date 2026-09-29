using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Reads a file that is not in the library into an <see cref="Track.IsExternal"/> track,
/// the way the drop / "Open with" path and the queue restore both need it. Blocking file
/// I/O — call off the UI thread.
/// </summary>
public static class ExternalTrackReader
{
    /// <summary>
    /// Tags from the file, with artwork extracted the way the scanner does. Null when the
    /// file is missing, unsupported or unreadable.
    /// </summary>
    public static Track? Read(IMetadataService metadata, IPersistenceService persistence, string path)
    {
        if (!File.Exists(path) ||
            !MetadataService.SupportedExtensions.Contains(Path.GetExtension(path)))
            return null;

        var track = metadata.ReadTrackMetadata(path);
        if (track == null) return null;

        // External tracks never pass through a library index rebuild, so
        // nothing populates their artwork. Extract it here the way the
        // scanner does (embedded tag, else cover file beside the track) —
        // otherwise the playback bar shows the placeholder and the Discord
        // relay has no cover to serve (GetArtworkUrl(null) → no image).
        track.IsExternal = true;
        var artPath = persistence.GetArtworkPath(track.AlbumId);
        if (!File.Exists(artPath))
        {
            var artBytes = metadata.ExtractAlbumArt(path);
            if (artBytes is { Length: > 0 })
                persistence.SaveArtwork(track.AlbumId, artBytes);
        }
        if (File.Exists(artPath))
            track.AlbumArtworkPath = artPath;
        return track;
    }
}
