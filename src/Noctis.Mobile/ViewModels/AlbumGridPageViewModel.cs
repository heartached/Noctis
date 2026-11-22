using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>What the Albums page's sort menu orders by; Title (A→Z) is the page's own order.</summary>
public enum AlbumSort { Title, Artist, Year, RecentlyAdded, MostPlayed }

/// <summary>All albums, two per row, with "Find in Albums" filtering by title or artist as you
/// type and the header's sort button ordering them (remembered, as a song list's sort is).
/// Rows (not a wrap panel) so the list virtualises — the same reason the desktop grid is
/// rows of albums (AlbumRow).</summary>
public sealed partial class AlbumGridPageViewModel : MobilePage, ISortable
{
    internal const int Columns = 2;

    /// <summary>The key the page's sort is saved under (the Albums tab and Library's Albums page share it).</summary>
    internal const string SortKey = "Nav.Albums";

    public AlbumGridPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        SortChoices = new[]
        {
            new SortChoice("Mobile.Sort.Title", AlbumSort.Title) { IsSelected = true },
            new SortChoice("Mobile.Sort.Artist", AlbumSort.Artist),
            new SortChoice("Mobile.Sort.ReleaseYear", AlbumSort.Year),
            new SortChoice("Mobile.RecentlyAdded", AlbumSort.RecentlyAdded),
            new SortChoice("Mobile.Sort.MostPlayed", AlbumSort.MostPlayed),
        };
        ApplySavedSort();
        Shell.ListSortChanged += OnListSortChanged;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Albums");

