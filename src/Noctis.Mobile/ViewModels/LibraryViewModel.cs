using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The phone Library over the shared Core library: the count tiles, the lists they open
/// (songs, favourites, recently added, playlists) and the folder flow (SAF pick →
/// AppSettings.MusicFolders → scan). Library events arrive on scan threads; <c>marshal</c>
/// hops them to the UI thread (tests pass a direct call). <see cref="Refreshed"/> tells open
/// pages to re-read, so a rescan or a heart toggle shows up without re-navigating.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int RecentlyAddedDays = 30;

    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IFolderPicker _picker;
    private readonly Action<Action> _marshal;
    private List<Playlist> _playlists = new();

    public LibraryViewModel(ILibraryService library, IPersistenceService persistence, IFolderPicker picker,
        Action<Action>? marshal = null)
    {
        _library = library;
        _persistence = persistence;
        _picker = picker;
        _marshal = marshal ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));

        _library.LibraryUpdated += (_, _) => _marshal(RefreshFromLibrary);
        _library.FavoritesChanged += (_, _) => _marshal(RefreshFromLibrary);
        _library.ScanProgress += (_, n) => _marshal(() => ScanProgress = n);
        _library.ScanAborted += (_, roots) => _marshal(() =>
        {
            NeedsReconnect = true;
            StatusText = $"{roots.Length} folder(s) unavailable. Reconnect them and rescan.";
        });
    }

    /// <summary>The shared Core library, for the pages built on this view model.</summary>
    public ILibraryService Service => _library;

    public IPersistenceService Persistence => _persistence;

    /// <summary>Raised on the UI thread after every rebuild (library update, favourites, playlists).</summary>
    public event EventHandler? Refreshed;

    [ObservableProperty] private int _songCount;
    [ObservableProperty] private int _albumCount;
    [ObservableProperty] private int _artistCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _recentlyAddedCount;
    [ObservableProperty] private int _playlistCount;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _scanProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectCard))]
    private bool _hasFolders;

    /// <summary>The last scan aborted on an unreadable root (a revoked SAF grant, spec §7):
    /// the page offers to re-pick the folder, which is how a dead grant is recovered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine))]
    private bool _needsReconnect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine))]
    private string _statusText = string.Empty;

    /// <summary>First launch: no folder yet, so the only thing to show is the way in.</summary>
    public bool ShowConnectCard => !HasFolders;

    /// <summary>The plain status line; the reconnect card carries the text while it is up.</summary>
    public bool ShowStatusLine => !NeedsReconnect && !string.IsNullOrEmpty(StatusText);

    /// <summary>Every track, sorted by title. Rebuilt with one Reset on each library update.</summary>
    public BulkObservableCollection<Track> Songs { get; } = new();

    /// <summary>The configured roots (SAF tree URIs on Android), mirrored from settings.</summary>
    public ObservableCollection<string> Folders { get; } = new();

    public IReadOnlyList<Playlist> Playlists => _playlists;

    public IEnumerable<Track> Favourites() => Songs.Where(t => t.IsFavorite);

    public IEnumerable<Track> RecentlyAdded()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RecentlyAddedDays);
        return _library.Tracks.Where(t => t.DateAdded >= cutoff).OrderByDescending(t => t.DateAdded);
    }

    public async Task InitializeAsync()
    {
        var settings = await _persistence.LoadSettingsAsync();
        SetFolders(settings.MusicFolders);
        await LoadPlaylistsAsync();
        RefreshFromLibrary();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var picked = await _picker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(picked)) return;

        var settings = await _persistence.LoadSettingsAsync();
        if (!settings.MusicFolders.Contains(picked))
        {
            settings.MusicFolders.Add(picked);
            await _persistence.SaveSettingsAsync(settings);
        }
        SetFolders(settings.MusicFolders);
        await ScanAsync(settings.MusicFolders);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        var settings = await _persistence.LoadSettingsAsync();
        if (settings.MusicFolders.Count == 0) return;
        await ScanAsync(settings.MusicFolders);
    }

    private async Task ScanAsync(IEnumerable<string> folders)
    {
        if (IsScanning) return;
        IsScanning = true;
        NeedsReconnect = false;
        ScanProgress = 0;
        StatusText = "Scanning…";
        try
        {
            await _library.ScanAsync(folders);
            if (StatusText == "Scanning…") StatusText = string.Empty; // ScanAborted may have replaced it
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Phone scan failed: {ex.Message}");
            StatusText = "Scan failed. See the log.";
        }
        finally
        {
            IsScanning = false;
            RefreshFromLibrary();
        }
    }

    private async Task LoadPlaylistsAsync()
    {
        try
        {
            _playlists = await _persistence.LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Playlist load failed: {ex.Message}");
            _playlists = new List<Playlist>();
        }
        PlaylistCount = _playlists.Count;
    }

    private void SetFolders(IEnumerable<string> folders)
    {
        Folders.Clear();
        foreach (var f in folders) Folders.Add(f);
        HasFolders = Folders.Count > 0;
    }

    private void RefreshFromLibrary()
    {
        var tracks = _library.Tracks;
        SongCount = tracks.Count;
        AlbumCount = _library.Albums.Count;
        ArtistCount = _library.Artists.Count;
        FavoriteCount = tracks.Count(t => t.IsFavorite);
        var cutoff = DateTime.UtcNow.AddDays(-RecentlyAddedDays);
        RecentlyAddedCount = tracks.Count(t => t.DateAdded >= cutoff);
        Songs.ReplaceAll(tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase));
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
}
