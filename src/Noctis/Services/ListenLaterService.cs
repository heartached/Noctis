using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// JSON-file-backed Listen Later bookmarks under the Noctis data directory.
/// Mutations are cheap and thread-safe; disk writes are debounced on the
/// thread pool, mirroring PlayHistoryService.
/// </summary>
public sealed class ListenLaterService : IListenLaterService
{
    private const int SaveDebounceMs = 1_500;

    private readonly object _lock = new();
    private readonly string _filePath;
    private List<ListenLaterItem>? _items;
    private Timer? _saveDebounce;

    public event EventHandler? Changed;

    public ListenLaterService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.DataRoot, "listen_later.json");
    }

    public IReadOnlyList<ListenLaterItem> Items
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _items!
                    .OrderByDescending(i => i.AddedAtUtc)
                    .ToArray();
            }
        }
    }

    public void AddTrack(Track track) =>
        Add(new ListenLaterItem
        {
            Kind = ListenLaterKind.Track,
            TargetId = track.Id,
            Name = track.Title,
            Subtitle = track.ArtistDisplay,
        });

    public void AddAlbum(Album album) =>
        Add(new ListenLaterItem
        {
            Kind = ListenLaterKind.Album,
            TargetId = album.Id,
            Name = album.Name,
            Subtitle = album.Artist,
        });

    public void AddArtist(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return;
        Add(new ListenLaterItem
        {
            Kind = ListenLaterKind.Artist,
            Name = artistName.Trim(),
        });
    }

    private void Add(ListenLaterItem item)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_items!.Any(i => IsSame(i, item)))
                return;
            _items!.Add(item);
            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSame(ListenLaterItem a, ListenLaterItem b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind == ListenLaterKind.Artist
            ? string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : a.TargetId == b.TargetId;
    }

    public bool ContainsTrack(Guid trackId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items!.Any(i => i.Kind == ListenLaterKind.Track && i.TargetId == trackId);
        }
    }

    public bool ContainsAlbum(Guid albumId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items!.Any(i => i.Kind == ListenLaterKind.Album && i.TargetId == albumId);
        }
    }

    public bool ContainsArtist(string artistName)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items!.Any(i => i.Kind == ListenLaterKind.Artist &&
                string.Equals(i.Name, artistName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Remove(Guid itemId)
    {
        bool removed;
        lock (_lock)
        {
            EnsureLoaded();
            removed = _items!.RemoveAll(i => i.Id == itemId) > 0;
            if (removed) ScheduleSave();
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_items!.Count == 0) return;
            _items.Clear();
            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (_items != null) return;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _items = JsonSerializer.Deserialize<List<ListenLaterItem>>(json) ?? new List<ListenLaterItem>();
                return;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "ListenLater.Load", ex.Message);
        }
        _items = new List<ListenLaterItem>();
    }

    private void ScheduleSave()
    {
        // Called under _lock. Debounce rapid adds into one write.
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ => Save(), null, SaveDebounceMs, Timeout.Infinite);
    }

    private void Save()
    {
        try
        {
            ListenLaterItem[] snapshot;
            lock (_lock)
            {
                if (_items == null) return;
                snapshot = _items.ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "ListenLater.Save", ex.Message);
        }
    }

    /// <summary>Test hook: writes pending changes synchronously.</summary>
    public void FlushForTests() => Save();
}
