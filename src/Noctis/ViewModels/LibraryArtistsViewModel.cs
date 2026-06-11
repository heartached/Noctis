using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the artists grid view.
/// Shows artists as a virtualized grid of circular portraits: the outer
/// ListBox virtualizes <see cref="ArtistRow"/>s, each row lays out
/// <see cref="ArtistsPerRow"/> portraits in a non-virtualizing UniformGrid.
/// </summary>
public partial class LibraryArtistsViewModel : ViewModelBase, ISearchable, IDisposable
{
    /// <summary>Number of portrait columns per virtualized grid row.</summary>
    public const int ArtistsPerRow = 7;

    private readonly ILibraryService _library;
    private ArtistImageService? _artistImageService;

    private List<Artist> _allArtists = new();
    private string _currentFilter = string.Empty;
    private bool _isDirty = true;
    private DispatcherTimer? _searchDebounce;
    private DispatcherTimer? _imageRefreshDebounce;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Rows of artists for the virtualized grid display.</summary>
    public BulkObservableCollection<ArtistRow> ArtistRows { get; } = new();

    /// <summary>Fires when the user opens a specific artist's page.</summary>
    public event EventHandler<Artist>? ArtistOpened;

    public LibraryArtistsViewModel(ILibraryService library)
    {
        _library = library;

        // Dispatch to UI thread since scan fires LibraryUpdated from a background thread
        _library.LibraryUpdated += (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };
    }

    public void SetArtistImageService(ArtistImageService service) => _artistImageService = service;

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    public void Refresh()
    {
        if (!_isDirty && ArtistRows.Count > 0)
            return;
        _isDirty = false;

        _allArtists = _library.Artists.ToList();
        foreach (var artist in _allArtists)
        {
            if (!string.IsNullOrWhiteSpace(artist.ImagePath) && !File.Exists(artist.ImagePath))
                artist.ImagePath = null;
        }
        ApplyFilter(_currentFilter);

        // Trigger background artist image fetch
        if (_artistImageService != null && _allArtists.Count > 0)
        {
            if (_imageRefreshDebounce == null)
            {
                _imageRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _imageRefreshDebounce.Tick += (_, _) =>
                {
                    _imageRefreshDebounce.Stop();
                    ApplyFilter(_currentFilter);
                };
            }

            _ = _artistImageService.FetchAndCacheAsync(_allArtists, (artist, path) =>
            {
                // Debounce list rebuild — batch image updates every 2 seconds
                Dispatcher.UIThread.Post(() =>
                {
                    _imageRefreshDebounce.Stop();
                    _imageRefreshDebounce.Start();
                });
            });
        }
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));

        IEnumerable<Artist> filtered;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            filtered = _allArtists
                .Where(a => MatchesSearch(a.Name, q, qNoSpaces))
                .OrderBy(a => RankMatch(a.Name, q, qNoSpaces))
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            filtered = _allArtists.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        // Chunk into fixed-width rows so the outer ListBox can virtualize
        var rows = new List<ArtistRow>();
        ArtistRow? row = null;
        foreach (var artist in filtered)
        {
            if (row == null || row.Artists.Count == ArtistsPerRow)
            {
                row = new ArtistRow();
                rows.Add(row);
            }
            row.Artists.Add(artist);
        }

        ArtistRows.ReplaceAll(rows);
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
    private void OpenArtist(Artist artist)
    {
        ArtistOpened?.Invoke(this, artist);
    }

    private static bool MatchesSearch(string? source, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return RemoveWhitespace(source).Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase);
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

        return 1000;
    }

    public void Dispose()
    {
        if (_searchDebounce != null)
        {
            _searchDebounce.Stop();
            _searchDebounce = null;
        }
        if (_imageRefreshDebounce != null)
        {
            _imageRefreshDebounce.Stop();
            _imageRefreshDebounce = null;
        }
    }

    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    }
}
