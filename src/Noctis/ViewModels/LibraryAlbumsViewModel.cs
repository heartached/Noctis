using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly SettingsViewModel _settings;
    private readonly System.ComponentModel.PropertyChangedEventHandler _settingsPropertyChangedHandler;

    private List<Album> _allAlbums = new();
    private string _currentFilter = string.Empty;
    private DispatcherTimer? _searchDebounce;
    private int _rebuildGeneration;
    private bool _isDirty = true;
    private EventHandler? _viewStateLoadedHandler;

    /// <summary>Guards the startup adoption of the persisted sort so echoing it back into
    /// settings doesn't queue a pointless disk write.</summary>
    private bool _adoptingPersistedState;

    /// <summary>Set while mode and direction are changed together, so the grid rebuilds
    /// once for the pair rather than once per property.</summary>
    private bool _suspendSortRebuild;

    private const int ColumnsPerRow = 5;
    private const double TileTextHeight = 64;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private double _tileArtworkSize = 180;
    [ObservableProperty] private string _artistFilterName = string.Empty;
    public double TileRowHeight => TileArtworkSize + TileTextHeight;

    /// <summary>
    /// Active release-type chip filter. null = "All". Drives the chip strip
    /// at the top of the Albums view and is applied alongside the search query.
    /// </summary>
    [ObservableProperty] private ReleaseType? _releaseTypeFilter;

    /// <summary>Filter chips shown above the album grid; one entry per filter value.</summary>
    public ObservableCollection<ReleaseTypeChip> ReleaseTypeChips { get; }

    /// <summary>Audio-quality filter chips (Lossless / Hi-Res), toggleable.</summary>
    public ObservableCollection<QualityChip> QualityChips { get; } = new()
    {
        new QualityChip { Key = "lossless", Label = "Lossless" },
        new QualityChip { Key = "hires", Label = "Hi-Res" },
    };

    /// <summary>Active quality filter: "" (off), "lossless", or "hires".</summary>
    [ObservableProperty] private string _qualityFilter = string.Empty;

    /// <summary>Grid sort: "default" (artist/recent floats), "title", "dateadded",
    /// "mostplayed", "albumartist", or "year".</summary>
    [ObservableProperty] private string _albumSortMode = "default";

    /// <summary>Sort direction. Ignored by "default", which has its own recent-import
    /// float rather than a single ordering key.</summary>
    [ObservableProperty] private bool _albumSortAscending = true;

    /// <summary>Label for the sort dropdown button.</summary>
    public string AlbumSortLabel => AlbumSortMode switch
    {
        "title" => "Title",
        "dateadded" => "Recently added",
        "mostplayed" => "Most played",
        "albumartist" => "Album Artist",
        "year" => "Year",
        _ => "Default",
    };

    /// <summary>Whether the direction controls apply to the current mode.</summary>
    public bool AlbumSortDirectionEnabled => AlbumSortMode != "default";

    /// <summary>Checkmark helpers for the sort dropdown.</summary>
    public bool AlbumSortDescending => !AlbumSortAscending;

    /// <summary>Label for the release-type dropdown button.</summary>
    public string ReleaseTypeFilterLabel => ReleaseTypeFilter switch
    {
        ReleaseType.Album => "Albums",
        ReleaseType.Single => "Singles",
        ReleaseType.EP => "EPs",
        ReleaseType.Compilation => "Other",
        _ => "All",
    };

    /// <summary>Label for the quality dropdown button.</summary>
    public string QualityFilterLabel => QualityFilter switch
    {
        "lossless" => "Lossless",
        "hires" => "Hi-Res",
        _ => "All",
    };

    partial void OnQualityFilterChanged(string value)
    {
        foreach (var chip in QualityChips)
            chip.IsActive = chip.Key == value;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(QualityFilterLabel));
        RebuildFilteredRows();
    }

    partial void OnAlbumSortModeChanged(string value)
    {
        OnPropertyChanged(nameof(AlbumSortLabel));
        OnPropertyChanged(nameof(AlbumSortDirectionEnabled));
        if (!_adoptingPersistedState) _settings.AlbumSortMode = value;
        if (!_suspendSortRebuild) RebuildFilteredRows();
    }

    partial void OnAlbumSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(AlbumSortDescending));
        if (!_adoptingPersistedState) _settings.AlbumSortAscending = value;
        if (!_suspendSortRebuild) RebuildFilteredRows();
    }

    /// <summary>Toggles a quality chip; clicking the active chip clears the filter.</summary>
    [RelayCommand]
    private void SelectQualityChip(QualityChip? chip)
    {
        if (chip == null) return;
        QualityFilter = chip.Key == QualityFilter ? string.Empty : chip.Key;
    }

    /// <summary>
    /// Sets the grid sort from the dropdown: a mode key, or "ascending"/"descending" for
    /// the direction. Switching mode adopts that mode's natural direction — picking
    /// "Recently added" should mean newest first, not whichever way the previous mode ran.
    /// </summary>
    [RelayCommand]
    private void SetAlbumSort(string mode)
    {
        switch (mode)
        {
            case "ascending":
                AlbumSortAscending = true;
                return;
            case "descending":
                AlbumSortAscending = false;
                return;
        }

        if (AlbumSortMode == mode) return;

        // Mode and direction change together; hold the rebuild so the grid is rebuilt
        // once for the pair instead of once per property.
        _suspendSortRebuild = true;
        try
        {
            AlbumSortAscending = !IsDescendingByDefault(mode);
            AlbumSortMode = mode;
        }
        finally
        {
            _suspendSortRebuild = false;
        }

        RebuildFilteredRows();
    }

    /// <summary>
    /// Modes whose natural reading is "most/newest first": a user picking Year or
    /// Recently added means latest, not 1954.
    /// </summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static bool IsDescendingByDefault(string sortMode) =>
        sortMode is "dateadded" or "mostplayed" or "year";

    /// <summary>Applies the grid sort persisted from the previous session.</summary>
    private void AdoptPersistedSort()
    {
        _adoptingPersistedState = true;
        _suspendSortRebuild = true;
        try
        {
            AlbumSortMode = _settings.AlbumSortMode;
            AlbumSortAscending = _settings.AlbumSortAscending;
        }
        finally
        {
            _suspendSortRebuild = false;
            _adoptingPersistedState = false;
        }

        RebuildFilteredRows();
    }

    /// <summary>Sets the release-type filter from a dropdown key ("all" clears it).</summary>
    [RelayCommand]
    private void SetReleaseTypeFilter(string key) => ReleaseTypeFilter = key switch
    {
        "album" => ReleaseType.Album,
        "single" => ReleaseType.Single,
        "ep" => ReleaseType.EP,
        "other" => ReleaseType.Compilation,
        _ => null,
    };

    /// <summary>Sets the quality filter from a dropdown key ("all" clears it).</summary>
    [RelayCommand]
    private void SetQualityFilter(string key) => QualityFilter = key == "all" ? string.Empty : key;

    partial void OnTileArtworkSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TileRowHeight));
    }
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter) || ReleaseTypeFilter.HasValue || QualityFilter.Length > 0;

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

    /// <summary>
    /// Filtered albums grouped into rows for the virtualized grid. Mixed row types:
    /// <see cref="AlbumRow"/> for album tiles, plus <see cref="ArtistSectionHeader"/> and
    /// <see cref="ArtistSongsRow"/> when an artist page interleaves its Songs section.
    /// </summary>
    public BulkObservableCollection<object> FilteredAlbumRows { get; } = new();

    /// <summary>Fires when the user wants to open an album's detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Fires when the user clicks the back button in artist filter mode.</summary>
    public event EventHandler? BackRequested;

    public LibraryAlbumsViewModel(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar, SettingsViewModel settings)
    {
        _library = library;
        _player = player;
        _sidebar = sidebar;
        _settings = settings;

        // Rebuild the grid when the "collapse album editions" setting is toggled.
        _settingsPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CollapseAlbumEditions))
            {
                _isDirty = true;
                Dispatcher.UIThread.Post(() => RebuildFilteredRows());
            }
        };
        _settings.PropertyChanged += _settingsPropertyChangedHandler;

        // Settings load asynchronously and finish after this view model is built, so the
        // persisted sort is adopted when it lands rather than read here.
        _viewStateLoadedHandler = (_, _) => AdoptPersistedSort();
        _settings.ViewStateLoaded += _viewStateLoadedHandler;

        ReleaseTypeChips = new ObservableCollection<ReleaseTypeChip>
        {
            new() { Filter = null, Label = "All", IsActive = true },
            new() { Filter = ReleaseType.Album, Label = "Albums" },
            new() { Filter = ReleaseType.Single, Label = "Singles" },
            new() { Filter = ReleaseType.EP, Label = "EPs" },
            new() { Filter = ReleaseType.Compilation, Label = "Other" },
        };

        // Mark dirty when library changes — actual reload deferred to next Refresh() call.
        // Held in a field so Dispose can detach it: an anonymous lambda with no stored
        // reference can never be unsubscribed, and Dispose only detached the settings
        // handler. Harmless only while MainWindowViewModel keeps this instance alive for
        // the process lifetime — LibrarySongsViewModel already does it this way.
        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            // Only rebuild immediately while this view is current — a scan fires
            // LibraryUpdated every ~1.5 s and hidden views catch up via the dirty
            // flag when activated instead of rebuilding the grid each event.
            if (_isActive)
                Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    private EventHandler? _libraryUpdatedHandler;

    /// <summary>
    /// Set by MainWindowViewModel when Albums becomes (or stops being) the current view.
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

    partial void OnReleaseTypeFilterChanged(ReleaseType? value)
    {
        foreach (var chip in ReleaseTypeChips)
            chip.IsActive = chip.Filter == value;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ReleaseTypeFilterLabel));
        RebuildFilteredRows();
    }

    [RelayCommand]
    private void SelectReleaseTypeChip(ReleaseTypeChip? chip)
    {
        if (chip == null) return;
        ReleaseTypeFilter = chip.Filter;
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

        // Off-UI-thread rebuild via the same generation-guarded path as filter changes.
        // The sync rebuild this replaces froze the UI thread on every navigation/scan
        // event at large library sizes; the grid keeps showing its previous rows for
        // the few frames the fresh list takes to compute in the background.
        RebuildFilteredRows(refreshFromLibrary: true);
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
        var rows = BuildFilteredRows(_allAlbums, ArtistFilterName, _currentFilter, ColumnsPerRow, ReleaseTypeFilter, QualityFilter, AlbumSortMode, AlbumSortAscending);
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
        var rows = BuildFilteredRows(_allAlbums, ArtistFilterName, _currentFilter, ColumnsPerRow, ReleaseTypeFilter, QualityFilter, AlbumSortMode, AlbumSortAscending);
        FilteredAlbumRows.ReplaceAll(rows);
    }

    private void RebuildFilteredRows(bool refreshFromLibrary = false)
    {
        // Capture state for the background task
        var generation = Interlocked.Increment(ref _rebuildGeneration);
        var albums = _allAlbums;
        var artistFilter = ArtistFilterName;
        var searchFilter = _currentFilter;
        var columns = ColumnsPerRow;
        var releaseTypeFilter = ReleaseTypeFilter;
        var qualityFilter = QualityFilter;
        var sortMode = AlbumSortMode;
        var sortAscending = AlbumSortAscending;
        var library = refreshFromLibrary ? _library : null;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Move the library snapshot off the UI thread when refreshing
            if (library != null)
                albums = library.Albums.ToList();

            var rows = BuildFilteredRows(albums, artistFilter, searchFilter, columns, releaseTypeFilter, qualityFilter, sortMode, sortAscending);

            // Only apply if no newer rebuild was requested. A superseded library
            // reload must re-mark dirty: the newer request may have captured the
            // pre-reload _allAlbums snapshot.
            if (Volatile.Read(ref _rebuildGeneration) == generation)
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _rebuildGeneration) != generation)
                    {
                        if (library != null) _isDirty = true;
                        return;
                    }
                    if (library != null) _allAlbums = albums;
                    FilteredAlbumRows.ReplaceAll(rows);
                });
            else if (library != null)
            {
                _isDirty = true;
            }
        });
    }

    private List<object> BuildFilteredRows(
        List<Album> allAlbums, string artistFilter, string searchFilter, int columnsPerRow,
        ReleaseType? releaseTypeFilter = null, string qualityFilter = "", string sortMode = "default",
        bool sortAscending = true)
    {
        var filtered = allAlbums.AsEnumerable();

        // Release-type chip narrows the grid before any other filter.
        if (releaseTypeFilter.HasValue)
        {
            filtered = releaseTypeFilter.Value switch
            {
                // "Other" chip groups everything that is not Album / Single / EP
                // (Compilation, Live, Remix, Soundtrack, Other) under one bucket.
                ReleaseType.Compilation => filtered.Where(a => a.ReleaseType is not (ReleaseType.Album or ReleaseType.Single or ReleaseType.EP)),
                _ => filtered.Where(a => a.ReleaseType == releaseTypeFilter.Value),
            };
        }

        // Quality chip: an album qualifies when every track meets the bar,
        // matching the album-level quality badge semantics.
        filtered = qualityFilter switch
        {
            "lossless" => filtered.Where(a => a.Tracks.Count > 0 && a.Tracks.All(t => t.IsLossless)),
            "hires" => filtered.Where(a => a.Tracks.Count > 0 && a.Tracks.All(t => t.IsHiResLossless)),
            _ => filtered,
        };

        // Apply artist filter first. Match on the album-artist credit only (parsed
        // into individual collaborators, so credited collaboration albums like
        // "A & B" still appear). Track-level feature appearances are deliberately
        // excluded so a different artist's album doesn't land in this artist's
        // discography just because they're featured on a track — keeping this grid
        // consistent with the artist landing page (GetAlbumsByArtist).
        if (!string.IsNullOrEmpty(artistFilter))
        {
            filtered = filtered.Where(a => ContainsArtistToken(a.Artist, artistFilter));
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

        // Explicit sort modes replace the default ordering (and the recent-import
        // float) when no artist/search filter narrows the grid.
        if (sortMode != "default" && string.IsNullOrEmpty(artistFilter) && string.IsNullOrWhiteSpace(searchFilter))
        {
            filtered = ApplySortMode(filtered, sortMode, sortAscending);

            IEnumerable<Album> sortedAlbums = filtered;
            if (_settings.CollapseAlbumEditions)
                sortedAlbums = CollapseEditions(sortedAlbums);

            // Sort modes only apply outside artist pages, so no songs section here.
            return GroupIntoRows(sortedAlbums, columnsPerRow).Cast<object>().ToList();
        }

        // Float newly added albums to the top when not searching/filtering.
        // IsRecentImport only lives for the current session, so also float
        // anything added in the last 7 days — the placement survives a restart
        // (the previous flag-only check lost the float on every relaunch).
        IEnumerable<Album> ordered;
        if (string.IsNullOrEmpty(artistFilter) && string.IsNullOrWhiteSpace(searchFilter))
        {
            var recentCutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            var materialized = filtered.ToList();
            var recent = materialized
                .Where(a => a.Tracks.Any(t => t.IsRecentImport || t.DateAdded >= recentCutoff))
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

        // Collapse multiple editions of the same release into one representative tile
        // when the opt-in setting is on. Skipped while searching so a specific edition
        // can still be found. Hidden editions stay reachable via the album page's
        // "Other Versions" section.
        if (_settings.CollapseAlbumEditions && string.IsNullOrWhiteSpace(searchFilter))
            ordered = CollapseEditions(ordered);

        var albumRows = GroupIntoRows(ordered, columnsPerRow);

        // Artist pages interleave a Songs section above the discography so tracks whose
        // albums are credited to someone else (compilation appearances, features, typo'd
        // credits) are still reachable — without it an artist minted from track credits
        // alone opens to a permanently empty page. Chip filters are album-level, so the
        // section is omitted while one narrows the grid.
        if (!string.IsNullOrEmpty(artistFilter) && !releaseTypeFilter.HasValue && qualityFilter.Length == 0)
            return ComposeArtistRows(albumRows, allAlbums, artistFilter, searchFilter);

        return albumRows.Cast<object>().ToList();
    }

    /// <summary>
    /// Orders the grid for an explicit sort mode ("title", "dateadded", "mostplayed",
    /// "albumartist", "year"); any other mode returns the input unchanged.
    /// <para>
    /// <paramref name="ascending"/> flips the primary key only — tie-breakers stay
    /// ascending, so reversing "Most played" still lists equally-played albums A→Z
    /// rather than shuffling them into reverse alphabetical order.
    /// </para>
    /// </summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static IEnumerable<Album> ApplySortMode(IEnumerable<Album> albums, string sortMode, bool ascending) =>
        sortMode switch
        {
            // Straight alphabetical by album title — what Apple Music's Albums view does,
            // and the one ordering the grid was missing (issue #33).
            "title" => (ascending
                    ? albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    : albums.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase))
                .ThenBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Year),
            "dateadded" => ascending
                ? albums.OrderBy(a => a.Tracks.Count > 0 ? a.Tracks.Max(t => t.DateAdded) : DateTime.MinValue)
                : albums.OrderByDescending(a => a.Tracks.Count > 0 ? a.Tracks.Max(t => t.DateAdded) : DateTime.MinValue),
            "mostplayed" => (ascending
                    ? albums.OrderBy(a => a.Tracks.Sum(t => (long)t.PlayCount))
                    : albums.OrderByDescending(a => a.Tracks.Sum(t => (long)t.PlayCount)))
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            // Album Artist/Year (Apple Music/MusicBee): artists A→Z, each artist's
            // releases in chronological order. Album.Artist is the album artist.
            "albumartist" => (ascending
                    ? albums.OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                    : albums.OrderByDescending(a => a.Artist, StringComparer.OrdinalIgnoreCase))
                .ThenBy(a => a.Year)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            // Descending by default so newest releases lead; unknown years (0) sink
            // to the bottom there, and lead when the direction is flipped.
            "year" => (ascending
                    ? albums.OrderBy(a => a.Year)
                    : albums.OrderByDescending(a => a.Year))
                .ThenBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => albums,
        };

    private const int SongsPerRow = 3;

    /// <summary>
    /// Ranked pills shown on an artist page with albums; artists with no matching
    /// albums show their full song list instead (that page is otherwise empty).
    /// </summary>
    private const int MaxArtistSongs = 9;

    /// <summary>
    /// Prepends the artist's ranked Songs section (Home "Most Listened To" pill rows)
    /// to the album grid rows, with section headers when both sections are present.
    /// </summary>
    private static List<object> ComposeArtistRows(
        List<AlbumRow> albumRows, List<Album> allAlbums, string artistFilter, string searchFilter)
    {
        // Match on the full track credit (tokenised), so feature appearances count as
        // "associated with" — but tokens compare exactly, keeping near-duplicate artist
        // spellings (e.g. a quoted variant) on their own separate pages.
        IEnumerable<Track> tracks = allAlbums
            .SelectMany(a => a.Tracks)
            .Where(t => ContainsArtistToken(t.Artist, artistFilter));

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var q = searchFilter.Trim();
            var qNoSpaces = RemoveWhitespace(q);
            tracks = tracks.Where(t => MatchesSearch(t.Title, q, qNoSpaces));
        }

        var orderedSongs = tracks
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedSongs.Count == 0)
            return albumRows.Cast<object>().ToList();

        var capped = albumRows.Count > 0 && orderedSongs.Count > MaxArtistSongs;
        if (capped)
            orderedSongs = orderedSongs.Take(MaxArtistSongs).ToList();

        var rows = new List<object>
        {
            new ArtistSectionHeader { Title = capped ? "Top Songs" : "Songs" },
        };

        for (var i = 0; i < orderedSongs.Count; i += SongsPerRow)
        {
            rows.Add(new ArtistSongsRow
            {
                Songs = orderedSongs
                    .Skip(i).Take(SongsPerRow)
                    .Select((t, j) => new TopSongRow { Track = t, Rank = i + j + 1 })
                    .ToList(),
            });
        }

        if (albumRows.Count > 0)
            rows.Add(new ArtistSectionHeader { Title = "Albums" });
        rows.AddRange(albumRows);
        return rows;
    }

    /// <summary>Groups albums into fixed-width rows for the virtualized grid.</summary>
    private static List<AlbumRow> GroupIntoRows(IEnumerable<Album> albums, int columnsPerRow)
    {
        var rows = new List<AlbumRow>();
        var currentRow = new List<Album>();

        foreach (var album in albums)
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

    /// <summary>
    /// Collapses albums sharing the same album-artist credit and normalized base title
    /// (edition suffixes stripped) into a single representative edition. Each group is
    /// anchored at its first occurrence so the caller's existing sort order is preserved.
    /// </summary>
    private static IEnumerable<Album> CollapseEditions(IEnumerable<Album> albums)
    {
        var groups = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var a in albums)
        {
            var baseTitle = Helpers.AlbumTitle.NormalizeForEdition(a.Name);
            var key = string.IsNullOrEmpty(baseTitle)
                ? $" id:{a.Id}"                                   // never merge untitled albums
                : $"{(a.Artist ?? string.Empty).Trim()} {baseTitle}";
            if (!groups.TryGetValue(key, out var rep))
            {
                groups[key] = a;
                order.Add(key);
            }
            else if (IsBetterEditionRepresentative(a, rep))
            {
                groups[key] = a;
            }
        }
        return order.Select(k => groups[k]).ToList();
    }

    /// <summary>
    /// Representative selection: prefer the plain/base edition, else the most complete
    /// (most tracks), else the earliest release year.
    /// </summary>
    private static bool IsBetterEditionRepresentative(Album cand, Album cur)
    {
        var cb = Helpers.AlbumTitle.IsBaseEdition(cand.Name);
        var ub = Helpers.AlbumTitle.IsBaseEdition(cur.Name);
        if (cb != ub) return cb;                                  // prefer plain edition
        if (cand.TrackCount != cur.TrackCount) return cand.TrackCount > cur.TrackCount; // most complete
        if (cand.Year != cur.Year) return cand.Year != 0 && (cur.Year == 0 || cand.Year < cur.Year); // earliest
        return false;
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
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(album.Tracks);
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
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(allTracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    /// <summary>
    /// Collects all tracks from the currently displayed rows: the Songs section first
    /// (loose tracks an artist page surfaces), then album tracks, de-duplicated so a
    /// song pill whose track also sits in a displayed album isn't queued twice.
    /// </summary>
    private List<Track> GetAllFilteredTracks()
    {
        var tracks = new List<Track>();
        var seen = new HashSet<Guid>();
        foreach (var row in FilteredAlbumRows)
        {
            switch (row)
            {
                case ArtistSongsRow songsRow:
                    foreach (var song in songsRow.Songs)
                        if (seen.Add(song.Track.Id))
                            tracks.Add(song.Track);
                    break;
                case AlbumRow albumRow:
                    foreach (var album in albumRow.Albums)
                        foreach (var track in album.Tracks ?? new())
                            if (seen.Add(track.Id))
                                tracks.Add(track);
                    break;
            }
        }
        return tracks;
    }

    /// <summary>The artist page's Songs section in display (rank) order.</summary>
    private List<Track> GetArtistSongsInOrder() => FilteredAlbumRows
        .OfType<ArtistSongsRow>()
        .SelectMany(r => r.Songs)
        .Select(s => s.Track)
        .ToList();

    // ── Track-level commands for the artist page's Songs section ──

    [RelayCommand]
    private void PlayArtistSong(Track track)
    {
        var songs = GetArtistSongsInOrder();
        if (songs.Count == 0) return;
        var index = songs.IndexOf(track);
        _player.ReplaceQueueAndPlay(songs, index < 0 ? 0 : index);
    }

    [RelayCommand]
    private void ShuffleArtistSongs()
    {
        var songs = GetArtistSongsInOrder();
        if (songs.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(songs);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextTrack(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddTrackToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task AddTrackToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task ToggleTrackFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveTrackUserStateAsync(new[] { track });
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private async Task OpenTrackMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private void SearchLyricsTrack(Track track)
    {
        _searchLyricsAction?.Invoke(track);
    }

    [RelayCommand]
    private void ShowInExplorerTrack(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task RemoveTrackFromLibrary(Track track)
    {
        if (track == null) return;
        await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, new List<Track> { track });
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

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
    private async Task ToggleAlbumFavorites(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        var changed = new List<Track>();
        foreach (var a in albums)
        {
            if (a.Tracks == null || a.Tracks.Count == 0) continue;
            var newState = !a.IsAllTracksFavorite;
            foreach (var track in a.Tracks)
            {
                track.IsFavorite = newState;
                changed.Add(track);
            }
        }
        await _library.SaveTrackUserStateAsync(changed);
        _library.NotifyFavoritesChanged();
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Album album)
    {
        // Multi-album selection: edit every track across the selected albums in the
        // shared multi-select editor (Mixed fields, edits fan out to all tracks).
        if (CtrlSelectedAlbums.Count > 1)
        {
            var tracks = CtrlSelectedAlbums.SelectMany(a => a.Tracks ?? new()).ToList();
            CtrlSelectedAlbums.Clear();
            await MetadataHelper.OpenBatchMetadataWindow(tracks);
            return;
        }

        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        // Album-scoped: edit the whole album (Mixed fields, edits fan out to all tracks)
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private async Task BatchEditAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenBatchMetadataWindow(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ConvertAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenAudioConverterDialog(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ScanAlbumReplayGain(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenReplayGainScannerDialog(album.Tracks.ToList());
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
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums.ToList() : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (!await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, tracks))
            return;
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
        _settings.PropertyChanged -= _settingsPropertyChangedHandler;
        if (_viewStateLoadedHandler != null)
        {
            _settings.ViewStateLoaded -= _viewStateLoadedHandler;
            _viewStateLoadedHandler = null;
        }
        if (_searchDebounce != null)
        {
            _searchDebounce.Stop();
            _searchDebounce = null;
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
