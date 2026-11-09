using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>What a song list's sort menu orders by; Default is the list's own order (a playlist's saved order).</summary>
public enum SongSort { Default, Title, Artist, Album, DateAdded, Year, Duration, MostPlayed, RecentlyPlayed }

/// <summary>
/// A flat list of songs: all songs, favourites, recently added, a playlist (and, until the
/// album and artist pages land, an album or an artist). The rows come from a source function
/// re-read on every library refresh, so a rescan or a heart toggle shows in an open list.
/// The toolbar sorts, filters (favourites, downloaded) and searches the list; Play and Shuffle
/// play what is shown.
/// </summary>
public sealed partial class SongListPageViewModel : MobilePage, ISortable
{
    private readonly string _title;
    private readonly Func<IEnumerable<Track>> _source;
    private readonly Playlist? _playlist;
    private readonly string? _sortKey;
    private List<Track> _all = new();

    /// <param name="sortKey">The list whose sort is remembered (Songs, Favorites, a playlist):
    /// the page opens in the sort it was left in. Null for lists that always open in their own
    /// order (an artist's top songs, a mix).</param>
    public SongListPageViewModel(ShellViewModel shell, string title, Func<IEnumerable<Track>> source, Playlist? playlist = null, string? sortKey = null)
    {
        Shell = shell;
        _title = title;
        _source = source;
        _playlist = playlist;
        SortChoices = new[]
        {
            new SortChoice(playlist != null ? "Mobile.Sort.PlaylistOrder" : "Mobile.Sort.DefaultOrder", SongSort.Default) { IsSelected = true },
            new SortChoice("Mobile.Sort.Title", SongSort.Title),
            new SortChoice("Mobile.Sort.Artist", SongSort.Artist),
            new SortChoice("Mobile.Sort.Album", SongSort.Album),
            new SortChoice("Mobile.Sort.DateAdded", SongSort.DateAdded),
            new SortChoice("Mobile.Sort.ReleaseYear", SongSort.Year),
            new SortChoice("Mobile.Sort.Time", SongSort.Duration),
            new SortChoice("Mobile.Sort.MostPlayed", SongSort.MostPlayed),
            new SortChoice("Mobile.RecentlyPlayed", SongSort.RecentlyPlayed),
        };
        Selection = new TrackSelection(shell, () => Songs, () => Title, () => Playlist);
        _sortKey = sortKey ?? (playlist != null ? PlaylistSortKey(playlist) : null);
        if (_sortKey != null)
        {
            ApplySavedSort();
            Shell.ListSortChanged += OnListSortChanged;
        }
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    /// <summary>The key a playlist's page saves its sort under.</summary>
    internal static string PlaylistSortKey(Playlist playlist) => "Playlist:" + playlist.Id.ToString("N");

    public ShellViewModel Shell { get; }

    /// <summary>Select mode: ticks, the "N Selected" bar and the shell's action bar.</summary>
    public TrackSelection Selection { get; }

    /// <summary>The tools' Select button: Select mode with nothing ticked yet.</summary>
    [RelayCommand]
    private void BeginSelect() => Selection.Begin();

    /// <summary>A playlist's page reads the name the library holds now (a rename shows at once).</summary>
    public override string Title => Playlist?.Name ?? Localized(_title);

    /// <summary>The playlist this page shows, as the library holds it now; null for other lists.</summary>
    public Playlist? Playlist => _playlist == null ? null : Shell.CurrentPlaylist(_playlist);

    /// <summary>A playlist's page has its own ⋯ (Rename, Delete Playlist and the playlist's actions).</summary>
    public bool IsPlaylist => _playlist != null;

    /// <summary>A playlist page's cover over its songs: the custom cover when it has one, else the
    /// cover its tiles show (<see cref="LibraryViewModel.PlaylistArtwork"/>); null without art
    /// (the page shows the placeholder). Re-read on each library refresh, so Change Cover, Remove
    /// Cover and a synced cover show at once; raised only when the path changes.</summary>
    public string? CoverPath { get; private set; }

    private void RefreshCover()
    {
        var path = Playlist is { } playlist ? Shell.Library.PlaylistArtwork(playlist) : null;
        if (path == CoverPath) return;
        CoverPath = path;
        OnPropertyChanged(nameof(CoverPath));
    }

    /// <summary>The title row's ⋯ on a playlist's page: the playlist's glass menu.</summary>
    [RelayCommand]
    private void ShowPlaylistMenu()
    {
        if (Playlist is { } playlist) Shell.OpenPlaylistSheetCommand.Execute(playlist);
    }

    /// <summary>A row's ⋯ (or a long press): the song's menu, which on a playlist's page also
    /// offers Remove from Playlist.</summary>
    [RelayCommand]
    private void OpenSongMenu(Track? track)
    {
        if (track == null) return;
        if (Selection.IsActive) Selection.Toggle(track);   // a long press in Select mode ticks, as a tap does
        else Shell.OpenTrackMenu(track, Playlist, Selection);
    }

    /// <summary>The rows shown: the source, filtered and sorted.</summary>
    public BulkObservableCollection<Track> Songs { get; } = new();

    /// <summary>The whole list's size; the toolbar stays while a filter hides every row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSongs), nameof(CountText))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(ShowEmpty), nameof(EmptyText))]
    private int _songCount;

    public bool HasSongs => TotalCount > 0;

    public bool ShowEmpty => SongCount == 0;

    public string EmptyText => TotalCount == 0 ? Loc.T("Mobile.SongList.NoSongs") : Loc.T("Mobile.SongList.NoMatch");

    public string CountText => SongCount == TotalCount
        ? Loc.Plural("Mobile.Count.Songs", SongCount)
        : Loc.T("Mobile.SongList.CountOf", SongCount, TotalCount);

    // ── Search in the list (the page's FindField, as the Artists page's) ──

    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => Apply();

    // ── Filters ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    private bool _onlyFavourites;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    private bool _onlyDownloaded;

    partial void OnOnlyFavouritesChanged(bool value) => Apply();
    partial void OnOnlyDownloadedChanged(bool value) => Apply();

    /// <summary>The filter button lights up while a filter is on.</summary>
    public bool IsFiltered => OnlyFavourites || OnlyDownloaded;

    /// <summary>Downloaded means a desktop song's offline copy: offered while signed in to a
    /// computer, or while a song in this list is one (the computer revoked this phone: its songs
    /// and copies stay). Signed out with none, the filter could only empty the list (it offered
    /// itself whenever the account service existed: "0 of 11", 10-14), and so it did after a sign
    /// out that kept the downloads: their songs leave the library, the files wait for a sign-in.</summary>
    public bool CanFilterDownloaded => OffersDownloadedFilter && Shell.Account is { } account
        && (account.IsSignedIn || _all.Any(account.IsDownloaded));

    // A list that is already one of the filters does not offer it: the Favorites list's filter
    // menu offered Favorites, the Downloaded list's Downloaded, and both did nothing.
    public bool OffersFavouritesFilter { get; init; } = true;
    public bool OffersDownloadedFilter { get; init; } = true;

    /// <summary>The filter button: only with a filter to offer.</summary>
    public bool HasFilters => OffersFavouritesFilter || CanFilterDownloaded;

    // ── Sort ──

    public IReadOnlyList<SortChoice> SortChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private SongSort _sort = SongSort.Default;

    // Notifies IsSorted itself: choosing the list's own order again while it was reversed
    // changes only the direction, and the button stayed lit.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private bool _isDescending;

    /// <summary>The sort button lights up away from the list's own order.</summary>
    public bool IsSorted => Sort != SongSort.Default || IsDescending;

    public bool HasSortDirection => true;

    /// <summary>The toolbar's sort button: the sort menu on the glass pop menu, out of the button.</summary>
    [RelayCommand]
    private void ShowSortMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForSort(Shell, this));

    /// <summary>The toolbar's filter button: the filter menu on the glass pop menu.</summary>
    [RelayCommand]
    private void ShowFilterMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForFilter(Shell, this));

    public void ChooseSort(SortChoice choice)
    {
        foreach (var c in SortChoices) c.IsSelected = ReferenceEquals(c, choice);
        var sort = (SongSort)choice.Key;
        // Each order starts the way it is usually read: names A→Z, dates and counts newest/most first.
        IsDescending = sort is SongSort.DateAdded or SongSort.Year or SongSort.MostPlayed or SongSort.RecentlyPlayed;
        Sort = sort;
        Apply();
        SaveSort();
    }

    public void ChooseDirection(bool descending)
    {
        IsDescending = descending;
        Apply();
        SaveSort();
    }

    private void SaveSort()
    {
        if (_sortKey != null) Shell.SaveListSort(_sortKey, Sort, IsDescending, SongSort.Default);
    }

    /// <summary>Puts the list in its saved sort (its own order when none is saved); true when that changed it.</summary>
    private bool ApplySavedSort()
    {
        if (_sortKey == null) return false;
        if (!Shell.TryGetListSort<SongSort>(_sortKey, out var sort, out var descending))
            (sort, descending) = (SongSort.Default, false);
        if (sort == Sort && descending == IsDescending) return false;
        foreach (var c in SortChoices) c.IsSelected = Equals(c.Key, sort);
        IsDescending = descending;
        Sort = sort;
        return true;
    }

    // The saved sorts were read (a page made during a cold start), or another page of this list
    // was sorted (the Favorites tab follows a Favorites page opened from Library).
    private void OnListSortChanged(object? sender, string? key)
    {
        if ((key == null || key == _sortKey) && ApplySavedSort()) Apply();
    }

    public void Refresh()
    {
        _all = _source().ToList();
        TotalCount = _all.Count;
        if (_playlist != null)
        {
            OnPropertyChanged(nameof(Title));   // renamed since
            RefreshCover();
        }
        OnPropertyChanged(nameof(HasFilters));   // signed in or out since
        // A filter no longer offered does not stay on: with the filter button gone too (a
        // Favorites list) nothing could turn it off, and the list stayed empty.
        if (OnlyDownloaded && !CanFilterDownloaded)
        {
            _onlyDownloaded = false;
            OnPropertyChanged(nameof(OnlyDownloaded));
            OnPropertyChanged(nameof(IsFiltered));
        }
        Apply();
    }

    private void Apply()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ApplyCore(); }
        finally { Services.FrameStallMonitor.AddTime("find filter", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void ApplyCore()
    {
        IEnumerable<Track> rows = _all;
        if (OnlyFavourites) rows = rows.Where(t => t.IsFavorite);
        if (OnlyDownloaded && Shell.Account is { } account) rows = rows.Where(account.IsDownloaded);
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var match = new MobileSearch.Matcher(FilterText);
            rows = rows.Where(match.HitTrack);
        }
        // Only when the rows differ: every library refresh (a heart, a scan's progress) re-runs
        // this, and a Reset on an unchanged list tore down and re-realised every row on screen.
        MobileLibrary.ReplaceIfChanged(Songs, Order(rows).ToList());
        SongCount = Songs.Count;
        Selection.Prune();
    }

    private IEnumerable<Track> Order(IEnumerable<Track> rows)
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        if (Sort == SongSort.Default) return IsDescending ? rows.Reverse() : rows;
        // The playlist as the library holds it now: an edit re-reads it into a new object.
        var playlist = _playlist == null ? null : Shell.CurrentPlaylist(_playlist);
        IOrderedEnumerable<Track> By<TKey>(Func<Track, TKey> key, IComparer<TKey>? comparer = null) =>
            IsDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        var ordered = Sort switch
        {
            SongSort.Title => By(t => t.Title, names),
            SongSort.Artist => By(t => t.Artist, names),
            SongSort.Album => By(t => t.Album, names),
            SongSort.DateAdded => By(t => playlist?.AddedDateFor(t.Id, t.DateAdded) ?? t.DateAdded),
            SongSort.Year => By(t => t.Year),
            SongSort.Duration => By(t => t.Duration),
            SongSort.MostPlayed => By(t => t.PlayCount),
            SongSort.RecentlyPlayed => By(t => t.LastPlayed ?? DateTime.MinValue),
            _ => By(t => t.Title, names),
        };
        // Ties read naturally: by album, then its track order, then title.
        return ordered.ThenBy(t => t.Album, names).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title, names);
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    /// <summary>A row tap: play this list from that row.</summary>
    [RelayCommand]
    private void Play(Track? track)
    {
        if (track == null) return;
        if (Selection.IsActive)
        {
            Selection.Toggle(track);
            return;
        }
        var list = Songs.ToList();
        var index = list.IndexOf(track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, Title);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Songs.Count > 0) Shell.Player.PlayTracks(Songs.ToList(), 0, Title);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Songs.Count > 0) Shell.Player.PlayShuffled(Songs.ToList(), source: Title);
    }

    public override void OnClosed()
    {
        Selection.End();
        Shell.Library.Refreshed -= OnRefreshed;
        Shell.ListSortChanged -= OnListSortChanged;
    }
}
