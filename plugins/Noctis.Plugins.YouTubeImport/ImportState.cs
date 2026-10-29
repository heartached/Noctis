using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>One finished download in the window's "Recent downloads" list.</summary>
public sealed record RecentDownload(string Title, string Artist, string FilePath, DateTimeOffset When, bool InLibrary);

/// <summary>
/// What the window remembers between sessions, in the plugin's data folder (state.json): the
/// download folder the user chose (empty = the default next to the music folders), the format,
/// and the last downloads. A missing or broken file starts fresh; nothing here is required.
/// </summary>
public sealed class ImportState
{
    public const string FileName = "state.json";
    public const int MaxRecent = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<RecentDownload> _recent = new();

    /// <param name="legacyFolder">First run only (no state.json yet): the download folder set in
    /// Noctis before this was a plugin, so a custom folder is not silently dropped (owner 10-29).</param>
    public ImportState(string? dataDirectory, Func<string?>? legacyFolder = null)
    {
        _path = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, FileName);
        Load(legacyFolder);
    }

    /// <summary>The folder chosen in the window; empty = the default.</summary>
    public string DownloadFolder { get; private set; } = string.Empty;

    /// <summary><see cref="AudioFormat.Key"/> of the chosen format.</summary>
    public string Format { get; private set; } = AudioFormat.Default.Key;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentDownload> Recent { get { lock (_gate) return _recent.ToList(); } }

    /// <summary>Raised (on the caller's thread) after <see cref="AddRecent"/> or <see cref="ClearRecent"/>.</summary>
    public event EventHandler? RecentChanged;

    public void SetDownloadFolder(string? folder)
    {
        DownloadFolder = folder?.Trim() ?? string.Empty;
        Save();
    }

    public void SetFormat(string key)
    {
        Format = AudioFormat.FromKey(key).Key;
        Save();
    }

    public void AddRecent(RecentDownload item)
    {
        lock (_gate)
        {
            _recent.RemoveAll(r => string.Equals(r.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, item);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }
        Save();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRecent()
    {
        lock (_gate) _recent.Clear();
        Save();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Dto
    {
        public string? DownloadFolder { get; set; }
        public string? Format { get; set; }
        public List<RecentDownload>? Recent { get; set; }
    }

    private void Load(Func<string?>? legacyFolder)
    {
        if (_path is null) return;
        if (!File.Exists(_path))
        {
            string? old = null;
            try { old = legacyFolder?.Invoke()?.Trim(); } catch { /* nothing to carry over */ }
            // Saved at once, so a folder cleared later in the window is not brought back.
            if (!string.IsNullOrEmpty(old)) SetDownloadFolder(old);
            return;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), Json);
            if (dto is null) return;
            DownloadFolder = dto.DownloadFolder?.Trim() ?? string.Empty;
            Format = AudioFormat.FromKey(dto.Format).Key;
            if (dto.Recent is { } recent)
                _recent.AddRange(recent.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.FilePath)).Take(MaxRecent));
        }
        catch { /* a broken file starts fresh */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Dto dto;
            lock (_gate) dto = new Dto { DownloadFolder = DownloadFolder, Format = Format, Recent = _recent.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* remembering is a convenience */ }
    }
}
