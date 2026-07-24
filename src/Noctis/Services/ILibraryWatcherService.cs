namespace Noctis.Services;

/// <summary>
/// Watches all configured media folders and keeps the library in near-real-time
/// sync as files are added, changed, removed, or renamed on disk.
/// </summary>
public interface ILibraryWatcherService : IDisposable
{
    /// <summary>
    /// Rebuilds the set of active filesystem watchers from current settings
    /// (<c>MusicFolders</c> + <c>WatchFoldersEnabled</c>). Safe to call repeatedly;
    /// disposes any existing watchers first. A no-op when watching is disabled.
    /// </summary>
    void Refresh();

    /// <summary>
    /// Tells the watcher to ignore filesystem events for these paths for a short window,
    /// because the app itself is about to move them.
    ///
    /// Without this, a batch move that takes longer than the watcher's debounce (1.5s)
    /// gets flushed mid-run: the old paths are recorded as Deleted and RemoveTracksAsync
    /// permanently adds them to ExcludedFilePaths, so the relocate that follows finds no
    /// track to update and the user loses play counts, ratings, favorites, date-added and
    /// playlist membership for everything already moved.
    /// </summary>
    /// <param name="paths">Absolute paths that are about to change.</param>
    /// <param name="window">How long to ignore events for them.</param>
    void SuppressPaths(IEnumerable<string> paths, TimeSpan window);
}
