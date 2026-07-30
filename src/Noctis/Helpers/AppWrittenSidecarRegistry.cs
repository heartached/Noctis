using System.Text.Json;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// Persistent registry of the lyric sidecar files (.lrc next to a track) that Noctis
/// itself wrote. "Remove lyrics" deletes only registered paths, so a user's own
/// sidecar is never removed on their behalf, and "Try alternate" may overwrite only
/// registered paths, so a user's own sidecar is never replaced either.
///
/// This used to be an in-memory static HashSet, which emptied on every restart: the
/// app's own auto-written sidecar then looked user-owned, Remove skipped it, and the
/// sidecar probe resurrected the removed lyrics on the next play — forever. The set
/// is now backed by a JSON file under the data root: loaded lazily, saved on every
/// mutation, thread-safe.
/// </summary>
public sealed class AppWrittenSidecarRegistry
{
    /// <summary>Registry backed by the app's data directory.</summary>
    public static AppWrittenSidecarRegistry Default { get; } =
        new(Path.Combine(AppPaths.DataRoot, "app_written_sidecars.json"));

    private readonly object _lock = new();
    private readonly string _filePath;
    private HashSet<string>? _paths;

    public AppWrittenSidecarRegistry(string filePath) => _filePath = filePath;

    /// <summary>Records a sidecar path as app-written and persists immediately.</summary>
    public void Add(string path)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_paths!.Add(path))
                Save();
        }
    }

    /// <summary>
    /// Unregisters a sidecar path. Returns true when the path was registered —
    /// i.e. the file is the app's own and is safe to delete.
    /// </summary>
    public bool Remove(string path)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var removed = _paths!.Remove(path);
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>True when the path was written by the app.</summary>
    public bool Contains(string path)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _paths!.Contains(path);
        }
    }

    private void EnsureLoaded()
    {
        if (_paths != null) return;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                _paths = new HashSet<string>(list ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception ex)
        {
            // A corrupt registry degrades to "nothing is ours" — Remove then leaves
            // sidecars behind rather than ever deleting a user's file.
            DebugLogger.Error(DebugLogger.Category.Error, "SidecarRegistry.Load", ex.Message);
        }
        _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Called under _lock. Atomic tmp+move so a crash never leaves a torn file.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_paths!.ToList()));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "SidecarRegistry.Save", ex.Message);
        }
    }
}
