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

    /// <summary>Quality filter for the top-bar chips: "All", "Lossless" or "HiRes".</summary>
    [ObservableProperty] private string _qualityFilter = "All";

    /// <summary>"2,144 songs · 138 hours" line for the top bar; reflects the current filters.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Tracks currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Track> CtrlSelectedTracks { get; set; } = new();

    /// <summary>Filtered and sorted tracks displayed in the DataGrid.</summary>
    public BulkObservableCollection<Track> FilteredTracks { get; } = new();

    /// <summary>Currently selected tracks (for multi-select and drag).</summary>
    public ObservableCollection<Track> SelectedTracks { get; } = new();

    /// <summary>Fires when the user wants to view an album from a track.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    public LibrarySongsViewModel(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar, IPersistenceService persistence)
    {
        _library = library;
        _player = player;
        _sidebar = sidebar;
        _persistence = persistence;

        // Mark dirty when library changes — actual reload deferred to next Refresh() call.
        // Only rebuild immediately while this view is current: a scan fires LibraryUpdated
        // every ~1.5 s, and rebuilding a 40k-100k row list for a hidden view burns CPU for
        // nothing. Hidden views catch up once via the dirty flag when they become active.
        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            if (_isActive)
                Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    /// <summary>
    /// Set by MainWindowViewModel when Songs becomes (or stops being) the current view.
    /// Mirrors CoverFlowViewModel.IsActive: gates LibraryUpdated-driven rebuilds while
    /// hidden, and catches up on activation (no-op when nothing changed).
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            // Catch up on anything missed while hidden — covers back-navigation paths
            // that swap CurrentView without going through RefreshAndReturnSongs.
            if (value) Refresh();
        }
    }

    private bool _isActive;

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Reloads tracks from the library service. Skips if data hasn't changed.</summary>
    public void Refresh()
    {
        if (!_isDirty && FilteredTracks.Count > 0)
            return;

        _isDirty = false;

        // Off-UI-thread rebuild via the same generation-guarded path as keystroke
        // filtering. The sync rebuild this replaces froze the UI thread 30-250 ms per
        // navigation/scan event at 40k-100k tracks; the view keeps showing its previous
        // rows for the few frames the fresh list takes to compute in the background.
        ApplyFilterAndSort(refreshFromLibrary: true);
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

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
    private void SetQualityFilter(string quality)
    {
        if (QualityFilter == quality) return;
        QualityFilter = quality;
        ApplyFilterAndSort();
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
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);

        _player.ReplaceQueueAndPlay(shuffled, 0);
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
    private async Task AddToNewPlaylist(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedTracks.Clear();
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks.ToList() : new List<Track> { track };
        if (!await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, tracks))
            return;
        CtrlSelectedTracks.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        if (CtrlSelectedTracks.Count > 1)
        {
            var selection = CtrlSelectedTracks.ToList();
            CtrlSelectedTracks.Clear();
            await MetadataHelper.OpenBatchMetadataWindow(selection);
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
        await _library.SaveTrackUserStateAsync(tracks);
        _library.NotifyFavoritesChanged(tracks);
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
            var quality = QualityFilter;
            var tracks = _allTracks;
            var library = refreshFromLibrary ? _library : null;

            // Run the heavy filter/sort work on a background thread
            var result = await Task.Run(() =>
            {
                // Move ToList() off the UI thread when refreshing from library
                if (library != null)
                    tracks = library.Tracks.ToList();

                return BuildFilteredAndSortedTracks(tracks, filter, sortCol, sortAsc, favOnly, quality);
            });

            // Discard stale results if a newer filter/sort has been requested. A
            // superseded library reload must re-mark dirty: the newer request may have
            // captured the pre-reload _allTracks snapshot.
            if (generation != _filterGeneration)
            {
                if (refreshFromLibrary) _isDirty = true;
                return;
            }

            // Save refreshed tracks list back (already on UI thread)
            if (refreshFromLibrary)
                _allTracks = tracks;

            FilteredTracks.ReplaceAll(result);
            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SongsVM] Filter/sort failed: {ex.Message}");
        }
    }

    private void UpdateSummaryText()
    {
        var count = FilteredTracks.Count;
        var total = TimeSpan.Zero;
        foreach (var t in FilteredTracks)
            total += t.Duration;

        var songs = count == 1 ? "song" : "songs";
        string time;
        if (total.TotalHours >= 1)
        {
            var hours = (int)Math.Round(total.TotalHours);
            time = hours == 1 ? "1 hour" : $"{hours:N0} hours";
        }
        else
        {
            var minutes = (int)Math.Round(total.TotalMinutes);
            time = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        SummaryText = $"{count:N0} {songs} · {time}";
    }

    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static List<Track> BuildFilteredAndSortedTracks(
        List<Track> tracks, string filter, string sortCol, bool sortAsc, bool favOnly, string qualityFilter)
    {
        var filtered = tracks.AsEnumerable();

        if (favOnly)
            filtered = filtered.Where(t => t.IsFavorite);

        filtered = qualityFilter switch
        {
            "Lossless" => filtered.Where(t => t.IsLossless),
            "HiRes" => filtered.Where(t => t.IsHiResLossless),
            _ => filtered,
        };

        // Normalize the query once — these were recomputed per track inside the
        // rank projection below, costing two string allocations per track per
        // keystroke on large libraries.
        var hasQuery = !string.IsNullOrWhiteSpace(filter);
        var q = hasQuery ? filter.Trim() : string.Empty;
        var qNoSpaces = hasQuery ? RemoveWhitespace(q) : string.Empty;

        if (hasQuery)
        {
            filtered = filtered.Where(t =>
                MatchesSearch(t.Title, t.SearchTitleKey, q, qNoSpaces) ||
                MatchesSearch(t.Artist, t.SearchArtistKey, q, qNoSpaces) ||
                MatchesSearch(t.Album, t.SearchAlbumKey, q, qNoSpaces));
        }

        var ranked = filtered
            .Select(t => new
            {
                Track = t,
                Rank = hasQuery ? GetTrackSearchRank(t, q, qNoSpaces) : 0
            });

        var ordered = ranked.OrderBy(x => x.Rank);
        ordered = sortCol switch
        {
            "Title" => sortAsc ? ordered.ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Title),
            "Time" => sortAsc ? ordered.ThenBy(x => x.Track.Duration) : ordered.ThenByDescending(x => x.Track.Duration),
            "Artist" => sortAsc ? ordered.ThenBy(x => x.Track.Artist).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Artist).ThenBy(x => x.Track.Title),
            // Album Artist/Year ordering (Apple Music/MusicBee): albums grouped under
            // their album artist, chronological within the artist, tracks in album order.
            "Album Artist" => sortAsc
                ? ordered.ThenBy(x => AlbumArtistSortKey(x.Track)).ThenBy(x => x.Track.Year).ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber)
                : ordered.ThenByDescending(x => AlbumArtistSortKey(x.Track)).ThenBy(x => x.Track.Year).ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber),
            "Album" => sortAsc ? ordered.ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber) : ordered.ThenByDescending(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber),
            "Genre" => sortAsc ? ordered.ThenBy(x => x.Track.Genre).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Genre).ThenBy(x => x.Track.Title),
            "Year" => sortAsc ? ordered.ThenBy(x => x.Track.Year).ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber) : ordered.ThenByDescending(x => x.Track.Year).ThenBy(x => x.Track.Album).ThenBy(x => x.Track.TrackNumber),
            "Plays" => sortAsc ? ordered.ThenBy(x => x.Track.PlayCount) : ordered.ThenByDescending(x => x.Track.PlayCount),
            // First click (ascending) groups favorites at the top — that's what
            // clicking a "Favorites" header is for; the second click flips it.
            "IsFavorite" => sortAsc ? ordered.ThenByDescending(x => x.Track.IsFavorite).ThenBy(x => x.Track.Title) : ordered.ThenBy(x => x.Track.IsFavorite).ThenBy(x => x.Track.Title),
            "Rating" => sortAsc ? ordered.ThenBy(x => x.Track.Rating).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Rating).ThenBy(x => x.Track.Title),
            "Bpm" => sortAsc ? ordered.ThenBy(x => x.Track.Bpm).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Bpm).ThenBy(x => x.Track.Title),
            "Bitrate" => sortAsc ? ordered.ThenBy(x => x.Track.Bitrate).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.Bitrate).ThenBy(x => x.Track.Title),
            "SampleRate" => sortAsc ? ordered.ThenBy(x => x.Track.SampleRate).ThenBy(x => x.Track.Title) : ordered.ThenByDescending(x => x.Track.SampleRate).ThenBy(x => x.Track.Title),
            "Duration" => sortAsc ? ordered.ThenBy(x => x.Track.Duration) : ordered.ThenByDescending(x => x.Track.Duration),
            "Date Added" => sortAsc ? ordered.ThenBy(x => x.Track.DateAdded) : ordered.ThenByDescending(x => x.Track.DateAdded),
            _ => ordered.ThenBy(x => x.Track.Title)
        };

        return ordered.Select(x => x.Track).ToList();
    }

    /// <summary>
    /// Album-artist sort key. The scanner already resolves an empty album-artist tag
    /// to the performer (see Track.ResolveAlbumArtist), but tracks from older caches
    /// or other construction paths may miss that step — fall back to the track artist
    /// so they still group under someone, matching Apple Music/MusicBee.
    /// </summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static string AlbumArtistSortKey(Track track) =>
        string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;

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

    // sourceKey is the track's cached SearchText.Normalize key (Track.SearchTitleKey etc.).
    // Match and rank used to re-normalize Title/Artist/Album per track per keystroke —
    // up to six throwaway strings per matching track, hundreds of thousands of
    // allocations per keystroke at 100k tracks. The cached key is allocation-free here.
    private static bool MatchesSearch(string? source, string sourceKey, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        // Single substring match (fast path)
        if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (sourceKey.Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
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
        var titleRank = RankMatch(track.Title, track.SearchTitleKey, query, queryNoSpaces);
        var artistRank = RankMatch(track.Artist, track.SearchArtistKey, query, queryNoSpaces);
        var albumRank = RankMatch(track.Album, track.SearchAlbumKey, query, queryNoSpaces);

        return Math.Min(titleRank, Math.Min(artistRank + 20, albumRank + 40));
    }

    private static int RankMatch(string? source, string sourceKey, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 1000;

        // Normalize strips whitespace, so the cached key equals the old
        // RemoveWhitespace(source.Trim()) result without the per-call allocations.
        var normalized = source.Trim();

        if (string.Equals(normalized, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceKey, queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (normalized.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            sourceKey.StartsWith(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (sourceKey.Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
            return 3;

        // Word-level match: all query words found in source
        if (MatchesAllWords(normalized, query))
            return 4;

        return 1000;
    }

    // Normalizes a value into a comparable search key: strips whitespace, punctuation
    // (e.g. the apostrophe in "Don't") and accents so queries match regardless. Name kept
    // for its call sites; see Helpers/SearchText for the shared implementation.
    private static string RemoveWhitespace(string value) => Noctis.Helpers.SearchText.Normalize(value);
}
