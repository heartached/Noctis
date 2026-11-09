using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>All albums, two per row, with "Find in Albums" filtering by title or artist as you
/// type. Rows (not a wrap panel) so the list virtualises — the same reason the desktop grid is
/// rows of albums (AlbumRow).</summary>
public sealed partial class AlbumGridPageViewModel : MobilePage
{
    internal const int Columns = 2;

    public AlbumGridPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
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

    // Every album A→Z (its search keys are cached on the Album).
    private List<Album> _all = new();

    public void Refresh()
    {
        _all = Shell.Library.Service.Albums
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AlbumCount = _all.Count;
        ApplyFilter();
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

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}