    public BulkObservableCollection<AlbumRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums), nameof(ShowNoResults))]
    private int _albumCount;

    /// <summary>The albums the search leaves (all of them without one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private int _resultCount;

    /// <summary>The "Find in Albums" text (FindField, as the Artists page's); the grid follows it as it changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    private string _query = string.Empty;

    public bool HasAlbums => AlbumCount > 0;

    public bool IsFiltering => !string.IsNullOrWhiteSpace(Query);

    public bool ShowNoResults => HasAlbums && ResultCount == 0;

    /// <summary>The albums the grid shows, in grid order.</summary>
    public IReadOnlyList<Album> Albums => Rows.SelectMany(r => r.Albums).ToList();

    // The source's albums as the library lists them, and the same in the sort in use (their
    // search keys are cached on the Album). A search filters the sorted list: typing never re-sorts.
    private List<Album> _source = new();
    private List<Album> _all = new();

    public void Refresh()
    {
        _source = Shell.Library.SourceAlbums().ToList();
        AlbumCount = _source.Count;
        ApplySort();
    }

    // ── Sort ──

    public IReadOnlyList<SortChoice> SortChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private AlbumSort _sort = AlbumSort.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private bool _isDescending;

    /// <summary>The sort button lights up away from the page's own order (A→Z).</summary>
    public bool IsSorted => Sort != AlbumSort.Title || IsDescending;

    public bool HasSortDirection => true;

    /// <summary>The header's sort button: the sort menu on the glass pop menu, out of the button.</summary>
    [RelayCommand]
    private void ShowSortMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForSort(Shell, this));

    public void ChooseSort(SortChoice choice)
    {
        foreach (var c in SortChoices) c.IsSelected = ReferenceEquals(c, choice);
        var sort = (AlbumSort)choice.Key;
        // As a song list's: names A→Z, years, dates and counts newest/most first.
        IsDescending = sort is AlbumSort.Year or AlbumSort.RecentlyAdded or AlbumSort.MostPlayed;
        Sort = sort;
        ApplySort();
        Shell.SaveListSort(SortKey, Sort, IsDescending, AlbumSort.Title);
    }

    public void ChooseDirection(bool descending)
    {
        IsDescending = descending;
        ApplySort();
        Shell.SaveListSort(SortKey, Sort, IsDescending, AlbumSort.Title);
    }

    /// <summary>Puts the page in its saved sort (A→Z when none is saved); true when that changed it.</summary>
    private bool ApplySavedSort()
    {
        if (!Shell.TryGetListSort<AlbumSort>(SortKey, out var sort, out var descending))
            (sort, descending) = (AlbumSort.Title, false);
        if (sort == Sort && descending == IsDescending) return false;
        foreach (var c in SortChoices) c.IsSelected = Equals(c.Key, sort);
        IsDescending = descending;
        Sort = sort;
        return true;
    }

    // The saved sorts were read (the tab's page made during a cold start), or the other page of
    // the albums was sorted (the Albums tab follows Library's Albums page).
    private void OnListSortChanged(object? sender, string? key)
    {
        if ((key == null || key == SortKey) && ApplySavedSort()) ApplySort();
    }

    private void ApplySort()
    {
        _all = Order(_source);
        ApplyFilter();
    }

    /// <summary>The albums in the sort in use. LINQ reads each album's key once per sort, so
    /// Recently Added's newest song and Most Played's summed plays walk each album's songs once.</summary>
    private List<Album> Order(List<Album> albums)
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<Album> By<TKey>(Func<Album, TKey> key, IComparer<TKey>? comparer = null) =>
            IsDescending ? albums.OrderByDescending(key, comparer) : albums.OrderBy(key, comparer);
        var ordered = Sort switch
        {
            AlbumSort.Artist => By(a => a.Artist, names),
            AlbumSort.Year => By(a => a.Year),
            AlbumSort.RecentlyAdded => By(a => a.Tracks is { Count: > 0 } tracks ? tracks.Max(t => t.DateAdded) : DateTime.MinValue),
            AlbumSort.MostPlayed => By(a => a.Tracks?.Sum(t => (long)t.PlayCount) ?? 0),
            _ => By(a => a.Name, names),
        };
        // Ties read naturally: by title, then artist.
        return ordered.ThenBy(a => a.Name, names).ThenBy(a => a.Artist, names).ToList();
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    [RelayCommand] private void ClearQuery() => Query = string.Empty;

    /// <summary>The desktop's search rule (the Artists page's): accent/case/punctuation-insensitive
    /// substring of the album's title or artist, or a raw substring for a query of punctuation only.</summary>
    private void ApplyFilter()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ApplyFilterCore(); }
        finally { Services.FrameStallMonitor.AddTime("find filter", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void ApplyFilterCore()
    {
        var raw = (Query ?? string.Empty).Trim();
        List<Album> albums;
        if (raw.Length == 0)
        {
            albums = _all;
        }
        else
        {
            var key = SearchText.Normalize(raw);
            albums = _all.Where(a => key.Length > 0
                    ? a.SearchNameKey.Contains(key, StringComparison.Ordinal) || a.SearchArtistKey.Contains(key, StringComparison.Ordinal)
                    : a.Name.Contains(raw, StringComparison.OrdinalIgnoreCase) || a.Artist.Contains(raw, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        ResultCount = albums.Count;
        // A rescan republishes every 1.5 s with rebuilt Album objects: rebuilding unchanged rows
        // re-realised every tile and blanked the covers. A kept tile opens the current album
        // (ShellViewModel.OpenAlbum resolves it by id).
        if (SameTiles(Rows.SelectMany(r => r.Albums).ToList(), albums)) return;
        Rows.ReplaceAll(albums.Chunk(Columns).Select(chunk => new AlbumRow { Albums = chunk.ToList() }));
    }

    /// <summary>What a tile shows (AlbumTile: cover, name, artist), in the same order.</summary>
    private static bool SameTiles(IReadOnlyList<Album> shown, IReadOnlyList<Album> next)
    {
        if (shown.Count != next.Count) return false;
        for (var i = 0; i < next.Count; i++)
        {
            var (a, b) = (shown[i], next[i]);
            if (a.Id != b.Id || a.Name != b.Name || a.Artist != b.Artist || a.ArtworkPath != b.ArtworkPath) return false;
        }
        return true;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed()
    {
        Shell.Library.Refreshed -= OnRefreshed;
        Shell.ListSortChanged -= OnListSortChanged;
    }
}
