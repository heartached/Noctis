using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Plugins.ABLoop.Looping;

/// <summary>A named part of a song, in seconds.</summary>
public sealed record SavedLoop(string Name, double A, double B)
{
    [JsonIgnore] public TimeSpan Start => TimeSpan.FromSeconds(A);
    [JsonIgnore] public TimeSpan End => TimeSpan.FromSeconds(B);
}

/// <summary>
/// Saved loops, per song (keyed by its file path), in loops.json in the plugin's data folder.
/// With "Keep loops for each song" off they live only in memory until Noctis closes.
/// </summary>
public sealed class LoopStore
{
    public const string FileName = "loops.json";
    public const int MaxPerSong = 50;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string? _file;
    private readonly Action<string>? _log;
    private Dictionary<string, List<SavedLoop>> _songs = new(PathComparer);

    /// <param name="dataDirectory">Where loops.json lives; null keeps everything in memory.</param>
    public LoopStore(string? dataDirectory, Action<string>? log = null)
    {
        _file = dataDirectory is null ? null : Path.Combine(dataDirectory, FileName);
        _log = log;
        Load();
    }

    /// <summary>Write to disk on every change (the "Keep loops for each song" setting).</summary>
    public bool Persist { get; set; } = true;

    /// <summary>Keep the loop window above other windows (remembered with the loops).</summary>
    public bool OnTop { get; private set; }

    public int SongCount => _songs.Count;

    public IReadOnlyList<SavedLoop> For(string? path)
        => !string.IsNullOrEmpty(path) && _songs.TryGetValue(path, out var list) ? list : Array.Empty<SavedLoop>();

    /// <summary>Saves a loop for <paramref name="path"/> ("Loop 3" when no name is given). Returns it.</summary>
    public SavedLoop Add(string path, TimeSpan a, TimeSpan b, string? name = null)
    {
        if (!_songs.TryGetValue(path, out var list)) _songs[path] = list = new List<SavedLoop>();
        if (string.IsNullOrWhiteSpace(name))
        {
            var n = list.Count + 1;
            while (list.Any(l => l.Name == "Loop " + n)) n++;
            name = "Loop " + n;
        }
        var loop = new SavedLoop(name.Trim(), Math.Round(a.TotalSeconds, 3), Math.Round(b.TotalSeconds, 3));
        list.Add(loop);
        if (list.Count > MaxPerSong) list.RemoveAt(0);
        Save();
        return loop;
    }

    public void Rename(string path, SavedLoop loop, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_songs.TryGetValue(path, out var list)) return;
        var i = list.IndexOf(loop);
        if (i < 0) return;
        list[i] = loop with { Name = name.Trim().Length > 60 ? name.Trim()[..60] : name.Trim() };
        Save();
    }

    public void Remove(string path, SavedLoop loop)
    {
        if (!_songs.TryGetValue(path, out var list) || !list.Remove(loop)) return;
        if (list.Count == 0) _songs.Remove(path);
        Save();
    }

    public void SetOnTop(bool on)
    {
        OnTop = on;
        Save();
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_file));
            if (data is null) return;
            OnTop = data.OnTop;
            _songs = new Dictionary<string, List<SavedLoop>>(PathComparer);
            foreach (var (path, loops) in data.Songs ?? new())
            {
                var good = (loops ?? new()).Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name)
                    && double.IsFinite(l.A) && double.IsFinite(l.B) && l.A >= 0 && l.B > l.A).ToList();
                if (good.Count > 0 && !string.IsNullOrEmpty(path)) _songs[path] = good;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke("could not read the saved loops: " + ex.Message);
        }
    }

    /// <summary>Writes loops.json (beside, then swapped in, so a crash never loses the file).</summary>
    public void Save()
    {
        if (_file is null || !Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            var data = new FileData { Version = 1, OnTop = OnTop, Songs = _songs.ToDictionary(k => k.Key, v => v.Value) };
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("could not save the loops: " + ex.Message);
        }
    }

    private sealed class FileData
    {
        public int Version { get; set; } = 1;
        public bool OnTop { get; set; }
        public Dictionary<string, List<SavedLoop>>? Songs { get; set; }
    }
}
