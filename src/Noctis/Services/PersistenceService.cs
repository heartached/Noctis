using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// JSON-file-based persistence. All data lives under %APPDATA%\Noctis\.
///
/// File layout:
///   %APPDATA%\Noctis\
///   ├── settings.json
///   ├── library.json
///   ├── playlists.json
///   ├── queue.json
///   ├── indexes.json
///   └── artwork\
///       ├── {albumId}.jpg
///       └── ...
/// </summary>
public class PersistenceService : IPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private string LibraryPath => Path.Combine(DataDirectory, "library.json");
    private string PlaylistsPath => Path.Combine(DataDirectory, "playlists.json");
    private string QueuePath => Path.Combine(DataDirectory, "queue.json");
    private string IndexCachePath => Path.Combine(DataDirectory, "indexes.json");
    private string ArtworkDirectory => Path.Combine(DataDirectory, "artwork");

    public PersistenceService()
    {
        // Use %APPDATA%\Noctis\ as the data root
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DataDirectory = Path.Combine(appData, "Noctis");

        // Ensure directories exist
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ArtworkDirectory);
    }

    // ── Settings ──────────────────────────────────────────────

    public async Task<AppSettings> LoadSettingsAsync()
    {
        return await LoadJsonAsync<AppSettings>(SettingsPath) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await SaveJsonAsync(SettingsPath, settings);
    }

    // ── Library ───────────────────────────────────────────────

    public async Task<List<Track>?> LoadLibraryAsync()
    {
        return await LoadJsonAsync<List<Track>>(LibraryPath);
    }

    public async Task SaveLibraryAsync(List<Track> tracks)
    {
        await SaveJsonAsync(LibraryPath, tracks);
    }

    // ── Playlists ─────────────────────────────────────────────

    public async Task<List<Playlist>> LoadPlaylistsAsync()
    {
        return await LoadJsonAsync<List<Playlist>>(PlaylistsPath) ?? new List<Playlist>();
    }

    public async Task SavePlaylistsAsync(List<Playlist> playlists)
    {
        await SaveJsonAsync(PlaylistsPath, playlists);
    }

    // ── Queue State ───────────────────────────────────────────

    public async Task<QueueState?> LoadQueueStateAsync()
    {
        return await LoadJsonAsync<QueueState>(QueuePath);
    }

    public async Task SaveQueueStateAsync(QueueState state)
    {
        await SaveJsonAsync(QueuePath, state);
    }

    // ── Index Cache ───────────────────────────────────────────

    public async Task<LibraryIndexCache?> LoadIndexCacheAsync()
    {
        return await LoadJsonAsync<LibraryIndexCache>(IndexCachePath);
    }

    public async Task SaveIndexCacheAsync(LibraryIndexCache cache)
    {
        await SaveJsonAsync(IndexCachePath, cache);
    }

    // ── Artwork ───────────────────────────────────────────────

    public string GetArtworkPath(Guid albumId)
    {
        return Path.Combine(ArtworkDirectory, $"{albumId}.jpg");
    }

    public void SaveArtwork(Guid albumId, byte[] imageData)
    {
        try
        {
            var path = GetArtworkPath(albumId);
            File.WriteAllBytes(path, imageData);
        }
        catch
        {
            // Non-critical: if artwork save fails, we just won't have a cached image
        }
    }

    // ── Helpers ───────────────────────────────────────────────

    private static async Task<T?> LoadJsonAsync<T>(string path) where T : class
    {
        // If the main file doesn't exist but the temp does, a previous save crashed
        // mid-write. The temp file may be complete, so try it as a recovery path.
        var tempPath = path + ".tmp";
        if (!File.Exists(path) && File.Exists(tempPath))
        {
            try
            {
                File.Move(tempPath, path);
            }
            catch
            {
                // If recovery fails, we'll just return null below
            }
        }

        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PersistenceService] Failed to load {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static async Task SaveJsonAsync<T>(string path, T data)
    {
        // Write to temp file first, then rename — prevents data loss on crash
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
            }

            // Atomic rename (on NTFS this is atomic for same-volume moves)
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            // Propagate the error so callers know the save failed.
            // Previously this was silently swallowed, causing data loss
            // (library, playlists, settings) without any notification.
            System.Diagnostics.Debug.WriteLine($"[PersistenceService] Failed to save {Path.GetFileName(path)}: {ex.Message}");
            throw;
        }
    }
}
