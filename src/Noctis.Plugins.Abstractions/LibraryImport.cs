namespace Noctis.Plugins;

/// <summary>
/// API 1.5, "library.import": adds audio files the plugin saved (a download, a conversion) to the
/// library, the way dropping them on the window does. Nothing is moved, renamed or retagged:
/// the files stay where the plugin put them and Noctis reads their tags. A file added this way
/// stays in the library across rescans even when it lies outside the music folders.
/// </summary>
public interface ILibraryImport
{
    /// <summary>The music folders the user chose in Settings → Library (copies, as typed there),
    /// for picking where new files go. Empty when none is set.</summary>
    IReadOnlyList<string> MusicFolders { get; }

    /// <summary>
    /// Adds <paramref name="paths"/> to the library (or refreshes them when already there) and
    /// returns copies of the library tracks for those files afterwards, in no particular order.
    /// Paths that are not absolute, do not exist, or are not a format Noctis plays are skipped
    /// (so they are missing from the result). Waits for a running library scan to finish first.
    /// Callable from any thread; at most 500 paths per call.
    /// </summary>
    Task<IReadOnlyList<TrackInfo>> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}
