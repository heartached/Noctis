using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MoreByArtistView : UserControl
{
    public MoreByArtistView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (e.NewSize.Width <= 0 || DataContext is not MoreByArtistViewModel vm)
            return;

        var usable = e.NewSize.Width - 64;
        var tileContentWidth = usable / 5.0 - 8;
        var newSize = Math.Max(96, tileContentWidth);

        if (Math.Abs(newSize - vm.TileArtworkSize) < 0.5)
            return;

        var savedY = AlbumScrollViewer.Offset.Y;
        vm.TileArtworkSize = newSize;

        if (savedY > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AlbumScrollViewer.Offset = new Vector(
                    0,
                    Math.Min(savedY, Math.Max(0, AlbumScrollViewer.Extent.Height - AlbumScrollViewer.Viewport.Height)));
            }, DispatcherPriority.Background);
        }
    }

    private void OnAlbumContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MoreByArtistViewModel vm) return;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return;

        // Single-album right-click on this page; clear any stale ctrl-selection on the shared VM
        albumsVm.CtrlSelectedAlbums = new List<Album>();
    }
}
