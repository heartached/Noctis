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

    private const int ColumnsPerRow = 6;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private double _tileArtworkSize = 180;
    [ObservableProperty] private string _artistFilterName = string.Empty;
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>Whether the view is filtered to a specific artist's discography.</summary>
    public bool IsArtistFiltered => !string.IsNullOrEmpty(ArtistFilterName);

    /// <summary>Dynamic header: artist name when filtered, "Albums" otherwise.</summary>
    public string HeaderText => IsArtistFiltered ? ArtistFilterName : "Albums";

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

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

        // Dispatch to UI thread since scan fires LibraryUpdated from a background thread
        _library.LibraryUpdated += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    partial void OnArtistFilterNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsArtistFiltered));
        OnPropertyChanged(nameof(HeaderText));
    }

    public void Refresh()
    {
        _allAlbums = _library.Albums.ToList();
        RebuildFilteredRows();
    }

    /// <summary>Sets the artist filter for showing a specific artist's discography.</summary>
    public void SetArtistFilter(string artistName)
    {
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

        RebuildFilteredRows();
    }

    /// <summary>Clears the artist filter (when navigating back to all albums).</summary>
    public void ClearArtistFilter()
    {
        ArtistFilterName = string.Empty;
        _currentFilter = string.Empty;
        OnPropertyChanged(nameof(HasActiveFilter));
        SearchText = string.Empty;
    }

    public void ApplyFilter(string query)
    {
        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));
        RebuildFilteredRows();
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

            // Group artist matches together: sort by rank, then artist, then year
            filtered = filtered
                .OrderBy(a => GetAlbumSearchRank(a, q, qNoSpaces))
                .ThenBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        // Group albums into rows of columnsPerRow for virtualized display
        var rows = new List<AlbumRow>();
        var currentRow = new List<Album>();

        foreach (var album in filtered)
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

        // Create a copy to avoid collection modification issues
        var tracks = album.Tracks.ToList();

        foreach (var track in tracks)
        {
            _player.AddToQueue(track);
        }
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        // Create a copy to avoid collection modification issues
        var tracks = album.Tracks.ToList();

        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        if (album.Tracks == null || album.Tracks.Count == 0) return;

        var tracks = album.Tracks.ToList();
        await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        var newState = !album.IsAllTracksFavorite;
        foreach (var track in album.Tracks)
            track.IsFavorite = newState;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
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
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync($"Remove \"{album.Name}\" from your library?"))
            return;
        var trackIds = album.Tracks.Select(t => t.Id).ToList();
        await _library.RemoveTracksAsync(trackIds);
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
