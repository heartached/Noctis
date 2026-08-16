using System.Runtime.InteropServices;

namespace Noctis.Services;

/// <summary>
/// Lightweight in-memory session log surfaced by Settings → About → Developer Mode.
/// Thread-safe, bounded, and self-seeded with the system info a bug report needs
/// (version, OS, install source), so "Copy Logs" is always useful even when
/// nothing else has logged yet.
/// </summary>
public static class DebugLog
{
    private const int MaxLines = 500;

    private static readonly object Lock = new();
    private static readonly List<string> Lines = new();
    private static readonly HashSet<string> OnceKeys = new(StringComparer.Ordinal);
    private static bool _seeded;

    // Disk mirror (CrashJournal). Invoked inside Lock so the file gets lines in
    // exactly the order the ring does.
    private static Action<string>? _sink;
    private static Action? _sinkReset;

    /// <summary>Raised after a write or clear. May fire on any thread.</summary>
    public static event Action? Changed;

    private static bool _vlcBridgeEnabled;

    /// <summary>Raised when <see cref="VlcBridgeEnabled"/> changes.</summary>
    public static event Action? VlcBridgeChanged;

    /// <summary>
    /// When true, the audio player mirrors LibVLC warning/error log lines into
    /// this log, so "Copy Logs" captures audio-engine complaints (underruns,
    /// "playback too late", device errors) without the NOCTIS_VLC_LOG env var.
    /// Follows the Developer Mode toggle. The player subscribes to VLC's log
    /// callback only while enabled — normal sessions pay no per-message cost.
    /// </summary>
    public static bool VlcBridgeEnabled
    {
        get => _vlcBridgeEnabled;
        set
        {
            if (_vlcBridgeEnabled == value) return;
            _vlcBridgeEnabled = value;
            VlcBridgeChanged?.Invoke();
        }
    }

    public static void Write(string source, string message)
    {
        // This log leaves the machine via "Copy Logs" — no auth-bearing URLs
        // (media-server stream tokens) may ever be stored in it.
        message = LogRedaction.Scrub(message);
        lock (Lock)
        {
            SeedLocked();
            var line = $"[{DateTime.Now:HH:mm:ss}] [{source}] {message}";
            Lines.Add(line);
            if (Lines.Count > MaxLines)
                Lines.RemoveRange(0, Lines.Count - MaxLines);
            _sink?.Invoke(line);
        }
        Changed?.Invoke();
    }

    public static void Write(string source, Exception ex) => Write(source, ex.ToString());

    /// <summary>
    /// Writes the first occurrence per <paramref name="key"/> and stays quiet on
    /// repeats — for failure paths that fire per track or per request (offline
    /// lyrics fetches, server artwork errors) and would otherwise flood the ring.
    /// </summary>
    public static void WriteOnce(string source, string key, string message)
    {
        lock (Lock)
        {
            if (OnceKeys.Contains(key)) return;
            // Pathological key churn: stop admitting new keys rather than flood.
            if (OnceKeys.Count >= 256) return;
            OnceKeys.Add(key);
        }
        Write(source, message + " (repeats of this are suppressed)");
    }

    /// <summary>
    /// Registers the disk mirror (one sink, set once at startup). Replays what
    /// is already in the ring — seeding the header first if nothing has logged
    /// yet — so the file always starts with the system info, then receives every
    /// later line in ring order. <paramref name="reset"/> runs on
    /// <see cref="Clear"/> so the file restarts alongside the ring.
    /// </summary>
    internal static void AttachSink(Action<string> sink, Action reset)
    {
        lock (Lock)
        {
            SeedLocked();
            foreach (var line in Lines)
                sink(line);
            _sink = sink;
            _sinkReset = reset;
        }
    }

    /// <summary>Current log contents as one string (oldest first).</summary>
    public static string Snapshot()
    {
        lock (Lock)
        {
            SeedLocked();
            return string.Join(Environment.NewLine, Lines);
        }
    }

    /// <summary>Clears the session log, keeping the system-info header.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Lines.Clear();
            OnceKeys.Clear();
            _seeded = false;
            _sinkReset?.Invoke();
            SeedLocked();
            if (_sink != null)
                foreach (var line in Lines)
                    _sink(line);
        }
        Changed?.Invoke();
    }

    private static void SeedLocked()
    {
        if (_seeded) return;
        _seeded = true;

        var v = UpdateService.CurrentVersion;
        Lines.Add($"Noctis {v.Major}.{v.Minor}.{v.Build}" +
                  (UpdateService.IsPrereleaseBuild ? " (pre-release)" : ""));
        Lines.Add($"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Lines.Add($"Install source: {UpdateService.Source}");
        // An AppImage's BaseDirectory is its throwaway /tmp squashfs mount, so
        // prefer $APPIMAGE (the real on-disk file) when set.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        Lines.Add($"Install location: {(string.IsNullOrEmpty(appImage) ? AppContext.BaseDirectory : appImage)}");
        Lines.Add($"Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add("────────────────────────────");
    }
}
