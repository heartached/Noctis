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
    private static bool _seeded;

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
        lock (Lock)
        {
            SeedLocked();
            Lines.Add($"[{DateTime.Now:HH:mm:ss}] [{source}] {message}");
            if (Lines.Count > MaxLines)
                Lines.RemoveRange(0, Lines.Count - MaxLines);
        }
        Changed?.Invoke();
    }

    public static void Write(string source, Exception ex) => Write(source, ex.ToString());

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
            _seeded = false;
            SeedLocked();
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
        Lines.Add($"Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add("────────────────────────────");
    }
}
