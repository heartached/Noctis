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
