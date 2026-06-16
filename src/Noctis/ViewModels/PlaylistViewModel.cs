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

    /// <summary>View-only sort applied to the displayed track list (does not change saved order).</summary>
    [ObservableProperty] private PlaylistSortMode _sortMode = PlaylistSortMode.Manual;

    public bool IsManualSort => SortMode == PlaylistSortMode.Manual;
    public string SortLabel => SortMode switch
    {
        PlaylistSortMode.Title => "Title",
        PlaylistSortMode.Artist => "Artist",
        PlaylistSortMode.Album => "Album",
        PlaylistSortMode.Duration => "Duration",
        PlaylistSortMode.RecentlyAdded => "Recently Added",
        _ => "Manual"
    };

    partial void OnSortModeChanged(PlaylistSortMode value)
    {
        OnPropertyChanged(nameof(IsManualSort));
        OnPropertyChanged(nameof(SortLabel));
        LoadTracks();
    }

    /// <summary>Empty-state flags: which overlay to show when the track list is empty.</summary>
    [ObservableProperty] private bool _showEmptyManual;
    [ObservableProperty] private bool _showEmptySmart;
    [ObservableProperty] private bool _showNoResults;

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

    /// <summary>Image used for the ambient blurred backdrop (custom cover, else first collage art).</summary>
    public string? BackdropArtPath => HasCustomArt ? PlaylistCoverArtPath : Art1;

    /// <summary>Resolved tracks in this playlist (order matches playlist).</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    /// <summary>Unique artists represented by tracks in this playlist.</summary>
    public ObservableCollection<PlaylistFeaturedArtist> FeaturedArtists { get; } = new();
    public bool HasFeaturedArtists => FeaturedArtists.Count > 0;
    public string? FirstFeaturedArtistName => FeaturedArtists.FirstOrDefault()?.Name;

    /// <summary>Subset shown stacked in the left rail; the header chevron opens the full list.</summary>
    public IEnumerable<PlaylistFeaturedArtist> TopFeaturedArtists => FeaturedArtists.Take(5);

    /// <summary>Fires when the user wants to go back to the previous view.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Fires when the user wants to view an album from a track.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

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

    /// <summary>
    /// True when the track matches the "Find in Playlist" query. Matches Title, Artist,
    /// or Album (all visible columns) case-insensitively. A blank query matches everything.
    /// </summary>
    public static bool MatchesSearch(Track track, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Noctis.Helpers.SearchText.Matches(track.Title, query)
            || Noctis.Helpers.SearchText.Matches(track.Artist, query)
            || Noctis.Helpers.SearchText.Matches(track.Album, query);
    }

    [RelayCommand]
    private void SetSort(string mode)
    {
        if (Enum.TryParse<PlaylistSortMode>(mode, ignoreCase: true, out var parsed))
            SortMode = parsed;
    }

    /// <summary>Pure view-only sort for the displayed list. Manual preserves the playlist order.</summary>
    public static IReadOnlyList<Track> SortTracks(IReadOnlyList<Track> tracks, PlaylistSortMode mode)
    {
        if (tracks == null) return Array.Empty<Track>();
        return mode switch
        {
            PlaylistSortMode.Title => tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            PlaylistSortMode.Artist => tracks.OrderBy(t => t.PrimaryArtist, StringComparer.OrdinalIgnoreCase)
                                             .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            PlaylistSortMode.Album => tracks.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
                                            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList(),
            PlaylistSortMode.Duration => tracks.OrderBy(t => t.Duration).ToList(),
            PlaylistSortMode.RecentlyAdded => tracks.OrderByDescending(t => t.DateAdded).ToList(),
            _ => tracks
        };
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
            resolved = allResolvedTracks.Where(t => MatchesSearch(t, _currentFilter));
        }
        else
        {
            resolved = allResolvedTracks;
        }

        foreach (var track in SortTracks(resolved.ToList(), SortMode))
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

        UpdateEmptyStateFlags(allResolvedTracks.Count);

        RebuildFeaturedArtists(allResolvedTracks);
    }

    /// <param name="unfilteredCount">Track count before the search filter was applied.</param>
    private void UpdateEmptyStateFlags(int unfilteredCount)
    {
        var filterActive = !string.IsNullOrWhiteSpace(_currentFilter);
        ShowNoResults = Tracks.Count == 0 && filterActive && unfilteredCount > 0;
        ShowEmptyManual = Tracks.Count == 0 && !ShowNoResults && !IsSmartPlaylist;
        ShowEmptySmart = Tracks.Count == 0 && !ShowNoResults && IsSmartPlaylist;
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
            OnPropertyChanged(nameof(TopFeaturedArtists));
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
        OnPropertyChanged(nameof(TopFeaturedArtists));

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
        UpdateEmptyStateFlags(_playlist.TrackIds.Count);
        RebuildFeaturedArtists(Tracks.ToList());

        await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
        CtrlSelectedTracks.Clear();
    }

    public async Task MoveTrack(int fromIndex, int toIndex)
    {
        if (IsSmartPlaylist) return;
        // Tracks is the filtered view while a search is active; rebuilding
        // TrackIds from it would silently delete every non-matching track.
        if (!string.IsNullOrWhiteSpace(_currentFilter)) return;
        // Reordering only makes sense in Manual sort — otherwise the displayed
        // order isn't the saved order and persisting it would scramble the playlist.
        if (SortMode != PlaylistSortMode.Manual) return;
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
    private void StartRadio(Track track) => _player.StartRadioCommand.Execute(track);

    [RelayCommand]
    private void SnoozeForMonth(Track track) => _player.SnoozeForMonthCommand.Execute(track);

    [RelayCommand]
    private void ShuffleAll()
    {
        var tracks = Tracks.ToList();
        if (tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);
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
    private async Task OpenMetadata(Track track)
    {
        if (CtrlSelectedTracks.Count > 1)
        {
            var sel = CtrlSelectedTracks.ToList();
            CtrlSelectedTracks.Clear();
            await MetadataHelper.OpenBatchMetadataWindow(sel);
        }
        else
        {
            await MetadataHelper.OpenMetadataWindow(track);
        }
    }

    [RelayCommand]
    private async Task ConvertTracks(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks.ToList() : new List<Track> { track };
        CtrlSelectedTracks.Clear();
        await MetadataHelper.OpenAudioConverterDialog(tracks);
    }

    [RelayCommand]
    private async Task ScanReplayGain(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks.ToList() : new List<Track> { track };
        CtrlSelectedTracks.Clear();
        await MetadataHelper.OpenReplayGainScannerDialog(tracks);
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

    /// <summary>Toggles favorite for a single track only (used by the inline row heart),
    /// independent of any multi-selection so a heart click never affects other rows.
    /// Kept synchronous: an async command would disable every heart button (they share this
    /// one command instance) while the save awaits, flickering all hearts. The bool flip
    /// updates the bound heart instantly; persistence runs in the background.</summary>
    [RelayCommand]
    private void ToggleFavoriteSingle(Track track)
    {
        if (track == null) return;
        track.IsFavorite = !track.IsFavorite;
        _ = PersistFavoriteChangeAsync();
    }

    private async Task PersistFavoriteChangeAsync()
    {
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
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

    /// <summary>Opens the search-driven library picker to add songs to this (manual) playlist.</summary>
    [RelayCommand]
    private async Task AddSongs()
    {
        if (IsSmartPlaylist) return;
        await _sidebar.OpenAddSongsAsync(_playlist);
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
        OnPropertyChanged(nameof(BackdropArtPath));
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

/// <summary>View-only display order for a playlist's track list.</summary>
public enum PlaylistSortMode
{
    Manual,
    Title,
    Artist,
    Album,
    Duration,
    RecentlyAdded
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
