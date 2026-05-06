using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MoreByArtistView : UserControl
{
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();

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
        if (sender is not ContextMenu ctx) return;

        // Single-album right-click on this page; clear any stale ctrl-selection on the shared VM
        albumsVm.CtrlSelectedAlbums = new List<Album>();

        if (!_playlistPopulators.TryGetValue(ctx, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in ctx.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[ctx] = populator;
        }

        var album = ctx.DataContext as Album;
        populator.Populate(albumsVm.Playlists, albumsVm.AddToExistingPlaylistCommand,
            playlist => new object[] { album!, playlist });
    }

}
