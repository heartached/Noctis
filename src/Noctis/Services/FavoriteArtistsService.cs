using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// Persists the user's favorite artists (GitHub #41) as a set of artist names in
/// favorite_artists.json under the data root. Keyed by name, not Artist.Id: the id is
/// itself derived from the name, and names stay valid if the id algorithm ever changes.
/// </summary>
public class FavoriteArtistsService
{
    private readonly string _filePath;
    private readonly HashSet<string> _names;
    private readonly object _gate = new();

    public FavoriteArtistsService()
        : this(Path.Combine(Helpers.AppPaths.DataRoot, "favorite_artists.json"))
    {
    }

    public FavoriteArtistsService(string filePath)
    {
        _filePath = filePath;
        _names = Load(filePath);
    }

    private static HashSet<string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (names != null)
                    return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoriteArtists] Load failed: {ex.Message}");
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsFavorite(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return false;
        lock (_gate) return _names.Contains(artistName);
    }

    public void SetFavorite(string artistName, bool favorite)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return;
        lock (_gate)
        {
            var changed = favorite ? _names.Add(artistName) : _names.Remove(artistName);
            if (!changed) return;
            Save();
        }
    }

    /// <summary>Called under <see cref="_gate"/>. Write-to-temp + move so a crash
    /// mid-write can't truncate the list.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                _names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoriteArtists] Save failed: {ex.Message}");
        }
    }
}
