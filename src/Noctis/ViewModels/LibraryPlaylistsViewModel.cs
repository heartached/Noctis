using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the playlists library view.
/// Displays all user-created playlists in a grid similar to albums.
/// </summary>
public partial class LibraryPlaylistsViewModel : ViewModelBase, ISearchable
{
    private readonly SidebarViewModel _sidebar;
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;

    private string _currentFilter = string.Empty;
    private DispatcherTimer? _searchDebounce;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>All user-created playlists displayed in the grid.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    /// <summary>Filtered playlists for display.</summary>
    public ObservableCollection<Playlist> FilteredPlaylists { get; } = new();

    /// <summary>Fires when the user wants to open a playlist's detail view.</summary>
    public event EventHandler<Playlist>? PlaylistOpened;

    public LibraryPlaylistsViewModel(SidebarViewModel sidebar, PlayerViewModel player, ILibraryService library, IPersistenceService persistence)
    {
        _sidebar = sidebar;
        _player = player;
        _library = library;
        _persistence = persistence;

        // Re-filter when the source playlists change
        _sidebar.Playlists.CollectionChanged += (_, _) => ApplyFilter(_currentFilter);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Playlists));
        ApplyFilter(_currentFilter);
    }

    public void ApplyFilter(string query)
    {
        _currentFilter = query;

        FilteredPlaylists.Clear();

        var filtered = Playlists.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var playlist in filtered)
            FilteredPlaylists.Add(playlist);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                ApplyFilter(SearchText);
            };
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenPlaylist(Playlist playlist)
    {
        PlaylistOpened?.Invoke(this, playlist);
    }

    private List<Track> ResolvePlaylistTracks(Playlist playlist)
    {
        if (playlist.IsSmartPlaylist)
            return SmartPlaylistEvaluator.Evaluate(playlist, _library.Tracks);

        return playlist.TrackIds
            .Select(id => _library.GetTrackById(id))
            .Where(t => t != null)
            .Cast<Track>()
            .ToList();
    }

    [RelayCommand]
    private void PlayPlaylist(Playlist playlist)
    {
        var tracks = ResolvePlaylistTracks(playlist);
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShufflePlaylist(Playlist playlist)
    {
        var tracks = ResolvePlaylistTracks(playlist);
        if (tracks.Count == 0) return;
        var shuffled = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextPlaylist(Playlist playlist)
    {
        var tracks = ResolvePlaylistTracks(playlist);
        if (tracks.Count == 0) return;
        for (int i = tracks.Count - 1; i >= 0; i--)
            _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddPlaylistToQueue(Playlist playlist)
    {
        var tracks = ResolvePlaylistTracks(playlist);
        foreach (var track in tracks)
            _player.AddToQueue(track);
    }

    [RelayCommand]
    private async Task EditPlaylist(Playlist playlist)
    {
        await _sidebar.EditPlaylistAsync(playlist);
        ApplyFilter(_currentFilter);
    }

    [RelayCommand]
    private async Task CreateSmartPlaylist()
    {
        await _sidebar.CreateSmartPlaylistAsync();
    }

    [RelayCommand]
    private async Task DeletePlaylist(Playlist playlist)
    {
        await _sidebar.DeletePlaylistAsync(playlist.Id);
        OnPropertyChanged(nameof(Playlists));
        ApplyFilter(_currentFilter);
    }

    [RelayCommand]
    private async Task SetCoverArt(Playlist playlist)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;

        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Cover Art",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } }
            }
        });

        if (files.Count == 0) return;

        // Copy to app data covers directory
        var coversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Noctis", "playlist_covers");
        Directory.CreateDirectory(coversDir);

        var ext = Path.GetExtension(files[0].Name);
        var destPath = Path.Combine(coversDir, $"{playlist.Id}{ext}");

        await using var srcStream = await files[0].OpenReadAsync();
        await using var destStream = File.Create(destPath);
        await srcStream.CopyToAsync(destStream);

        playlist.CoverArtPath = destPath;
        playlist.ModifiedAt = DateTime.UtcNow;
        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
        ApplyFilter(_currentFilter);
    }

    [RelayCommand]
    private async Task RemoveCoverArt(Playlist playlist)
    {
        if (string.IsNullOrEmpty(playlist.CoverArtPath)) return;

        // Delete the file if it exists in our covers directory
        if (File.Exists(playlist.CoverArtPath))
        {
            try { File.Delete(playlist.CoverArtPath); } catch { /* non-fatal */ }
        }

        playlist.CoverArtPath = null;
        playlist.ModifiedAt = DateTime.UtcNow;
        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
        ApplyFilter(_currentFilter);
    }
}
