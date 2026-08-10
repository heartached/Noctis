using System.Collections.Concurrent;

namespace Noctis.Services;

/// <summary>
/// Lightweight debug logger with categorized, leveled, timestamped entries.
/// Zero overhead when disabled (early return). Thread-safe ring buffer.
/// Toggle via <see cref="IsEnabled"/> or Ctrl+Shift+D in the UI.
/// </summary>
public static class DebugLogger
{
    public enum Category { UI, Playback, Lyrics, Queue, Search, ContextMenu, State, Error }
    public enum Level { Info, Warn, Error }

    public sealed record LogEntry(
        DateTime Timestamp,
        Category Category,
        Level Level,
        string Action,
        string? Metadata = null);

    private static readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 500;

    /// <summary>Master switch. When false, Log() is a no-op.</summary>
    public static bool IsEnabled { get; set; }

    /// <summary>
    /// Mirrors <see cref="Category.Playback"/> entries into <see cref="DebugLog"/>, the
    /// session log behind Settings → Developer Mode → "Copy Logs".
    /// <para>
    /// Without this, entries here reached only the in-app debug panel, so a bug report
    /// carrying a full session log still had no record of device changes, keep-alive
    /// errors or session-volume resolution — the audio-dropout evidence. Playback only:
    /// the other categories are UI chatter that would flush DebugLog's 500-line ring.
    /// </para>
    /// </summary>
    public static bool MirrorPlaybackToSessionLog { get; set; }

    /// <summary>Actions that already write to <see cref="DebugLog"/> at their call site
    /// (they must be recorded even when this logger is off), so mirroring them would
    /// double every line.</summary>
    private static readonly HashSet<string> SessionLogSelfWriters =
        new(StringComparer.Ordinal) { "PositionTimer.Stall" };

    /// <summary>Also write to System.Diagnostics.Debug output.</summary>
    public static bool MirrorToDebugOutput { get; set; }
#if DEBUG
        = true;
#endif

    /// <summary>Fires on the calling thread whenever a new entry is added.</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static void Log(Category category, Level level, string action, string? metadata = null)
    {
        if (!IsEnabled) return;

        var entry = new LogEntry(DateTime.Now, category, level, action, metadata);
        _entries.Enqueue(entry);

        // Trim ring buffer
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        if (MirrorToDebugOutput)
        {
            var meta = metadata != null ? $" | {metadata}" : "";
            System.Diagnostics.Debug.WriteLine($"[DBG:{category}:{level}] {action}{meta}");
        }

        if (MirrorPlaybackToSessionLog && category == Category.Playback &&
            !SessionLogSelfWriters.Contains(action))
        {
            var meta = metadata != null ? $" | {metadata}" : "";
            DebugLog.Write("Playback", level == Level.Info ? $"{action}{meta}" : $"{level}: {action}{meta}");
        }

        EntryAdded?.Invoke(entry);
    }

    public static void Info(Category category, string action, string? metadata = null)
        => Log(category, Level.Info, action, metadata);

    public static void Warn(Category category, string action, string? metadata = null)
        => Log(category, Level.Warn, action, metadata);

    public static void Error(Category category, string action, string? metadata = null)
        => Log(category, Level.Error, action, metadata);

    /// <summary>Returns a snapshot of all entries (oldest first).</summary>
    public static LogEntry[] GetEntries() => _entries.ToArray();

    /// <summary>Returns entries filtered by category.</summary>
    public static LogEntry[] GetEntries(Category category)
        => _entries.Where(e => e.Category == category).ToArray();

    public static void Clear() => _entries.Clear();
}
