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

    public IReadOnlyList<PlayHistoryEvent> Events
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _events!.ToArray();
            }
        }
    }

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
                return;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.Load", ex.Message);
        }
        _events = new List<PlayHistoryEvent>();
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
