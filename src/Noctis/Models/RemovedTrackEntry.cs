namespace Noctis.Models;

/// <summary>
/// One track removed from the library with "Keep Files": its file is still on
/// disk but excluded from scans via <see cref="AppSettings.ExcludedFilePaths"/>.
/// Shown in the Settings → Library "Removed Tracks" list so the removal can be
/// undone. The track's metadata left the library with it, so the display fields
/// are derived from the file path.
/// </summary>
public sealed class RemovedTrackEntry
{
    public RemovedTrackEntry(string filePath)
    {
        FilePath = filePath;
        Title = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrEmpty(Title)) Title = Path.GetFileName(filePath);
        Folder = Path.GetDirectoryName(filePath) ?? string.Empty;
    }

    /// <summary>Absolute path of the kept file (the ExcludedFilePaths entry).</summary>
    public string FilePath { get; }

    /// <summary>File name without extension — the best title available once the library entry is gone.</summary>
    public string Title { get; }

    /// <summary>Directory the file lives in.</summary>
    public string Folder { get; }
}
