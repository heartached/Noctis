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
/// ViewModel for the album grid view.
/// Displays albums as artwork tiles in a virtualized row-based grid.
/// </summary>
public partial class LibraryAlbumsViewModel : ViewModelBase, ISearchable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly SidebarViewModel _sidebar;

    private List<Album> _allAlbums = new();
    private string _currentFilter = string.Empty;
    private DispatcherTimer? _searchDebounce;
    private int _rebuildGeneration;
    private bool _isDirty = true;

    private const int ColumnsPerRow = 5;
    private const double TileTextHeight = 64;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private double _tileArtworkSize = 180;
    [ObservableProperty] private string _artistFilterName = string.Empty;
    public double TileRowHeight => TileArtworkSize + TileTextHeight;

    partial void OnTileArtworkSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TileRowHeight));
    }
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>Whether the view is filtered to a specific artist's discography.</summary>
    public bool IsArtistFiltered => !string.IsNullOrEmpty(ArtistFilterName);

    /// <summary>Dynamic header: artist name when filtered, "Albums" otherwise.</summary>
    public string HeaderText => IsArtistFiltered ? ArtistFilterName : "Albums";

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Last saved scroll offset for the unfiltered album grid.</summary>
    public double SavedUnfilteredScrollOffset { get; private set; }

    /// <summary>Albums currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Album> CtrlSelectedAlbums { get; set; } = new();

    /// <summary>Filtered albums grouped into rows for the virtualized grid.</summary>
    public BulkObservableCollection<AlbumRow> FilteredAlbumRows { get; } = new();

    /// <summary>Fires when the user wants to open an album's detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Fires when the user clicks the back button in artist filter mode.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public LibraryAlbumsViewModel(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar)
    {
        _library = library;
        _player = player;
        _sidebar = sidebar;

        // Mark dirty when library changes — actual reload deferred to next Refresh() call
        _library.LibraryUpdated += (_, _) =>
        {
            _isDirty = true;
            Dispatcher.UIThread.Post(Refresh);
        };
    }

    partial void OnArtistFilterNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsArtistFiltered));
        OnPropertyChanged(nameof(HeaderText));
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    public void Refresh()
    {
        if (!_isDirty && FilteredAlbumRows.Count > 0)
            return;

        _isDirty = false;

        // Sync rebuild: this is invoked from the navigation path (e.g. sidebar
        // "Albums" click) which sets CurrentView immediately after, so the bound
        // FilteredAlbumRows must be current on first paint to avoid flashing the
        // previous filter's content for a frame.
        Interlocked.Increment(ref _rebuildGeneration);
        var albums = _library.Albums.ToList();
        var rows = BuildFilteredRows(albums, ArtistFilterName, _currentFilter, ColumnsPerRow);
        _allAlbums = albums;
        FilteredAlbumRows.ReplaceAll(rows);
    }

    /// <summary>Sets the artist filter for showing a specific artist's discography.</summary>
    public void SetArtistFilter(string artistName)
    {
        if (!IsArtistFiltered && !HasActiveFilter && SavedScrollOffset > 0)
            SavedUnfilteredScrollOffset = SavedScrollOffset;

        ArtistFilterName = artistName;
        _currentFilter = string.Empty;
        OnPropertyChanged(nameof(HasActiveFilter));
        SearchText = string.Empty;

        // Reset saved scroll offset so the view doesn't try to restore
        // a stale position from a previous full-grid visit (which hides
        // the ListBox at Opacity=0 while waiting for matching extent).
        SavedScrollOffset = 0;

        // Refresh album data only if it hasn't been loaded yet;
        // ongoing library changes are handled by the LibraryUpdated handler.
        if (_allAlbums.Count == 0)
            _allAlbums = _library.Albums.ToList();

        // Rebuild synchronously: the caller (artist-link navigation) sets CurrentView
        // immediately after this returns, so the view must already hold the filtered
        // rows on first paint — otherwise the grid flashes the previous (unfiltered)
        // album list for a frame before the async rebuild's UI post lands.
        Interlocked.Increment(ref _rebuildGeneration);
        var rows = BuildFilteredRows(_allAlbums, ArtistFilterName, _currentFilter, ColumnsPerRow);
        FilteredAlbumRows.ReplaceAll(rows);
    }

    /// <summary>Clears the artist filter (when navigating back to all albums).</summary>
    public void ClearArtistFilter()
    {
        // Mark dirty if any filter was active so Refresh() rebuilds with cleared state
        if (!string.IsNullOrEmpty(ArtistFilterName) || !string.IsNullOrEmpty(_currentFilter))
            _isDirty = true;

        ArtistFilterName = string.Empty;
        _currentFilter = string.Empty;
        OnPropertyChanged(nameof(HasActiveFilter));
        SearchText = string.Empty;

        if (SavedUnfilteredScrollOffset > 0)
            SavedScrollOffset = SavedUnfilteredScrollOffset;
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));
        RebuildFilteredRows();
    }

    /// <summary>
    /// Sync variant of <see cref="ApplyFilter"/>. Used by navigation paths (back-restore,
    /// section-restore) where CurrentView is swapped to this VM immediately after the
    /// call returns; the async path would let the view paint the previous filter's rows
    /// for one frame before the rebuild lands.
    /// </summary>
    public void ApplyFilterImmediate(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));

        Interlocked.Increment(ref _rebuildGeneration);
        var rows = BuildFilteredRows(_allAlbums, ArtistFilterName, _currentFilter, ColumnsPerRow);
        FilteredAlbumRows.ReplaceAll(rows);
    }

    private void RebuildFilteredRows()
    {
        // Capture state for the background task
        var generation = Interlocked.Increment(ref _rebuildGeneration);
        var albums = _allAlbums;
        var artistFilter = ArtistFilterName;
        var searchFilter = _currentFilter;
        var columns = ColumnsPerRow;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var rows = BuildFilteredRows(albums, artistFilter, searchFilter, columns);

            // Only apply if no newer rebuild was requested
            if (Volatile.Read(ref _rebuildGeneration) == generation)
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _rebuildGeneration) == generation)
                        FilteredAlbumRows.ReplaceAll(rows);
                });
        });
    }

    private List<AlbumRow> BuildFilteredRows(
        List<Album> allAlbums, string artistFilter, string searchFilter, int columnsPerRow)
    {
        var filtered = allAlbums.AsEnumerable();

        // Apply artist filter first (match on album artist or any track artist,
        // including individual collaborators parsed from multi-artist strings)
        if (!string.IsNullOrEmpty(artistFilter))
        {
            filtered = filtered.Where(a =>
                ContainsArtistToken(a.Artist, artistFilter) ||
                a.Tracks.Any(t => ContainsArtistToken(t.Artist, artistFilter)));
        }

        // Apply search filter on top
        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var q = searchFilter.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            filtered = filtered.Where(a =>
                MatchesSearch(a.Name, q, qNoSpaces) ||
                MatchesSearch(a.Artist, q, qNoSpaces) ||
                a.Tracks.Any(t => MatchesSearch(t.Title, q, qNoSpaces) ||
                                  MatchesSearch(t.Artist, q, qNoSpaces)));

            // In artist discographies, show the artist's own releases before feature appearances.
            filtered = filtered
                .OrderBy(a => GetArtistDiscographyRank(a, artistFilter))
                .ThenBy(a => GetAlbumSearchRank(a, q, qNoSpaces))
                .ThenBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }
        else if (!string.IsNullOrEmpty(artistFilter))
        {
            filtered = filtered
                .OrderBy(a => GetArtistDiscographyRank(a, artistFilter))
                .ThenBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        // Float drag-and-drop imported albums to the top when not searching/filtering.
        IEnumerable<Album> ordered;
        if (string.IsNullOrEmpty(artistFilter) && string.IsNullOrWhiteSpace(searchFilter))
        {
            var materialized = filtered.ToList();
            var recent = materialized
                .Where(a => a.Tracks.Any(t => t.IsRecentImport))
                .OrderByDescending(a => a.Tracks.Max(t => t.DateAdded))
                .ToList();
            if (recent.Count > 0)
            {
                var recentIds = new HashSet<Guid>(recent.Select(a => a.Id));
                var rest = materialized.Where(a => !recentIds.Contains(a.Id));
                ordered = recent.Concat(rest);
            }
            else
            {
                ordered = materialized;
            }
        }
        else
        {
            ordered = filtered;
        }

        // Group albums into rows of columnsPerRow for virtualized display
        var rows = new List<AlbumRow>();
        var currentRow = new List<Album>();

        foreach (var album in ordered)
        {
            currentRow.Add(album);
            if (currentRow.Count == columnsPerRow)
            {
                rows.Add(new AlbumRow { Albums = currentRow });
                currentRow = new List<Album>();
            }
        }

        if (currentRow.Count > 0)
            rows.Add(new AlbumRow { Albums = currentRow });

        return rows;
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
    private void GoBack()
    {
        ClearArtistFilter();
        RebuildFilteredRows(); // Rebuild to show all albums after clearing filter
        BackRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Plays all tracks from the current filtered artist view in order.</summary>
    [RelayCommand]
    private void PlayAllArtistTracks()
    {
        var allTracks = GetAllFilteredTracks();
        if (allTracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(allTracks, 0);
    }

    /// <summary>Shuffles and plays all tracks from the current filtered artist view.</summary>
    [RelayCommand]
    private void ShuffleAllArtistTracks()
    {
        var allTracks = GetAllFilteredTracks();
        if (allTracks.Count == 0) return;
        var shuffled = allTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    /// <summary>Collects all tracks from the currently displayed albums.</summary>
    private List<Track> GetAllFilteredTracks()
    {
        var tracks = new List<Track>();
        foreach (var row in FilteredAlbumRows)
        {
            foreach (var album in row.Albums)
            {
                if (album.Tracks != null)
                    tracks.AddRange(album.Tracks);
            }
        }
        return tracks;
    }

    [RelayCommand]
    private void PlayNext(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        // Create a copy to avoid collection modification issues
        var tracks = album.Tracks.ToList();

        // Add tracks in reverse order so they appear in the correct order when inserted at position 0
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            _player.AddNext(tracks[i]);
        }
    }

    [RelayCommand]
    private void AddToQueue(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        _player.AddRangeToQueue(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
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

        // Open metadata for the first track in the album
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0]);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void ShowInExplorer(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var filePath = album.Tracks[0].FilePath;
        if (!File.Exists(filePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(filePath);
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

    private static int GetAlbumSearchRank(Album album, string query, string queryNoSpaces)
    {
        var nameRank = RankMatch(album.Name, query, queryNoSpaces);
        var artistRank = RankMatch(album.Artist, query, queryNoSpaces);
        // Also check individual track artists so featured-artist albums rank properly
        var trackArtistRank = album.Tracks.Count == 0
            ? 1000
            : album.Tracks.Min(t => RankMatch(t.Artist, query, queryNoSpaces));
        var trackTitleRank = album.Tracks.Count == 0
            ? 1000
            : album.Tracks.Min(t => RankMatch(t.Title, query, queryNoSpaces));

        // Artist matches rank equally to name matches for proper grouping
        return Math.Min(nameRank, Math.Min(artistRank, Math.Min(trackArtistRank + 5, trackTitleRank + 40)));
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

    /// <summary>
    /// Checks if an artist field contains the given artist name as one of its
    /// parsed tokens (handles "&amp;", "feat.", etc.), or as an exact match.
    /// Both sides are tokenised so that filtering by "A &amp; B" matches fields
    /// containing either "A" or "B", and vice versa.
    /// </summary>
    private static bool ContainsArtistToken(string? artistField, string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistField))
            return false;

        // Fast path: exact match
        if (artistField.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            return true;

        var fieldTokens = Track.ParseArtistTokens(artistField);
        var filterTokens = Track.ParseArtistTokens(artistName);

        if (filterTokens.Length > 1)
        {
            // Combined artist filter (e.g., "Bad Bunny, Prince Royce & J Balvin"):
            // require exact token set equality so only that specific collaboration matches.
            var fieldSet = new HashSet<string>(fieldTokens, StringComparer.OrdinalIgnoreCase);
            return fieldSet.SetEquals(filterTokens);
        }

        // Single artist filter: match if the token appears anywhere in the field.
        foreach (var ft in filterTokens)
        {
            if (fieldTokens.Any(t => t.Equals(ft, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static int GetArtistDiscographyRank(Album album, string artistFilter)
    {
        if (string.IsNullOrWhiteSpace(artistFilter))
            return 0;

        if (IsExactArtistCredit(album.Artist, artistFilter))
            return 0;

        if (album.Tracks.Any(t => IsExactArtistCredit(t.Artist, artistFilter)))
            return 1;

        if (ContainsArtistToken(album.Artist, artistFilter))
            return 2;

        return 3;
    }

    private static bool IsExactArtistCredit(string? artistField, string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistField) || string.IsNullOrWhiteSpace(artistName))
            return false;

        if (artistField.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            return true;

        var fieldTokens = Track.ParseArtistTokens(artistField);
        var filterTokens = Track.ParseArtistTokens(artistName);

        return fieldTokens.Length > 0
               && filterTokens.Length > 0
               && fieldTokens.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(filterTokens);
    }

    public void Dispose()
    {
        if (_searchDebounce != null)
        {
            _searchDebounce.Stop();
            _searchDebounce = null;
        }
    }

    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    }
}
