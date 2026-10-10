using System.Runtime.InteropServices;
using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// JSON-file-backed play event log under the Noctis data directory.
/// Recording is cheap and thread-safe; disk writes are debounced and
/// run on the thread pool so playback paths never block on I/O.
///
/// On disk: play_history.json (a JSON array of events, the format every version has
/// written) plus, once the log is large, play_history.recent.json holding only the
/// events logged since play_history.json was last rewritten. A save then rewrites that
/// small tail instead of the whole log; the base file is rewritten ("compacted") every
/// <see cref="CompactAfterTailEvents"/> plays, or when an event already in it changes.
/// </summary>
public sealed class PlayHistoryService : IPlayHistoryService
{
    /// <summary>
    /// Events kept, oldest trimmed first. Was 10,000: the Statistics page, the Settings
    /// Statistics tab and the Wrap all read this log, and the owner's profiles logged
    /// 8,470 (dev) and 4,800 (main) events between June and 10-09 2026, so the current
    /// year's first months were weeks from being trimmed. At the measured ~44 plays a day
    /// 250,000 is about 15 years (a 40 MB file, ~0.2 s to load off the UI thread).
    /// </summary>
    internal const int MaxEvents = 250_000;

    /// <summary>
    /// Up to this many events a save rewrites the whole file, as before (~3 MB, a few ms),
    /// so small logs stay a single play_history.json. Past it a save writes only the tail.
    /// </summary>
    internal const int FullRewriteMaxEvents = 20_000;

    /// <summary>Tail size at which the next save folds the tail into play_history.json.</summary>
    internal const int CompactAfterTailEvents = 2_000;

    private const int SaveDebounceMs = 3_000;
    private const string TailFileName = "play_history.recent.json";

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly string _tailPath;
    private readonly int _maxEvents;
    private Timer? _saveDebounce;

    // The live log is _buffer[_start .. _start + _count). Slots inside a published snapshot
    // are never written again: appends go past the end, trimming only advances _start, and
    // a full buffer is replaced by a new one (older snapshots keep the old array). That is
    // what lets RecordPlay publish a new snapshot without copying the log.
    private PlayHistoryEvent[]? _buffer;
    private int _start;
    private int _count;

    // Events appended since load, and how many of those play_history.json already holds.
    // The difference is the tail.
    private long _appended;
    private long _baseAppended;

    // Bumped when an event already written to play_history.json changes (a skip, a remap)
    // or the file failed to parse; the next save then rewrites it.
    private int _baseDirtyVersion;
    private int _baseCleanVersion;

    // Which play_history.json the tail on disk extends: its event count and the time of its
    // last event (PlayedAtUtc is never edited). A tail left behind by a compaction that
    // crashed before deleting it no longer matches, so it is not applied twice.
    private int _baseFileCount;
    private long _baseFileLastTicks;

    public PlayHistoryService(string? filePath = null) : this(filePath, MaxEvents) { }

