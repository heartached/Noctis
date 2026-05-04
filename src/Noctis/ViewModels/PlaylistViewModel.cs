using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for displaying and managing a single playlist.
/// </summary>
public partial class PlaylistViewModel : ViewModelBase, ISearchable, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly SidebarViewModel _sidebar;
    private readonly ArtistImageService? _artistImageService;
    private readonly System.ComponentModel.PropertyChangedEventHandler _playerPropertyChangedHandler;

    private Playlist _playlist;
    private PlaylistNavItem? _navItem;
    private string _currentFilter = string.Empty;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Tracks currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Track> CtrlSelectedTracks { get; set; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _trackCount;
    [ObservableProperty] private string _totalDuration = "";
    [ObservableProperty] private string _totalSize = "";
    [ObservableProperty] private bool _isSmartPlaylist;
    [ObservableProperty] private string? _playlistArtworkPath;
    [ObservableProperty] private Guid? _currentPlayingTrackId;
    [ObservableProperty] private bool _isPlayerPlaying;
    [ObservableProperty] private string _playlistDescription = string.Empty;
    [ObservableProperty] private bool _isDescriptionOpen;
    [ObservableProperty] private bool _isDescriptionEditing;
    [ObservableProperty] private string _descriptionEditorText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(PlaylistDescription);
    public bool HasDescriptionChanges =>
        !string.Equals(
            (DescriptionEditorText ?? string.Empty).Trim(),
            PlaylistDescription.Trim(),
            StringComparison.Ordinal);

    public bool IsManualPlaylist => !IsSmartPlaylist;

    /// <summary>Formatted creation date, e.g. "Created July 2026".</summary>
    public string CreatedDateDisplay =>
        $"Created {_playlist.CreatedAt.ToLocalTime():MMMM yyyy}";

    /// <summary>Playlist cover color (hex).</summary>
    public string PlaylistColor => _playlist.Color;

    /// <summary>Custom cover art path (if set).</summary>
    public string? PlaylistCoverArtPath => _playlist.CoverArtPath;

    /// <summary>Up to 4 unique album art paths for collage (synced from sidebar nav item).</summary>
    public string? Art1 => _navItem?.Art1;
    public string? Art2 => _navItem?.Art2;
    public string? Art3 => _navItem?.Art3;
    public string? Art4 => _navItem?.Art4;
    public bool HasCustomArt => !string.IsNullOrEmpty(PlaylistCoverArtPath);
    public bool HasCollageArt => !HasCustomArt && Art1 != null && Art2 != null;
    public bool HasSingleArt => !HasCustomArt && Art1 != null && Art2 == null;
    public bool ShowFallbackIcon => !HasCustomArt && Art1 == null;

    /// <summary>Resolved tracks in this playlist (order matches playlist).</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    /// <summary>Unique artists represented by tracks in this playlist.</summary>
    public ObservableCollection<PlaylistFeaturedArtist> FeaturedArtists { get; } = new();
    public bool HasFeaturedArtists => FeaturedArtists.Count > 0;
    public string? FirstFeaturedArtistName => FeaturedArtists.FirstOrDefault()?.Name;

    /// <summary>Fires when the user wants to go back to the previous view.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Fires when the user wants to view an album from a track.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    /// <summary>Exposes sidebar playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public PlaylistViewModel(Playlist playlist, PlayerViewModel player,
        ILibraryService library, IPersistenceService persistence, SidebarViewModel sidebar,
        ArtistImageService? artistImageService = null)
    {
        _player = player;
        _library = library;
        _persistence = persistence;
        _sidebar = sidebar;
        _artistImageService = artistImageService;
        _playlist = playlist;
        _navItem = _sidebar.PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id);
        _name = playlist.Name;
        _isSmartPlaylist = playlist.IsSmartPlaylist;
        _playlistDescription = playlist.Description ?? string.Empty;

        LoadTracks();

        // Track the currently playing song
        CurrentPlayingTrackId = _player.CurrentTrack?.Id;
        IsPlayerPlaying = _player.State == PlaybackState.Playing;
        _playerPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                CurrentPlayingTrackId = _player.CurrentTrack?.Id;
            if (e.PropertyName == nameof(PlayerViewModel.State))
                IsPlayerPlaying = _player.State == PlaybackState.Playing;
        };
        _player.PropertyChanged += _playerPropertyChangedHandler;

        if (_isSmartPlaylist)
            _library.LibraryUpdated += OnLibraryUpdated;

        _sidebar.PlaylistTracksChanged += OnPlaylistTracksChanged;
    }

    private void OnLibraryUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => LoadTracks());
    }

    private void OnPlaylistTracksChanged(object? sender, Guid playlistId)
    {
        if (playlistId == _playlist.Id)
            Dispatcher.UIThread.Post(() => LoadTracks());
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        LoadTracks();
    }

    private void LoadTracks()
    {
        Tracks.Clear();

        IEnumerable<Track> resolved;

        if (_playlist.IsSmartPlaylist)
        {
            resolved = SmartPlaylistEvaluator.Evaluate(_playlist, _library.Tracks);
        }
        else
        {
            resolved = _playlist.TrackIds
                .Select(id => _library.GetTrackById(id))
                .OfType<Track>();
        }

        var allResolvedTracks = resolved.ToList();

        if (!string.IsNullOrWhiteSpace(_currentFilter))
        {
            resolved = allResolvedTracks.Where(t =>
                t.Title.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            resolved = allResolvedTracks;
        }

        foreach (var track in resolved)
            Tracks.Add(track);

        TrackCount = Tracks.Count;
        var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        TotalDuration = total.TotalHours >= 1
            ? $"{(int)total.TotalHours}h {total.Minutes}m"
            : $"{(int)total.TotalMinutes} min";

        // Compute total file size
        long totalBytes = 0;
        foreach (var t in Tracks)
        {
            try { if (File.Exists(t.FilePath)) totalBytes += new FileInfo(t.FilePath).Length; }
            catch { /* non-fatal */ }
        }
        TotalSize = totalBytes switch
        {
            >= 1_073_741_824 => $"{totalBytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{totalBytes / 1_048_576.0:F1} MB",
            _ => $"{totalBytes / 1024.0:F0} KB"
        };

        // Use first track's album artwork as playlist cover
        PlaylistArtworkPath = Tracks.FirstOrDefault()?.AlbumArtworkPath;

        RebuildFeaturedArtists(allResolvedTracks);
    }

    private void RebuildFeaturedArtists(IReadOnlyList<Track> tracks)
    {
        var artistCounts = tracks
            .SelectMany(t => Track.ParseArtistTokens(t.Artist))
            .Where(name => !string.Equals(name, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.First(), TrackCount = group.Count() })
            .OrderByDescending(item => item.TrackCount)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        FeaturedArtists.Clear();

        if (artistCounts.Count == 0)
        {
            OnPropertyChanged(nameof(HasFeaturedArtists));
            OnPropertyChanged(nameof(FirstFeaturedArtistName));
            return;
        }

        var libraryArtists = _library.Artists
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var artistsToFetch = new List<Artist>();

        foreach (var entry in artistCounts)
        {
            libraryArtists.TryGetValue(entry.Name, out var artist);
            var item = new PlaylistFeaturedArtist(
                entry.Name,
                entry.TrackCount,
                artist?.ImagePath);

            FeaturedArtists.Add(item);

            if (artist != null)
            {
                artistsToFetch.Add(artist);
                if (_artistImageService?.HasCachedImage(artist.Id) == true)
                    item.ImagePath = _artistImageService.GetCachedImagePath(artist.Id);
            }
        }

        OnPropertyChanged(nameof(HasFeaturedArtists));
        OnPropertyChanged(nameof(FirstFeaturedArtistName));

        if (_artistImageService != null && artistsToFetch.Count > 0)
        {
            var itemsByName = FeaturedArtists.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            _ = _artistImageService.FetchAndCacheAsync(artistsToFetch, (artist, path) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (itemsByName.TryGetValue(artist.Name, out var item))
                        item.ImagePath = path;
                });
            });
        }
    }

    [RelayCommand]
    private void PlayAll()
    {
        var tracks = Tracks.ToList();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void PlayFrom(Track track)
    {
        var tracks = Tracks.ToList();
        var idx = tracks.IndexOf(track);
        if (idx < 0) idx = 0;
        _player.ReplaceQueueAndPlay(tracks, idx);
    }

    [RelayCommand]
    private async Task RemoveTrack(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks.ToList() : new List<Track> { track };
        foreach (var t in tracks)
        {
            var displayIdx = Tracks.IndexOf(t);
            if (displayIdx >= 0)
                Tracks.RemoveAt(displayIdx);
            _playlist.TrackIds.Remove(t.Id);
        }
        _playlist.ModifiedAt = DateTime.UtcNow;
        TrackCount = Tracks.Count;

        var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        TotalDuration = total.TotalHours >= 1
            ? $"{(int)total.TotalHours}h {total.Minutes}m"
            : $"{(int)total.TotalMinutes} min";
        RebuildFeaturedArtists(Tracks.ToList());

        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
        CtrlSelectedTracks.Clear();
    }

    public async Task MoveTrack(int fromIndex, int toIndex)
    {
        if (IsSmartPlaylist) return;
        if (fromIndex < 0 || fromIndex >= Tracks.Count) return;
        if (toIndex < 0 || toIndex >= Tracks.Count) return;
        if (fromIndex == toIndex) return;

        Tracks.Move(fromIndex, toIndex);

        // Rebuild TrackIds to match new order
        _playlist.TrackIds.Clear();
        foreach (var t in Tracks)
            _playlist.TrackIds.Add(t.Id);
        _playlist.ModifiedAt = DateTime.UtcNow;

        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private void ShuffleAll()
    {
        var tracks = Tracks.ToList();
        if (tracks.Count == 0) return;
        var shuffled = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedTracks.Clear();
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
        await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
        CtrlSelectedTracks.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
        foreach (var t in tracks)
            t.IsFavorite = !t.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        CtrlSelectedTracks.Clear();
    }

    [RelayCommand]
    private void ViewAlbum(Track track)
    {
        ViewAlbumRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    private Action<IReadOnlyList<PlaylistFeaturedArtist>>? _openFeaturedArtistsAction;
    public void SetOpenFeaturedArtistsAction(Action<IReadOnlyList<PlaylistFeaturedArtist>> action) => _openFeaturedArtistsAction = action;

    [RelayCommand]
    private void OpenFeaturedArtists()
    {
        if (FeaturedArtists.Count > 0)
            _openFeaturedArtistsAction?.Invoke(FeaturedArtists.ToList());
    }

    // ── Description ──────────────────────────────────────────

    partial void OnPlaylistDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasDescriptionChanges));
    }

    partial void OnDescriptionEditorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDescriptionChanges));
    }

    [RelayCommand]
    private async Task OpenDescription()
    {
        DescriptionEditorText = PlaylistDescription;
        IsDescriptionEditing = false;
        await Views.PlaylistDescriptionDialog.ShowAsync(this);
        IsDescriptionEditing = false;
    }

    [RelayCommand]
    private void CloseDescription()
    {
        IsDescriptionEditing = false;
        IsDescriptionOpen = false;
    }

    [RelayCommand]
    private void StartDescriptionEdit()
    {
        DescriptionEditorText = PlaylistDescription;
        IsDescriptionEditing = true;
    }

    [RelayCommand]
    private async Task SaveDescriptionEdit()
    {
        var edited = (DescriptionEditorText ?? string.Empty).Trim();
        _playlist.Description = edited;
        _playlist.ModifiedAt = DateTime.UtcNow;
        PlaylistDescription = edited;
        IsDescriptionEditing = false;
        IsDescriptionOpen = false;
        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
    }

    [RelayCommand]
    private void CancelDescriptionEdit()
    {
        DescriptionEditorText = PlaylistDescription;
        IsDescriptionEditing = false;
    }

    // ── Playlist-level actions (3-dot menu) ──────────────────

    [RelayCommand]
    private void PlayNextAll()
    {
        var tracks = Tracks.ToList();
        if (tracks.Count == 0) return;
        for (int i = tracks.Count - 1; i >= 0; i--)
            _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddAllToQueue()
    {
        _player.AddRangeToQueue(Tracks.ToList());
    }

    [RelayCommand]
    private async Task EditPlaylist()
    {
        await _sidebar.EditPlaylistAsync(_playlist);
        // Refresh after edit
        Name = _playlist.Name;
        PlaylistDescription = _playlist.Description ?? string.Empty;
        OnPropertyChanged(nameof(PlaylistColor));
        OnPropertyChanged(nameof(PlaylistCoverArtPath));
    }

    [RelayCommand]
    private async Task DeletePlaylist()
    {
        var confirmed = await Views.ConfirmationDialog.ShowAsync($"Are you sure you want to delete \"{_playlist.Name}\"? This cannot be undone.");
        if (!confirmed) return;
        await _sidebar.DeletePlaylistAsync(_playlist.Id);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void GoBack()
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= _playerPropertyChangedHandler;
        _sidebar.PlaylistTracksChanged -= OnPlaylistTracksChanged;
        if (_playlist.IsSmartPlaylist)
            _library.LibraryUpdated -= OnLibraryUpdated;
    }
}

public partial class PlaylistFeaturedArtist : ObservableObject
{
    public PlaylistFeaturedArtist(string name, int trackCount, string? imagePath)
    {
        Name = name;
        TrackCount = trackCount;
        ImagePath = imagePath;
    }

    public string Name { get; }
    public int TrackCount { get; }
    [ObservableProperty] private string? _imagePath;
}
