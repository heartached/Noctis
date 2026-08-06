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

        // Dispatch to UI thread since scan fires LibraryUpdated from a background thread.
        // Held in a field so Dispose can detach it — an anonymous lambda with no stored
        // reference cannot be unsubscribed, matching LibrarySongsViewModel.
        // Only rebuild immediately while this view is current — a scan fires LibraryUpdated
        // every ~1.5 s and hidden views catch up via the dirty flag when activated.
        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            if (_isActive)
                Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    private EventHandler? _libraryUpdatedHandler;

    /// <summary>
    /// Set by MainWindowViewModel when Artists becomes (or stops being) the current view.
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
            // Covers back-navigation paths that swap CurrentView without a Refresh call.
            if (value) Refresh();
        }
    }

    private bool _isActive;

    public void SetArtistImageService(ArtistImageService service) => _artistImageService = service;

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    public void Refresh()
    {
        if (!_isDirty && ArtistRows.Count > 0)
            return;
        _isDirty = false;

        _allArtists = _library.Artists.ToList();
        ApplyFilter(_currentFilter);

        // Clearing ImagePath for portraits whose file has gone missing used to be a
        // blocking File.Exists per artist, inline, before the tab could paint — 4,000
        // stats on a 4,000-artist library, on a NAS-backed cache that is 4,000 network
        // round trips. The grid renders first; the sweep corrects it a moment later.
        _ = SweepMissingArtistImagesAsync(_allArtists);

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

    private async Task SweepMissingArtistImagesAsync(List<Artist> artists)
    {
        try
        {
            var stale = await Task.Run(() => artists
                .Where(a => !string.IsNullOrWhiteSpace(a.ImagePath) && !File.Exists(a.ImagePath))
                .ToList()).ConfigureAwait(false);

            if (stale.Count == 0) return;

            // ImagePath is bound, so the write has to land on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var artist in stale)
                    artist.ImagePath = null;
                ApplyFilter(_currentFilter);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Artists] Image sweep failed: {ex.Message}");
        }
    }

    // Guards a stale background rebuild against overwriting a newer one.
    private int _rebuildGeneration;

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        OnPropertyChanged(nameof(HasActiveFilter));

        // Off-UI-thread rebuild via the same generation-guarded path as
        // LibraryAlbumsViewModel.RebuildFilteredRows. The sort + row chunking is
        // a full-library pass that used to run inline on the UI thread — on every
        // LibraryUpdated while this view was current (scans, the merge-featured
        // settings flip), stalling whatever animation was mid-flight. The grid
        // keeps its previous rows for the few frames the fresh list takes.
        var generation = Interlocked.Increment(ref _rebuildGeneration);
        var artists = _allArtists;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var rows = BuildRows(artists, query);
            if (Volatile.Read(ref _rebuildGeneration) == generation)
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _rebuildGeneration) != generation) return;
                    ArtistRows.ReplaceAll(rows);
                });
        });
    }

    private static List<ArtistRow> BuildRows(List<Artist> allArtists, string query)
    {
        IEnumerable<Artist> filtered;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            filtered = allArtists
                .Where(a => MatchesSearch(a.Name, q, qNoSpaces))
                .OrderBy(a => RankMatch(a.Name, q, qNoSpaces))
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            filtered = allArtists.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Sets a user-picked image as the artist's portrait. Evicts any stale cached
    /// bitmap and rebuilds the row so the tile (bound to a non-observable Artist)
    /// reflects the new image immediately.
    /// </summary>
    public async Task ChangeArtistImageAsync(Artist artist, byte[] imageData)
    {
        if (_artistImageService == null || artist == null)
            return;

        var newPath = await _artistImageService.SetCustomImageAsync(artist, imageData);
        if (string.IsNullOrEmpty(newPath))
            return;

        ArtworkCache.Invalidate(newPath);
        ApplyFilter(_currentFilter);
    }

    /// <summary>
    /// Re-downloads the artist's portrait from the online services (clearing any prior
    /// removal), restoring the auto-fetched photo. No-op if nothing is found.
    /// </summary>
    public async Task SearchArtistImageAsync(Artist artist)
    {
        if (_artistImageService == null || artist == null)
            return;

        var newPath = await _artistImageService.RefetchImageAsync(artist);
        if (string.IsNullOrEmpty(newPath))
            return;

        ArtworkCache.Invalidate(newPath);
        ApplyFilter(_currentFilter);
    }

    /// <summary>
    /// Removes the artist's portrait and suppresses future auto-download, falling
    /// back to the placeholder icon. Rebuilds the row to reflect the change.
    /// </summary>
    public void RemoveArtistImage(Artist artist)
    {
        if (_artistImageService == null || artist == null)
            return;

        var oldPath = artist.ImagePath;
        _artistImageService.RemoveImage(artist);

        if (!string.IsNullOrEmpty(oldPath))
            ArtworkCache.Invalidate(oldPath);
        ApplyFilter(_currentFilter);
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
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }

    // Normalizes a value into a comparable search key: strips whitespace, punctuation
    // (e.g. the apostrophe in "Don't") and accents so queries match regardless. Name kept
    // for its call sites; see Helpers/SearchText for the shared implementation.
    private static string RemoveWhitespace(string value) => Noctis.Helpers.SearchText.Normalize(value);
}