    internal PlayHistoryService(string? filePath, int maxEvents)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.DataRoot, "play_history.json");
        _tailPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? string.Empty, TailFileName);
        _maxEvents = Math.Max(1, maxEvents);
    }

    /// <summary>
    /// Immutable snapshot of the event log, swapped on write rather than copied on read.
    ///
    /// The getter used to call EnsureLoaded (a synchronous ReadAllText + deserialize of
    /// the whole log) and then allocate a fresh full-size array via ToArray —
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

    /// <summary>
    /// Publishes the live window. Must be called under _lock after an append. It used to
    /// copy the whole log (ToArray) on every play: 2 MB of large-object heap per track at
    /// 250,000 events.
    /// </summary>
    private void PublishSnapshot() => _snapshot = new ArraySegment<PlayHistoryEvent>(_buffer!, _start, _count);

    private int TailCount => (int)Math.Min(_appended - _baseAppended, _count);

    public void RecordPlay(Track track)
    {
        lock (_lock)
        {
            EnsureLoaded();
            Append(new PlayHistoryEvent
            {
                TrackId = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                PlayedAtUtc = DateTime.UtcNow,
                Skipped = false
            });
            _appended++;

            PublishSnapshot();
            ScheduleSave();
        }
    }

    /// <summary>Adds one event, trimming the oldest at the cap. Under _lock.</summary>
    private void Append(PlayHistoryEvent e)
    {
        if (_count >= _maxEvents)
        {
            // Trim by moving the window; the slot stays as it was for older snapshots.
            _start++;
            _count--;
        }
        if (_start + _count == _buffer!.Length)
        {
            var next = new PlayHistoryEvent[Capacity(_count + 1)];
            Array.Copy(_buffer, _start, next, 0, _count);
            _buffer = next;
            _start = 0;
        }
        _buffer[_start + _count] = e;
        _count++;
    }

    /// <summary>
    /// Buffer size for <paramref name="needed"/> events: doubling while small, and at the
    /// cap a little slack so trimming copies the log once per that many plays, not per play.
    /// </summary>
    private int Capacity(int needed)
    {
        var slack = Math.Max(64, _maxEvents / 64);
        return Math.Max(needed, Math.Min(Math.Max(needed * 2, 256), _maxEvents + slack));
    }

    public void RecordSkip(Track track)
    {
        lock (_lock)
        {
            EnsureLoaded();
            // The play event was added when the track started, so it sits at
            // (or very near) the tail. Scan a short window from the end.
            var floor = Math.Max(0, _count - 25);
            for (var i = _count - 1; i >= floor; i--)
            {
                var e = _buffer![_start + i];
                if (e.TrackId == track.Id)
                {
                    // Edited in place: the snapshot's shape doesn't change, so nothing to
                    // republish. An event already in play_history.json needs it rewritten.
                    e.Skipped = true;
                    if (i < _count - TailCount) _baseDirtyVersion++;
                    ScheduleSave();
                    return;
                }
            }
        }
    }

    public void RemapTrackIds(IReadOnlyDictionary<Guid, Guid> remap)
    {
        if (remap.Count == 0) return;
        lock (_lock)
        {
            EnsureLoaded();
            var changed = false;
            var baseEnd = _count - TailCount;
            for (var i = 0; i < _count; i++)
            {
                var e = _buffer![_start + i];
                if (remap.TryGetValue(e.TrackId, out var newId))
                {
                    e.TrackId = newId;
                    changed = true;
                    if (i < baseEnd) _baseDirtyVersion++;
                }
            }
            if (!changed) return;
            ScheduleSave();
        }
    }

    public Task FlushAsync()
    {
        lock (_lock)
        {
            _saveDebounce?.Dispose();
            _saveDebounce = null;
            if (_buffer == null)
                return Task.CompletedTask;
        }
        return Task.Run(Save);
    }

    private void EnsureLoaded()
    {
        if (_buffer != null) return;

        var events = new List<PlayHistoryEvent>();
        try
        {
            if (File.Exists(_filePath))
            {
                // Streamed: ReadAllText first built the whole file as one string (80 MB of
                // UTF-16 for a 40 MB log) before parsing it.
                using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 16, FileOptions.SequentialScan);
                events = JsonSerializer.Deserialize<List<PlayHistoryEvent>>(stream) ?? new List<PlayHistoryEvent>();
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.Load", ex.Message);
            events = new List<PlayHistoryEvent>();
            _baseDirtyVersion++; // replace the unreadable file on the next save, as before
        }

        _baseFileCount = events.Count;
        _baseFileLastTicks = events.Count > 0 ? events[^1].PlayedAtUtc.Ticks : 0;

        var tail = ReadTail();
        if (tail != null)
        {
            events.AddRange(tail);
            _appended = tail.Count;
        }

        ShareStrings(events);

        var keep = Math.Min(events.Count, _maxEvents);
        _buffer = new PlayHistoryEvent[Capacity(keep + 1)];
        CollectionsMarshal.AsSpan(events)[(events.Count - keep)..].CopyTo(_buffer);
        _start = 0;
        _count = keep;
        PublishSnapshot();
        _loaded = true;
    }

    /// <summary>The tail written after play_history.json, or null when there is none or it
    /// extends a different version of that file.</summary>
    private List<PlayHistoryEvent>? ReadTail()
    {
        try
        {
            if (!File.Exists(_tailPath)) return null;
            using var stream = File.OpenRead(_tailPath);
            var tail = JsonSerializer.Deserialize<TailFile>(stream);
            if (tail?.Events == null || tail.Events.Count == 0) return null;
            if (tail.BaseCount != _baseFileCount || tail.BaseLastTicks != _baseFileLastTicks)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "PlayHistory.Load",
                    $"ignored {tail.Events.Count} tail events written after a different play_history.json");
                return null;
            }
            return tail.Events;
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.LoadTail", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// One string instance per distinct title and artist. Deserializing gives every event
    /// its own copies; the owner's 8,470 events had 1,726 distinct titles and 360 artists.
    /// </summary>
    private static void ShareStrings(List<PlayHistoryEvent> events)
    {
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Share(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(pool, s, out var exists);
            if (!exists) slot = s;
            return slot!;
        }
        foreach (var e in events)
        {
            e.Title = Share(e.Title);
            e.Artist = Share(e.Artist);
        }
    }

    private void ScheduleSave()
    {
        // Called under _lock. Debounce so rapid track changes coalesce into one write.
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ => Save(), null, SaveDebounceMs, Timeout.Infinite);
    }

    // Serializes concurrent Save() calls (debounce timer vs FlushAsync — Dispose
    // doesn't stop an already-running callback), which otherwise race on the
    // shared ".tmp" opened exclusively and drop one write. The state is captured
    // inside it too, so a save that started earlier can't overwrite a newer one.
    private readonly object _saveGate = new();

    private void Save()
    {
        try
        {
            lock (_saveGate)
            {
                IReadOnlyList<PlayHistoryEvent> snapshot;
                int tailCount, dirtyVersion, baseCount;
                long appended, baseLastTicks;
                bool full;
                lock (_lock)
                {
                    if (_buffer == null) return;
                    snapshot = _snapshot;
                    appended = _appended;
                    tailCount = TailCount;
                    dirtyVersion = _baseDirtyVersion;
                    baseCount = _baseFileCount;
                    baseLastTicks = _baseFileLastTicks;
                    full = dirtyVersion != _baseCleanVersion
                           || snapshot.Count <= FullRewriteMaxEvents
                           || tailCount >= CompactAfterTailEvents;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                if (full)
                {
                    WriteAtomically(_filePath, snapshot);
                    lock (_lock)
                    {
                        _baseAppended = appended;
                        _baseCleanVersion = dirtyVersion;
                        _baseFileCount = snapshot.Count;
                        _baseFileLastTicks = snapshot.Count > 0 ? snapshot[^1].PlayedAtUtc.Ticks : 0;
                    }
                    // Its events are in play_history.json now. A crash before this line leaves
                    // a tail that no longer matches the file, which the next load ignores.
                    if (File.Exists(_tailPath)) File.Delete(_tailPath);
                }
                else if (tailCount > 0)
                {
                    var events = new List<PlayHistoryEvent>(tailCount);
                    for (var i = snapshot.Count - tailCount; i < snapshot.Count; i++)
                        events.Add(snapshot[i]);
                    WriteAtomically(_tailPath, new TailFile
                    {
                        BaseCount = baseCount,
                        BaseLastTicks = baseLastTicks,
                        Events = events
                    });
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "PlayHistory.Save", ex.Message);
        }
    }

    /// <summary>Serializes straight to a temp file (no whole-log string) and swaps it in.</summary>
    private static void WriteAtomically<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            JsonSerializer.Serialize(stream, value);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>play_history.recent.json: the events logged after play_history.json was
    /// written, and which version of that file they follow.</summary>
    private sealed class TailFile
    {
        public int BaseCount { get; set; }
        public long BaseLastTicks { get; set; }
        public List<PlayHistoryEvent> Events { get; set; } = new();
    }
}
