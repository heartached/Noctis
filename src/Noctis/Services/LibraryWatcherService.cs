using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Keeps the library in near-real-time sync with on-disk changes under the
/// configured media folders. Bursts of filesystem events are coalesced over a
/// short quiet period (see <see cref="WatchDebouncer"/>) and then applied off
/// the UI thread via <see cref="ILibraryService.ImportFilesAsync"/> /
/// <see cref="ILibraryService.RemoveTracksAsync"/>.
/// </summary>
public sealed class LibraryWatcherService : ILibraryWatcherService
{
    // Quiet period after the last raw event before a batch is flushed — long
    // enough to coalesce a folder copy / multi-file save, short enough to feel
    // near-real-time.
    private const int DebounceMs = 1500;

    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settingsAccessor;

    private readonly object _gate = new();
    private readonly WatchDebouncer _debouncer = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private System.Threading.Timer? _flushTimer;
    private bool _disposed;

    public LibraryWatcherService(ILibraryService library, Func<AppSettings> settingsAccessor)
    {
        _library = library;
        _settingsAccessor = settingsAccessor;
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Refresh()
    {
        if (_disposed) return;

        AppSettings settings;
        try { settings = _settingsAccessor() ?? new AppSettings(); }
        catch { return; }

        lock (_gate)
        {
            if (_disposed) return;
            DisposeWatchers();

            if (!settings.WatchFoldersEnabled) return;

            foreach (var folder in settings.MusicFolders ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    var w = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024
                    };
                    w.Created += OnChanged;
                    w.Changed += OnChanged;
                    w.Deleted += OnDeleted;
                    w.Renamed += OnRenamed;
                    w.Error += OnError;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch
                {
                    // A folder we can't watch (permissions, unmounted, too many
                    // handles) is skipped; the rest still get watched.
                }
            }
        }
    }

    private static bool IsAudio(string path)
        => MetadataService.SupportedExtensions.Contains(Path.GetExtension(path));

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsAudio(e.FullPath)) return;
        Record(() => _debouncer.Record(e.FullPath, FileChangeKind.CreatedOrChanged));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsAudio(e.FullPath)) return;
        Record(() => _debouncer.Record(e.FullPath, FileChangeKind.Deleted));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Either side may carry a non-audio name (e.g. a download's ".part" → ".mp3").
        var oldAudio = IsAudio(e.OldFullPath);
        var newAudio = IsAudio(e.FullPath);
        if (!oldAudio && !newAudio) return;
        Record(() => _debouncer.RecordRename(oldAudio ? e.OldFullPath : null,
                                             newAudio ? e.FullPath : null));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or the watched tree became inaccessible — rebuild the
        // watcher set. Any events lost in the overflow are reconciled by the next
        // explicit/startup scan.
        DebugLogger.Error(DebugLogger.Category.Error, "LibraryWatcher",
            $"watcher error: {e.GetException()?.Message}");
        Refresh();
    }

    private void Record(Action record)
    {
        lock (_gate)
        {
            if (_disposed) return;
            record();
            _flushTimer?.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        WatchBatch batch;
        lock (_gate)
        {
            if (_disposed) return;
            batch = _debouncer.Drain();
        }
        if (batch.IsEmpty) return;

        // The timer callback already runs off the UI thread; apply asynchronously
        // so we never block the timer thread on library I/O.
        _ = ApplyBatchAsync(batch);
    }

    private async Task ApplyBatchAsync(WatchBatch batch)
    {
        // Serialize batches so overlapping flushes don't mutate the library concurrently.
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (batch.ToImport.Count > 0)
                await _library.ImportFilesAsync(batch.ToImport).ConfigureAwait(false);

            if (batch.ToRemove.Count > 0)
            {
                var removeSet = new HashSet<string>(batch.ToRemove, StringComparer.OrdinalIgnoreCase);
                var ids = _library.Tracks
                    .Where(t => removeSet.Contains(t.FilePath))
                    .Select(t => t.Id)
                    .ToList();
                if (ids.Count > 0)
                    await _library.RemoveTracksAsync(ids).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "LibraryWatcher",
                $"batch apply failed: {ex.Message}");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* already gone */ }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeWatchers();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }
}
