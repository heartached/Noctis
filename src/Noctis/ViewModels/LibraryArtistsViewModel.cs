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
    /// <summary>Most portrait columns per virtualized grid row (a wide window).</summary>
    public const int ArtistsPerRow = 7;

    /// <summary>Width one portrait tile needs: the 190px disc plus its 4px side margins.</summary>
    public const double TileWidth = 198;

    /// <summary>Portraits per row for the current width (<see cref="ComputeColumns"/>). Seven
    /// fixed columns overflowed below ~1390px and the discs drew over each other.</summary>
    [ObservableProperty] private int _gridColumns = ArtistsPerRow;

    /// <summary>As many whole tiles as <paramref name="usableWidth"/> holds, 1..<see cref="ArtistsPerRow"/>.</summary>
    internal static int ComputeColumns(double usableWidth)
        => Math.Clamp((int)(usableWidth / TileWidth), 1, ArtistsPerRow);

    /// <summary>Re-chunks the rows when the view's usable width changes the column count.</summary>
    public void UpdateGridColumns(double usableWidth)
    {
        if (!double.IsFinite(usableWidth) || usableWidth <= 0) return;
        var columns = ComputeColumns(usableWidth);
        if (columns == GridColumns) return;
        GridColumns = columns;
        if (_isActive) ApplyFilter(_currentFilter);
        else _isDirty = true;
    }

    private readonly ILibraryService _library;
    private readonly FavoriteArtistsService _favoriteArtists = new();
    private ArtistImageService? _artistImageService;

    private List<Artist> _allArtists = new();
    private string _currentFilter = string.Empty;
    private bool _isDirty = true;
    private DispatcherTimer? _searchDebounce;
    private DispatcherTimer? _imageRefreshDebounce;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);

    /// <summary>A section click skips its ApplyFilter("") when true: re-applying the empty
    /// filter Reset the whole list, re-creating every row and cover on each click.</summary>
    public bool IsSearchCleared => string.IsNullOrEmpty(_currentFilter) && string.IsNullOrEmpty(SearchText);

    /// <summary>
    /// The applied search the current rows were built for (SearchText runs ahead of it by the
    /// debounce). The view resets its scroll only when this changes, so a rebuild of the same
    /// results (library reload, portrait refresh, favorite toggle) keeps its place.
    /// </summary>
    internal string FilterKey => _currentFilter;

    // ── Sort (Name / Songs / Albums + direction) ──
    // Mirrored into the top bar by MainWindowViewModel, persisted through SettingsViewModel
    // (same pattern as the Albums grid sort). Favorites float to the top only for the
    // name sort; a count sort is a ranking, so favorites take their real rank there.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    private string _sortMode = "name";
    [ObservableProperty] private bool _sortAscending = true;

    public string SortLabel => SortMode switch
    {
        "songs" => Noctis.Localization.Loc.T("Main.SongCount"),
        "albums" => Noctis.Localization.Loc.T("Main.AlbumCount"),
        "time" => Noctis.Localization.Loc.T("Main.Time"),
        "genre" => Noctis.Localization.Loc.T("Main.Genre"),
        _ => Noctis.Localization.Loc.T("Main.Name"),
    };

    /// <summary>Top-bar sort menu: a mode ("name" / "songs" / "albums" / "time" / "genre")
    /// or "Ascending" / "Descending".</summary>
    [RelayCommand]
    private void SetSort(string? parameter)
    {
        switch (parameter)
        {
            case "Ascending": SortAscending = true; break;
            case "Descending": SortAscending = false; break;
            case "name" or "songs" or "albums" or "time" or "genre":
                if (SortMode == parameter) return;
                SortMode = parameter;
                // Counts and lengths read naturally biggest-first; names and genres A→Z.
                SortAscending = parameter is "name" or "genre";
                break;
            default: return;
        }
    }

    /// <summary>
    /// List view column header click: a new column sorts by it (<see cref="SetSort"/>),
    /// the current one flips the direction, the same as the Songs list's headers
    /// (LibrarySongsViewModel.Sort).
    /// </summary>
    [RelayCommand]
    private void SortByColumn(string? column)
    {
        if (column == null) return;
        if (column == SortMode) SortAscending = !SortAscending;
        else SetSort(column);
    }

    // ── Layout: portrait grid (default) or Songs-style list ──
    // Persisted through SettingsViewModel.ArtistViewMode by MainWindowViewModel, the same
    // route the sort takes. Both collections are rebuilt together by ApplyFilter, so a
    // switch shows the current search and sort at once without a rebuild.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    private string _viewMode = "grid";

    public bool IsListView => ViewMode == "list";

    /// <summary>Top-bar view icon: grid ↔ list.</summary>
    [RelayCommand]
    private void ToggleViewMode() => ViewMode = IsListView ? "grid" : "list";

    partial void OnViewModeChanged(string value)
    {
        // Only "grid" and "list" exist; anything else (a hand-edited settings file) is the grid.
        if (value is not ("grid" or "list")) ViewMode = "grid";
    }

    partial void OnSortModeChanged(string value) => ApplyFilter(_currentFilter);
    partial void OnSortAscendingChanged(bool value) => ApplyFilter(_currentFilter);

    // Leading words the name sort skips (GitHub #99, "The Beatles" under B). Empty while the
    // Settings toggle is off, which is the plain name order. Pushed in by MainWindowViewModel.
    private IReadOnlyList<string> _sortIgnoredWords = Array.Empty<string>();

    /// <summary>
    /// Sets the leading words the name sort skips and re-sorts. Gated like a library update:
    /// while hidden (including beneath the Settings modal the toggle lives in) it only marks
    /// dirty, and activation catches up.
    /// </summary>
    public void SetSortIgnoredWords(IReadOnlyList<string> words)
    {
        if (words.SequenceEqual(_sortIgnoredWords, StringComparer.Ordinal)) return;
        _sortIgnoredWords = words;
        if (_isActive) ApplyFilter(_currentFilter);
        else _isDirty = true;
    }

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Rows of artists for the virtualized grid display.</summary>
    public BulkObservableCollection<ArtistRow> ArtistRows { get; } = new();

    /// <summary>The same artists, in the same order, one per row for the list view.</summary>
    public BulkObservableCollection<ArtistListRow> ListRows { get; } = new();

    /// <summary>Per-artist totals the list view's Time and Genre columns (and sorts) read.</summary>
    internal readonly record struct ArtistStats(TimeSpan Duration, string Genre);

    private static readonly Dictionary<string, ArtistStats> NoStats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Totals for the library snapshot the last Refresh took; computed on first use,
    /// on the rebuild's worker thread, and reused by every rebuild until the library changes.</summary>
    private Lazy<Dictionary<string, ArtistStats>> _stats = new(() => NoStats);

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
                Dispatcher.UIThread.Post(() => UiStallWatchdog.Time("ArtistsRefresh", Refresh));
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
        {
            // Nothing to rebuild, but cached portraits may be due for a re-check against
            // the service (ArtistImageService.RefreshInterval). Kicking the sweep here is
            // what lets a changed photo land without a library change; throttled so
            // tab-hopping doesn't queue a full-library pass every time.
            KickImageSweepIfDue();
            return;
        }
        _isDirty = false;

        // A scan publish (every 1.5 s while scanning) hands over new Artist objects with no
        // ImagePath. Keep the portrait the previous object had, in memory: the rows below
        // used to be built from the empty ones and blanked every tile until a sweep
        // surfaced them again (GitHub #137). The two sweeps below still correct it.
        var shownPaths = new Dictionary<Guid, string>(_allArtists.Count);
        foreach (var artist in _allArtists)
            if (artist.ImagePath != null)
                shownPaths[artist.Id] = artist.ImagePath;

        _allArtists = _library.Artists.ToList();
        // The list view's Time / Genre totals: the visible songs only (LibraryService.Tracks,
        // like the counts on Artist), grouped by the same key the Artist buckets use. The
        // list is swapped, never edited, by the library, so the worker can read it later.
        var tracks = _library.Tracks;
        var groupMode = ArtistCredit.GroupMode;
        _stats = new Lazy<Dictionary<string, ArtistStats>>(() => ComputeStats(tracks, groupMode));
        foreach (var artist in _allArtists)
        {
            artist.IsFavorite = _favoriteArtists.IsFavorite(artist.Name);
            if (artist.ImagePath == null && shownPaths.TryGetValue(artist.Id, out var path))
                artist.ImagePath = path;
        }
        ApplyFilter(_currentFilter);

        // Clearing ImagePath for portraits whose file has gone missing used to be a
        // blocking File.Exists per artist, inline, before the tab could paint — 4,000
        // stats on a 4,000-artist library, on a NAS-backed cache that is 4,000 network
        // round trips. The grid renders first; the sweep corrects it a moment later.
        _ = SweepMissingArtistImagesAsync(_allArtists);

        // Trigger background artist image fetch
        KickImageSweep();
    }

    /// <summary>Wall-clock of the last sweep kick; gates <see cref="KickImageSweepIfDue"/>.</summary>
    private DateTime _lastImageSweepUtc = DateTime.MinValue;

    /// <summary>Minimum spacing between sweeps kicked by a no-op Refresh (tab activation).</summary>
    internal static readonly TimeSpan ImageSweepThrottle = TimeSpan.FromMinutes(10);

    private void KickImageSweepIfDue()
    {
        if (DateTime.UtcNow - _lastImageSweepUtc < ImageSweepThrottle) return;
        KickImageSweep();
    }

    private void KickImageSweep()
    {
        if (_artistImageService == null || _allArtists.Count == 0) return;
        _lastImageSweepUtc = DateTime.UtcNow;

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
            // Batch image updates every 2 seconds. Not restarted per update: a paced
            // first sweep lands a portrait every ~150 ms, so restarting the timer held
            // every tile back until the whole sweep was over (GitHub #137).
            Dispatcher.UIThread.Post(() =>
            {
                if (!_imageRefreshDebounce.IsEnabled)
                    _imageRefreshDebounce.Start();
            });
        });
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
        var sortMode = SortMode;
        var ascending = SortAscending;
        var ignoredWords = _sortIgnoredWords;
        var columns = GridColumns;
        var stats = _stats;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var statsByName = stats.Value;
            var ordered = OrderArtists(artists, query, sortMode, ascending, ignoredWords, statsByName);
            var rows = ChunkRows(ordered, columns);
            var listRows = BuildListRows(ordered, statsByName);
            if (Volatile.Read(ref _rebuildGeneration) == generation)
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _rebuildGeneration) != generation) return;
                    ArtistRows.ReplaceAll(rows);
                    ListRows.ReplaceAll(listRows);
                });
        });
    }

    private static List<ArtistRow> BuildRows(List<Artist> allArtists, string query)
        => BuildRows(allArtists, query, "name", ascending: true);

    /// <summary>
    /// Pure ordering logic (static for unit tests). Under a search the match rank stays
    /// first and the chosen sort only breaks ties inside a rank, so relevance is intact.
    /// <paramref name="ignoredWords"/> are leading words the name key skips (GitHub #99).
    /// </summary>
    internal static List<ArtistRow> BuildRows(List<Artist> allArtists, string query, string sortMode, bool ascending,
        IReadOnlyList<string>? ignoredWords = null, int columns = ArtistsPerRow,
        IReadOnlyDictionary<string, ArtistStats>? stats = null)
        => ChunkRows(OrderArtists(allArtists, query, sortMode, ascending, ignoredWords, stats ?? NoStats), columns);

    /// <summary>The list view's rows over the same ordering (static for unit tests).</summary>
    internal static List<ArtistListRow> BuildListRows(List<Artist> allArtists, string query, string sortMode, bool ascending,
        IReadOnlyDictionary<string, ArtistStats> stats, IReadOnlyList<string>? ignoredWords = null)
        => BuildListRows(OrderArtists(allArtists, query, sortMode, ascending, ignoredWords, stats), stats);

    private static List<ArtistListRow> BuildListRows(List<Artist> ordered, IReadOnlyDictionary<string, ArtistStats> stats)
    {
        var rows = new List<ArtistListRow>(ordered.Count);
        foreach (var artist in ordered)
        {
            var s = StatsOf(stats, artist);
            rows.Add(new ArtistListRow { Artist = artist, TotalDuration = s.Duration, Genre = s.Genre });
        }
        return rows;
    }

    private static List<Artist> OrderArtists(List<Artist> allArtists, string query, string sortMode, bool ascending,
        IReadOnlyList<string>? ignoredWords, IReadOnlyDictionary<string, ArtistStats> stats)
    {
        IEnumerable<Artist> filtered;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            // GitHub #107: "artist:" tags and words in any order ("swift taylor") match the
            // name; tags for fields an artist row doesn't show (genre, year) don't narrow it.
            var parsed = Noctis.Helpers.SearchQuery.Parse(q);
            filtered = ApplySort(
                allArtists.Where(a => parsed.MatchesName(a.Name, RemoveWhitespace(a.Name ?? string.Empty)))
                          .OrderBy(a => RankMatch(a.Name, q, qNoSpaces)),
                sortMode, ascending, ignoredWords, stats);
        }
        else
        {
            filtered = ApplySort(allArtists, sortMode, ascending, ignoredWords, stats);
        }
        return filtered.ToList();
    }

    /// <summary>Chunks the ordered artists into fixed-width rows so the outer ListBox can virtualize.</summary>
    private static List<ArtistRow> ChunkRows(List<Artist> ordered, int columns)
    {
        var rows = new List<ArtistRow>();
        ArtistRow? row = null;
        foreach (var artist in ordered)
        {
            if (row == null || row.Artists.Count >= columns)
            {
                row = new ArtistRow();
                rows.Add(row);
            }
            row.Artists.Add(artist);
        }

        return rows;
    }

    private static ArtistStats StatsOf(IReadOnlyDictionary<string, ArtistStats> stats, Artist artist)
        => artist.Name != null && stats.TryGetValue(artist.Name, out var s) ? s : new ArtistStats(TimeSpan.Zero, string.Empty);

    /// <summary>
    /// Per-artist total length and dominant genre over <paramref name="tracks"/>, bucketed by
    /// the grouping artist exactly like LibraryService's Artist counts, so Time covers the
    /// songs the Songs column counts. The genre follows ArtistDetailViewModel.DominantGenre
    /// (each tag of a multi-genre song counts, ties alphabetical) in the artist page's GENRE
    /// fact casing. One pass, no LINQ grouping: it runs on every library change.
    /// </summary>
    internal static Dictionary<string, ArtistStats> ComputeStats(IReadOnlyList<Track> tracks, ArtistGroupMode mode)
    {
        var acc = new Dictionary<string, (long Ticks, Dictionary<string, int>? Genres)>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var key = track.GetGroupingArtist(mode);
            acc.TryGetValue(key, out var a);
            a.Ticks += track.Duration.Ticks;
            if (!string.IsNullOrWhiteSpace(track.Genre))
            {
                foreach (var genre in Track.SplitGenres(track.Genre))
                {
                    a.Genres ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    a.Genres[genre] = a.Genres.GetValueOrDefault(genre) + 1;
                }
            }
            acc[key] = a;
        }

        var textInfo = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
        var result = new Dictionary<string, ArtistStats>(acc.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, a) in acc)
        {
            string? top = null;
            var topCount = 0;
            if (a.Genres != null)
                foreach (var (genre, count) in a.Genres)
                    if (count > topCount || (count == topCount && StringComparer.OrdinalIgnoreCase.Compare(genre, top) < 0))
                    {
                        top = genre;
                        topCount = count;
                    }
            result[name] = new ArtistStats(TimeSpan.FromTicks(a.Ticks),
                top == null ? string.Empty : textInfo.ToTitleCase(top.ToLowerInvariant()));
        }
        return result;
    }

    /// <summary>
    /// Applies the grid sort. Works on an already-ordered sequence too (search rank),
    /// in which case it only orders within equal ranks. Favorites stay on top for the
    /// name sort (GitHub #41); count sorts rank everyone by the number.
    /// </summary>
    private static IEnumerable<Artist> ApplySort(IEnumerable<Artist> source, string sortMode, bool ascending,
        IReadOnlyList<string>? ignoredWords, IReadOnlyDictionary<string, ArtistStats> stats)
    {
        var ordered = source as IOrderedEnumerable<Artist> ?? source.OrderBy(_ => 0);
        var nameCmp = StringComparer.OrdinalIgnoreCase;
        // With no ignored words the key is the plain name, i.e. the pre-#99 order.
        Func<Artist, string> name = ignoredWords is { Count: > 0 }
            ? a => ArtistSortWords.SortKey(a.Name, ignoredWords)
            : a => a.Name;
        switch (sortMode)
        {
            case "songs":
                return ascending
                    ? ordered.ThenBy(a => a.TrackCount).ThenBy(name, nameCmp)
                    : ordered.ThenByDescending(a => a.TrackCount).ThenBy(name, nameCmp);
            case "albums":
                return ascending
                    ? ordered.ThenBy(a => a.AlbumCount).ThenBy(name, nameCmp)
                    : ordered.ThenByDescending(a => a.AlbumCount).ThenBy(name, nameCmp);
            case "time":
                return ascending
                    ? ordered.ThenBy(a => StatsOf(stats, a).Duration).ThenBy(name, nameCmp)
                    : ordered.ThenByDescending(a => StatsOf(stats, a).Duration).ThenBy(name, nameCmp);
            case "genre":
                // Untagged artists sit after every genre in both directions: a blank is no
                // genre, not one that sorts first.
                var withGenre = ordered.ThenBy(a => StatsOf(stats, a).Genre.Length == 0);
                return (ascending
                        ? withGenre.ThenBy(a => StatsOf(stats, a).Genre, nameCmp)
                        : withGenre.ThenByDescending(a => StatsOf(stats, a).Genre, nameCmp))
                    .ThenBy(name, nameCmp);
            default:
                return ascending
                    ? ordered.ThenByDescending(a => a.IsFavorite).ThenBy(name, nameCmp)
                    : ordered.ThenByDescending(a => a.IsFavorite).ThenByDescending(name, nameCmp);
        }
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

    /// <summary>The songs the artist page's Play All plays (its releases, then features).</summary>
    internal List<Track> TracksOf(Artist artist)
        => ArtistDetailViewModel.AllTracksOf(_library.Albums, artist.Name);

    /// <summary>GitHub #121: every song of the artist → Send to Folder (copy or move).</summary>
    [RelayCommand]
    private Task SendArtistToFolder(Artist artist)
        => artist == null ? Task.CompletedTask : MetadataHelper.OpenSendToFolderDialog(TracksOf(artist));

    /// <summary>
    /// Toggles the artist's favorite flag (GitHub #41): favorites float to the top of
    /// the grid and carry an accent star. Persisted by name, then the rows rebuild so
    /// the re-sort and the star land immediately.
    /// </summary>
    public void ToggleFavoriteArtist(Artist artist)
    {
        if (artist == null) return;

        var favorite = !_favoriteArtists.IsFavorite(artist.Name);
        _favoriteArtists.SetFavorite(artist.Name, favorite);
        artist.IsFavorite = favorite;
        ApplyFilter(_currentFilter);
    }

    /// <summary>Artist tile menu: snoozes every track the artist's page plays for a month.</summary>
    [RelayCommand]
    private Task SnoozeArtistForMonth(Artist? artist)
    {
        if (artist == null) return Task.CompletedTask;
        var (releases, _, songs) = ArtistDetailViewModel.Classify(_library.Albums, artist.Name);
        return _library.SetTracksSnoozedAsync(ArtistDetailViewModel.PlayableTracks(releases, songs),
            DateTime.UtcNow.AddDays(PlayerViewModel.SnoozeDurationDays));
    }

    /// <summary>Whether the artist is favourited — the artist page reads the same
    /// in-memory set this grid stamps its tiles from, so the two never disagree.</summary>
    public bool IsFavoriteArtist(string? artistName) => _favoriteArtists.IsFavorite(artistName);

    /// <summary>Settings › Reset Everything (the library is emptied with it, so no artist shows a heart).</summary>
    public void ClearFavoriteArtists() => _favoriteArtists.Clear();

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

    private static int RankMatch(string? source, string query, string queryNoSpaces)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 1000;

        var normalized = source.Trim();
        var normalizedNoSpaces = RemoveWhitespace(normalized);

        var hasKey = queryNoSpaces.Length > 0; // empty for punctuation-only queries

        if (string.Equals(normalized, query, StringComparison.OrdinalIgnoreCase) ||
            (hasKey && string.Equals(normalizedNoSpaces, queryNoSpaces, StringComparison.OrdinalIgnoreCase)))
            return 0;

        if (normalized.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            (hasKey && normalizedNoSpaces.StartsWith(queryNoSpaces, StringComparison.OrdinalIgnoreCase)))
            return 1;

        if (normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (hasKey && normalizedNoSpaces.Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
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
