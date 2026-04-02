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
/// ViewModel for the flat "Songs" view — shows all tracks in a sortable table.
/// </summary>
public partial class LibrarySongsViewModel : ViewModelBase, ISearchable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly SidebarViewModel _sidebar;
    private readonly IPersistenceService _persistence;

    private List<Track> _allTracks = new();
    private string _currentFilter = string.Empty;
    private int _filterGeneration;
    private DispatcherTimer? _searchDebounce;
    private EventHandler? _libraryUpdatedHandler;
    private bool _isDirty = true;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _sortColumn = "Date Added";
    [ObservableProperty] private bool _sortAscending = false;
    [ObservableProperty] private bool _showOnlyFavorites = false;
    [ObservableProperty] private bool _isFilterMenuOpen = false;

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Tracks currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Track> CtrlSelectedTracks { get; set; } = new();

    /// <summary>Filtered and sorted tracks displayed in the DataGrid.</summary>
    public BulkObservableCollection<Track> FilteredTracks { get; } = new();

    /// <summary>Currently selected tracks (for multi-select and drag).</summary>
    public ObservableCollection<Track> SelectedTracks { get; } = new();

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    /// <summary>Fires when the user wants to view an album from a track.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    public LibrarySongsViewModel(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar, IPersistenceService persistence)
    {
        _library = library;
        _player = player;
        _sidebar = sidebar;
        _persistence = persistence;

        // Mark dirty when library changes — actual reload deferred to next Refresh() call
        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Reloads tracks from the library service. Skips if data hasn't changed.</summary>
    public void Refresh()
    {
        if (!_isDirty && FilteredTracks.Count > 0)
            return;

        _isDirty = false;
        ApplyFilterAndSort(refreshFromLibrary: true);
    }

    public void ApplyFilter(string query)
    {
        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));
        ApplyFilterAndSort();
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
    private void ToggleFilterMenu()
    {
        IsFilterMenuOpen = !IsFilterMenuOpen;
    }

    [RelayCommand]
    private void SetShowAllItems()
    {
        ShowOnlyFavorites = false;
        IsFilterMenuOpen = false;
        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void SetShowOnlyFavorites()
    {
        ShowOnlyFavorites = true;
        IsFilterMenuOpen = false;
        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void Sort(string column)
    {
        // Handle Ascending/Descending from filter menu
        if (column == "Ascending")
        {
            SortAscending = true;
            IsFilterMenuOpen = false;
            ApplyFilterAndSort();
            return;
        }
        if (column == "Descending")
        {
            SortAscending = false;
            IsFilterMenuOpen = false;
            ApplyFilterAndSort();
            return;
        }

        // Handle column sorting
        if (SortColumn == column)
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        IsFilterMenuOpen = false;
        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void PlayFromHere(Track track)
    {
        var tracks = FilteredTracks.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;

        _player.ReplaceQueueAndPlay(tracks, index);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        var tracks = FilteredTracks.ToList();
        if (tracks.Count == 0) return;

        // Shuffle the list using thread-safe Random.Shared
        var shuffled = tracks.OrderBy(_ => Random.Shared.Next()).ToList();

        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

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
    private async Task RemoveFromLibrary(Track track)
    {
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
        var ids = tracks.Select(t => t.Id).ToList();
        await _library.RemoveTracksAsync(ids);
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

    private async void ApplyFilterAndSort(bool refreshFromLibrary = false)
    {
        try
        {
            var generation = Interlocked.Increment(ref _filterGeneration);

            // Capture all state needed for filtering/sorting
            var filter = _currentFilter;
            var sortCol = SortColumn;
            var sortAsc = SortAscending;
            var favOnly = ShowOnlyFavorites;
            var tracks = _allTracks;
            var library = refreshFromLibrary ? _library : null;

            // Run the heavy filter/sort work on a background thread
            var result = await Task.Run(() =>
            {
                // Move ToList() off the UI thread when refreshing from library
                if (library != null)
                    tracks = library.Tracks.ToList();

                var filtered = tracks.AsEnumerable();

                // Apply favorites filter
                if (favOnly)
                    filtered = filtered.Where(t => t.IsFavorite);

                // Apply search filter
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    var q = filter.Trim();
                    var qNoSpaces = RemoveWhitespace(q);
                    filtered = filtered.Where(t =>
                        MatchesSearch(t.Title, q, qNoSpaces) ||
                        MatchesSearch(t.Artist, q, qNoSpaces) ||
                        MatchesSearch(t.Album, q, qNoSpaces));
                }

                var hasQuery = !string.IsNullOrWhiteSpace(filter);
                var ranked = filtered
                    .Select(t => new
                    {
                        Track = t,
                        Rank = hasQuery ? GetTrackSearchRank(t, filter.Trim(), RemoveWhitespace(filter.Trim())) : 0
                    });

                var ordered = ranked.OrderBy(x => x.Rank);
                ordered = sortCol switch
                {
                    "Title" => sortAsc ? ordered.ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Title),
                    "Time" => sortAsc ? ordered.ThenBy(x => x.Track.Duration) : ordered.ThenByDescending(x => x.Track.Duration),
                    "Artist" => sortAsc ? ordered.ThenBy(x => x.Track.Artist).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Artist).ThenBy(x => x.Track.Title),
                    "Album" => sortAsc ? ordered.ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber) : ordered.ThenByDescending(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber),
                    "Genre" => sortAsc ? ordered.ThenBy(x => x.Track.Genre).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Genre).ThenBy(x => x.Track.Title),
                    "Plays" => sortAsc ? ordered.ThenBy(x => x.Track.PlayCount) : ordered.ThenByDescending(x => x.Track.PlayCount),
                    "Duration" => sortAsc ? ordered.ThenBy(x => x.Track.Duration) : ordered.ThenByDescending(x => x.Track.Duration),
                    "Date Added" => sortAsc ? ordered.ThenBy(x => x.Track.DateAdded) : ordered.ThenByDescending(x => x.Track.DateAdded),
                    _ => ordered.ThenBy(x => x.Track.Title)
                };

                return ordered.Select(x => x.Track).ToList();
            });

            // Discard stale results if a newer filter/sort has been requested
            if (generation != _filterGeneration) return;

            // Save refreshed tracks list back (already on UI thread)
            if (refreshFromLibrary)
                _allTracks = tracks;

            FilteredTracks.ReplaceAll(result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SongsVM] Filter/sort failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Stop and dispose search debounce timer
        if (_searchDebounce != null)
        {
            _searchDebounce.Stop();
            _searchDebounce = null;
        }

        // Unsubscribe from library events
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }

    private static bool MatchesSearch(string? source, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        // Single substring match (fast path)
        if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (RemoveWhitespace(source).Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return true;

        // Word-level match: every word in the query must appear somewhere in the source
        return MatchesAllWords(source, query);
    }

    private static bool MatchesAllWords(string source, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
            return false;

        foreach (var word in words)
        {
            if (!source.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static int GetTrackSearchRank(Track track, string query, string queryNoSpaces)
    {
        var titleRank = RankMatch(track.Title, query, queryNoSpaces);
        var artistRank = RankMatch(track.Artist, query, queryNoSpaces);
        var albumRank = RankMatch(track.Album, query, queryNoSpaces);

        return Math.Min(titleRank, Math.Min(artistRank + 20, albumRank + 40));
    }

    private static int RankMatch(string? source, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 1000;

        var normalized = source.Trim();
        var normalizedNoSpaces = RemoveWhitespace(normalized);

        if (string.Equals(normalized, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedNoSpaces, queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (normalized.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            normalizedNoSpaces.StartsWith(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (normalizedNoSpaces.Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 3;

        // Word-level match: all query words found in source
        if (MatchesAllWords(normalized, query))
            return 4;

        return 1000;
    }

    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    }
}
