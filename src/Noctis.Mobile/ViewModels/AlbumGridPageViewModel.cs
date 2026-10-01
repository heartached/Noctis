using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>All albums, two per row. Rows (not a wrap panel) so the list virtualises — the
/// same reason the desktop grid is rows of albums (AlbumRow).</summary>
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
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    private int _albumCount;

    public bool HasAlbums => AlbumCount > 0;

    public void Refresh()
    {
        var albums = Shell.Library.Service.Albums
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Rows.ReplaceAll(albums.Chunk(Columns).Select(chunk => new AlbumRow { Albums = chunk.ToList() }));
        AlbumCount = albums.Count;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}
