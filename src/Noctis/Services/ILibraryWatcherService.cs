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
}
