using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Reads audio file metadata (tags, artwork) using TagLib#.
/// </summary>
public interface IMetadataService
{
    /// <summary>
    /// Reads all metadata tags from an audio file and returns a populated Track model.
    /// Returns null if the file cannot be read or is not a supported audio format.
    /// </summary>
    Track? ReadTrackMetadata(string filePath);

    /// <summary>
    /// Extracts embedded album artwork from an audio file.
    /// Returns the raw image bytes, or null if no artwork is embedded.
    /// </summary>
    byte[]? ExtractAlbumArt(string filePath);

    /// <summary>
    /// Writes metadata tags back to the audio file.
    /// </summary>
    bool WriteTrackMetadata(Track track);

    /// <summary>
    /// Writes <paramref name="track"/>'s tags to a specific file (which may differ from
    /// the track's own path — e.g. a converted copy). When <paramref name="titleOverride"/>
    /// is set it replaces the title (e.g. "Song (WAV)").
    /// </summary>
    bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null);

    /// <summary>
    /// Sets the embedded album artwork on an audio file.
    /// Pass null to remove artwork.
    /// </summary>
    bool WriteAlbumArt(string filePath, byte[]? imageData);

    /// <summary>
    /// Reads detailed technical file information from an audio file.
    /// </summary>
    AudioFileInfo? ReadFileInfo(string filePath);
}
