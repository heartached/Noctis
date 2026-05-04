using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the artists list view.
/// Shows artists in a flat virtualized list with letter headers.
/// </summary>
public partial class LibraryArtistsViewModel : ViewModelBase, ISearchable, IDisposable
{
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

    /// <summary>Flat list of letter headers and artist items for virtualized display.</summary>
    public BulkObservableCollection<ArtistListItem> FlatArtistList { get; } = new();

    /// <summary>Fires when the user wants to view a specific artist's albums.</summary>
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
        if (!_isDirty && FlatArtistList.Count > 0)
            return;
        _isDirty = false;

        _allArtists = _library.Artists.ToList();
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

        var filtered = _allArtists.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            filtered = filtered.Where(a =>
                MatchesSearch(a.Name, q, qNoSpaces));

            filtered = filtered
                .OrderBy(a => RankMatch(a.Name, q, qNoSpaces))
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        // Build flat list with letter headers interleaved
        var items = new List<ArtistListItem>();
        var groups = filtered
            .GroupBy(a =>
            {
                var firstChar = char.ToUpperInvariant(a.Name.FirstOrDefault());
                return char.IsLetter(firstChar) ? firstChar : '#';
            })
            .OrderBy(g => g.Key == '#' ? 'Z' + 1 : g.Key);

        foreach (var group in groups)
        {
            items.Add(new ArtistHeaderItem { Letter = group.Key });
            foreach (var artist in group.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ArtistDataItem { Artist = artist });
            }
        }

        FlatArtistList.ReplaceAll(items);
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
