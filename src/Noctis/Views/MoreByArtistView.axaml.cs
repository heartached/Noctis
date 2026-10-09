using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Localization;
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

        // Mirror LibraryAlbumsView sizing so artwork fills the 5 columns identically:
        // ScrollViewer Padding is 12,_,2 (14px horiz) + each tile Margin="2" (4px horiz).
        var usable = e.NewSize.Width - 24;
        var tileContentWidth = usable / 5.0 - 8;
        var newSize = Math.Max(80, tileContentWidth);

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

    // ── Album tiles: one shared v2 menu (10-09 redesign), bound to the tile on open ──
    // Replaces the per-tile XAML ContextMenu: same commands (through the shared
    // LibraryAlbumsVm) and parameters, plus the entries the Albums page tile menu has.

    private AlbumContextMenuBuilder? _albumMenuBuilder;
    /// <summary>Culture the menu's strings were read in (they are read once, at Build).</summary>
    private string? _albumMenuCulture;
    private Control? _menuOwner;

    private void OnAlbumTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenAlbumMenu(tile))
            e.Handled = true;
    }

    private bool OpenAlbumMenu(Button tile)
    {
        if (tile.DataContext is not Album album) return false;
        if (DataContext is not MoreByArtistViewModel vm) return false;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return false;

        // Single-album right-click on this page; clear any stale ctrl-selection on the shared VM
        albumsVm.CtrlSelectedAlbums = new List<Album>();

        if (_albumMenuBuilder == null || _albumMenuCulture != Loc.Instance.Culture.Name)
        {
            _albumMenuCulture = Loc.Instance.Culture.Name;
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), this, v2: true, removeIsDanger: true);
        }

        _albumMenuBuilder.Bind(
            album,
            playCommand: albumsVm.PlayAlbumCommand,
            shuffleCommand: albumsVm.ShuffleAlbumCommand,
            playNextCommand: albumsVm.PlayNextCommand,
            addToQueueCommand: albumsVm.AddToQueueCommand,
            addToPlaylistCommand: albumsVm.AddToNewPlaylistCommand,
            toggleFavoritesCommand: albumsVm.ToggleAlbumFavoritesCommand,
            openMetadataCommand: albumsVm.OpenMetadataCommand,
            showInExplorerCommand: albumsVm.ShowInExplorerCommand,
            removeCommand: albumsVm.RemoveFromLibraryCommand,
            convertCommand: albumsVm.ConvertAlbumCommand,
            scanReplayGainCommand: albumsVm.ScanAlbumReplayGainCommand,
            searchLyricsCommand: albumsVm.SearchLyricsAlbumCommand);

        OpenMenu(_albumMenuBuilder.Menu, tile);
        return true;
    }

    private void OpenMenu(ContextMenu menu, Control owner)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        if (menu.IsOpen)
            menu.Close();

        // Detach from the previous owner so Open() doesn't throw
        // "Cannot show ContextMenu on a different control".
        if (_menuOwner != null && !ReferenceEquals(_menuOwner, owner))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, owner))
            prev.ContextMenu = null;

        _menuOwner = owner;
        owner.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(owner);
    }

    /// <summary>Tile hover dots: the same menu a right-click on the tile opens, bound
    /// afresh (the tile may still hold the shared menu from an older language or album).</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                OpenAlbumMenu(tile);
                break;
            }
        }
        e.Handled = true;
    }
}
