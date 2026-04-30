using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void OnAlbumContextMenuOpened(object? sender, RoutedEventArgs e)
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
