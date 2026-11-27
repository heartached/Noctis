using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>One finished download in the window's "Recent downloads" list.</summary>
public sealed record RecentDownload(string Title, string Artist, string FilePath, DateTimeOffset When, bool InLibrary);

/// <summary>A video downloaded before (by its YouTube id), so a later search does not download it again.</summary>
/// <param name="AlbumId">The library album it landed in (for "Show"); empty when unknown.</param>
public sealed record DownloadedVideo(string VideoId, string FilePath, string AlbumId, bool InLibrary);

/// <summary>
/// What the window remembers between sessions, in the plugin's data folder (state.json): the
/// download folder the user chose (empty = the default next to the music folders), the format,
/// and the last downloads. A missing or broken file starts fresh; nothing here is required.
/// </summary>
public sealed class ImportState
{
    public const string FileName = "state.json";
    public const int MaxRecent = 20;
    /// <summary>How many downloaded videos the index remembers (oldest go first).</summary>
    public const int MaxDownloaded = 5000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<RecentDownload> _recent = new();
    private readonly List<DownloadedVideo> _downloaded = new();
    private readonly HashSet<string> _downloading = new(StringComparer.Ordinal);

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

    /// <summary>The earlier download of <paramref name="videoId"/> whose file is still there, or null
    /// (a record whose file is gone is forgotten: downloading again is then right).</summary>
    public DownloadedVideo? FindDownloaded(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;
        DownloadedVideo? found;
        lock (_gate) found = _downloaded.LastOrDefault(d => d.VideoId == videoId);
        if (found is null) return null;
        bool exists;
        try { exists = File.Exists(found.FilePath); } catch { exists = false; }
        if (exists) return found;
        lock (_gate) _downloaded.RemoveAll(d => d.VideoId == videoId);
        Save();
        return null;
    }

    public void AddDownloaded(DownloadedVideo item)
    {
        if (string.IsNullOrEmpty(item.VideoId)) return;
        lock (_gate)
        {
            _downloaded.RemoveAll(d => d.VideoId == item.VideoId);
            _downloaded.Add(item);
            if (_downloaded.Count > MaxDownloaded) _downloaded.RemoveRange(0, _downloaded.Count - MaxDownloaded);
        }
        Save();
    }

    /// <summary>Marks <paramref name="videoId"/> as downloading (any window of this plugin); false when it already is.</summary>
    public bool TryStartDownload(string videoId)
    {
        lock (_gate) return _downloading.Add(videoId);
    }

    public void EndDownload(string videoId)
    {
        lock (_gate) _downloading.Remove(videoId);
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
        public List<DownloadedVideo>? Downloaded { get; set; }
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
            if (dto.Downloaded is { } downloaded)
                _downloaded.AddRange(downloaded.Where(d => d is not null && !string.IsNullOrEmpty(d.VideoId) && !string.IsNullOrWhiteSpace(d.FilePath))
                    .TakeLast(MaxDownloaded).Select(d => d with { AlbumId = d.AlbumId ?? "" }));
        }
        catch { /* a broken file starts fresh */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Dto dto;
            lock (_gate) dto = new Dto { DownloadFolder = DownloadFolder, Format = Format, Recent = _recent.ToList(), Downloaded = _downloaded.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* remembering is a convenience */ }
    }
}
