using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Home tab — shows top songs by play count and recently played albums.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly SidebarViewModel _sidebar;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly EventHandler _libraryUpdatedHandler;
    private bool _isDirty = true;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Albums currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Album> CtrlSelectedAlbums { get; set; } = new();

    /// <summary>Top songs sorted by play count descending.</summary>
    public BulkObservableCollection<Track> TopSongs { get; } = new();

    /// <summary>Recently played albums (grouped from playback history).</summary>
    public BulkObservableCollection<Album> RecentlyPlayedAlbums { get; } = new();

    [ObservableProperty] private string _greeting = GetGreeting();

    /// <summary>Fires when the user wants to open an album's detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public HomeViewModel(PlayerViewModel player, ILibraryService library, SidebarViewModel sidebar)
    {
        _player = player;
        _library = library;
        _sidebar = sidebar;

        // Subscribe to track changes for real-time updates
        _player.TrackStarted += OnTrackStarted;

        // Subscribe to library changes with debounce to avoid flooding UI thread
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };
        _libraryUpdatedHandler = (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }); };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update greeting in case time changed
            Greeting = GetGreeting();

            var album = _library.GetAlbumById(track.AlbumId);
            if (album != null)
            {
                var existing = RecentlyPlayedAlbums.FirstOrDefault(a => a.Id == album.Id);
                if (existing != null)
                    RecentlyPlayedAlbums.Remove(existing);

                RecentlyPlayedAlbums.Insert(0, album);

                while (RecentlyPlayedAlbums.Count > 10)
                    RecentlyPlayedAlbums.RemoveAt(RecentlyPlayedAlbums.Count - 1);
            }
        });
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Refreshes the Home tab content with latest data.</summary>
    public async void Refresh()
    {
        if (!_isDirty && TopSongs.Count > 0)
        {
            Greeting = GetGreeting();
            return;
        }
        _isDirty = false;

        try
        {
            Greeting = GetGreeting();

            // Top songs: tracks with highest play count
            var allTracks = _library.Tracks;
            if (allTracks.Count > 0)
            {
                var top = await Task.Run(() =>
                    allTracks
                        .Where(t => t.PlayCount > 0)
                        .OrderByDescending(t => t.PlayCount)
                        .Take(6)
                        .ToList());
                TopSongs.ReplaceAll(top);
            }
            else
            {
                TopSongs.ReplaceAll(Array.Empty<Track>());
            }

            // Recently played albums: O(1) lookups via GetAlbumById
            var recentAlbums = _player.History
                .Take(50)
                .Select(t => t.AlbumId)
                .Distinct()
                .Take(10)
                .Select(id => _library.GetAlbumById(id))
                .OfType<Album>()
                .ToList();
            RecentlyPlayedAlbums.ReplaceAll(recentAlbums);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeVM] Refresh failed: {ex.Message}");
        }
    }

    private static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 21 => "Good evening",
            _ => "Good night"
        };
    }

    // ── Top Song commands ──

    [RelayCommand]
    private void PlayTopSong(Track track)
    {
        var tracks = TopSongs.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task AddTrackToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddTrackToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
    }

    [RelayCommand]
    private async Task ToggleTrackFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private void ShowInExplorerTrack(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task OpenTrackMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private void SearchLyricsTrack(Track track)
    {
        _searchLyricsAction?.Invoke(track);
    }

    [RelayCommand]
    private void ShuffleTopSongs()
    {
        var tracks = TopSongs.ToList();
        if (tracks.Count == 0) return;
        var shuffled = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    /// <summary>Fires when the user wants to view a track's album.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    [RelayCommand]
    private void ViewAlbumFromTrack(Track track)
    {
        ViewAlbumRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private async Task RemoveTrackFromLibrary(Track track)
    {
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        await _library.RemoveTrackAsync(track.Id);
    }

    // ── Album commands ──

    [RelayCommand]
    private void OpenAlbum(Album album)
    {
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void PlayAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(album.Tracks, 0);
    }

    [RelayCommand]
    private void ShuffleAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        var shuffled = album.Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        var tracks = album.Tracks.ToList();
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            _player.AddNext(tracks[i]);
        }
    }

    [RelayCommand]
    private void AddAlbumToQueue(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        _player.AddRangeToQueue(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : new List<Album> { album };
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        foreach (var a in albums)
        {
            if (a.Tracks == null || a.Tracks.Count == 0) continue;
            var newState = !a.IsAllTracksFavorite;
            foreach (var track in a.Tracks)
                track.IsFavorite = newState;
        }
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0]);
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = albums.SelectMany(a => a.Tracks ?? new()).Select(t => t.Id).ToList();
        if (trackIds.Count > 0)
            await _library.RemoveTracksAsync(trackIds);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private void ShowInExplorerAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var filePath = album.Tracks[0].FilePath;
        if (!File.Exists(filePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(filePath);
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyricsAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        _searchLyricsAction?.Invoke(album.Tracks[0]);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    public void Dispose()
    {
        _refreshDebounce.Stop();
        _player.TrackStarted -= OnTrackStarted;
        _library.LibraryUpdated -= _libraryUpdatedHandler;
    }
}
