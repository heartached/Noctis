using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// JSON-file-backed play event log under the Noctis data directory.
/// Recording is cheap and thread-safe; disk writes are debounced and
/// run on the thread pool so playback paths never block on I/O.
/// </summary>
public sealed class PlayHistoryService : IPlayHistoryService
{
    private const int MaxEvents = 10_000;
    private const int SaveDebounceMs = 3_000;

    private readonly object _lock = new();
    private readonly string _filePath;
    private List<PlayHistoryEvent>? _events;
    private Timer? _saveDebounce;

    public PlayHistoryService()
    {
        _filePath = Path.Combine(AppPaths.DataRoot, "play_history.json");
    }

    /// <summary>
    /// Immutable snapshot of the event log, swapped on write rather than copied on read.
    ///
    /// The getter used to call EnsureLoaded (a synchronous ReadAllText + deserialize of
    /// up to 10,000 events) and then allocate a fresh 10,000-element array via ToArray —
    /// on every access. Every caller is a UI-thread path: HomeViewModel on each debounced
    /// LibraryUpdated, StatisticsViewModel, SettingsViewModel and WrapViewModel. First
    /// access blocked the UI on disk I/O and each later one allocated a full copy.
    /// </summary>
    private volatile IReadOnlyList<PlayHistoryEvent> _snapshot = Array.Empty<PlayHistoryEvent>();

    public IReadOnlyList<PlayHistoryEvent> Events
    {
        get
        {
            // Fast path: already loaded, no lock, no copy.
            if (_loaded) return _snapshot;

            lock (_lock)
            {
                EnsureLoaded();
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Loads the log off the UI thread. Call once at startup so the first Events access
    /// doesn't pay for the read.
    /// </summary>
    public Task PreloadAsync() => Task.Run(() =>
    {
        try
        {
            lock (_lock) { EnsureLoaded(); }
        }
        catch { /* a missing/corrupt log is handled by EnsureLoaded */ }
    });

    private volatile bool _loaded;

    /// <summary>Rebuilds the published snapshot. Must be called under _lock after a mutation.</summary>
    private void PublishSnapshot() => _snapshot = _events!.ToArray();

    public void RecordPlay(Track track)
    {
        lock (_lock)
        {
            EnsureLoaded();
            _events!.Add(new PlayHistoryEvent
            {
                TrackId = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                PlayedAtUtc = DateTime.UtcNow,
                Skipped = false
            });

            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);

            PublishSnapshot();
            ScheduleSave();
        }
    }

    public void RecordSkip(Track track)
    {
        lock (_lock)
        {
            EnsureLoaded();
            // The play event was added when the track started, so it sits at
            // (or very near) the tail. Scan a short window from the end.
            var floor = Math.Max(0, _events!.Count - 25);
            for (var i = _events.Count - 1; i >= floor; i--)
            {
                if (_events[i].TrackId == track.Id)
                {
                    _events[i].Skipped = true;
                    PublishSnapshot();
                    ScheduleSave();
                    return;
                }
            }
        }
    }

    public Task FlushAsync()
    {
        lock (_lock)
        {
            _saveDebounce?.Dispose();
            _saveDebounce = null;
            if (_events == null)
                return Task.CompletedTask;
        }
        return Task.Run(Save);
    }

    private void EnsureLoaded()
    {
        if (_events != null) return;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _events = JsonSerializer.Deserialize<List<PlayHistoryEvent>>(json) ?? new List<PlayHistoryEvent>();
                PublishSnapshot();
                _loaded = true;
                return;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.Load", ex.Message);
        }
        _events = new List<PlayHistoryEvent>();
        PublishSnapshot();
        _loaded = true;
    }

    private void ScheduleSave()
    {
        // Called under _lock. Debounce so rapid track changes coalesce into one write.
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ => Save(), null, SaveDebounceMs, Timeout.Infinite);
    }

    // Serializes concurrent Save() calls (debounce timer vs FlushAsync — Dispose
    // doesn't stop an already-running callback), which otherwise race on the
    // shared ".tmp" opened exclusively and drop one write.
    private readonly object _saveGate = new();

    private void Save()
    {
        try
        {
            PlayHistoryEvent[] snapshot;
            lock (_lock)
            {
                if (_events == null) return;
                snapshot = _events.ToArray();
            }

            lock (_saveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, _filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.Save", ex.Message);
        }
    }
}
