using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryPlaylistsView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    public LibraryPlaylistsView()
    {
        InitializeComponent();
    }

    /// <summary>Tile dots button: the same menu a right-click on the tile opens. Always
    /// (re)bound from the tile, which may still hold the shared menu bound to another playlist.</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                OpenPlaylistMenu(tile);
                break;
            }
        }
        e.Handled = true;
    }

    // ── Playlist tiles: one shared v2 menu (10-09 redesign), bound to the tile on open ──
    // Replaces the per-tile XAML ContextMenu: same commands and parameters, now as quick
    // tiles (Play / Shuffle / Play Next / Add to Queue) over grouped rows.

    private PlaylistTileMenu? _tileMenu;
    /// <summary>Culture the menu's strings were read in (they are read once, at build).</summary>
    private string? _tileMenuCulture;
    private Control? _menuOwner;

    private void OnPlaylistTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenPlaylistMenu(tile))
            e.Handled = true;
    }

    private bool OpenPlaylistMenu(Button tile)
    {
        if (tile.DataContext is not PlaylistNavItem item) return false;
        if (DataContext is not LibraryPlaylistsViewModel vm) return false;

        // Rebuilt after a live language switch so the menu follows it.
        if (_tileMenu == null || _tileMenuCulture != Loc.Instance.Culture.Name)
        {
            if (_tileMenu?.Menu.IsOpen == true) _tileMenu.Menu.Close();
            _tileMenuCulture = Loc.Instance.Culture.Name;
            _tileMenu = new PlaylistTileMenu(this);
        }
        _tileMenu.Bind(item, vm);

        var menu = _tileMenu.Menu;
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        MenuOpenAnimation.CloseNow(menu);
        // Detach from the previous owner so Open() doesn't throw
        // "Cannot show ContextMenu on a different control".
        if (_menuOwner != null && !ReferenceEquals(_menuOwner, tile))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, tile))
            prev.ContextMenu = null;
        _menuOwner = tile;
        tile.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(tile);
        return true;
    }

    /// <summary>
    /// The playlist tile's v2 menu: [tiles] · [View, Edit, Export, Star / Unstar] · [Delete].
    /// </summary>
    internal sealed class PlaylistTileMenu
    {
        public ContextMenu Menu { get; } = new();
        public Button QuickPlay { get; }
        public Button QuickShuffle { get; }
        public Button QuickPlayNext { get; }
        public Button QuickAddToQueue { get; }
        public MenuItem View { get; }
        public MenuItem Edit { get; }
        public MenuItem Export { get; }
        public MenuItem Star { get; }
        public MenuItem Unstar { get; }
        public MenuItem Delete { get; }

        public PlaylistTileMenu(Control host)
        {
            Menu.Classes.Add(MenuV2.MenuClass);
            var items = Menu.Items;
            QuickPlay = MenuV2.Tile(host, Menu, "MenuLinePlay", Loc.T("LibraryPlaylists.Play"));
            QuickShuffle = MenuV2.Tile(host, Menu, "MenuLineShuffle", Loc.T("LibraryPlaylists.Shuffle"));
            QuickPlayNext = MenuV2.Tile(host, Menu, "MenuLinePlayNext", Loc.T("LibraryPlaylists.PlayNext"));
            QuickAddToQueue = MenuV2.Tile(host, Menu, "MenuLineQueue", Loc.T("LibraryPlaylists.AddQueue"));
            items.Add(MenuV2.TileRow(QuickPlay, QuickShuffle, QuickPlayNext, QuickAddToQueue));

            items.Add(new Separator());
            items.Add(View = MenuV2.Row(host, Loc.T("LibraryPlaylists.ViewPlaylist"), "MenuLinePlaylist"));
            items.Add(Edit = MenuV2.Row(host, Loc.T("LibraryPlaylists.EditPlaylist"), "MenuLineEdit"));
            items.Add(Export = MenuV2.Row(host, Loc.T("LibraryPlaylists.ExportPlaylist"), "MenuLineExport"));
            items.Add(Star = MenuV2.Row(host, Loc.T("LibraryPlaylists.StarSidebar"), "MenuLineStar"));
            items.Add(Unstar = MenuV2.Row(host, Loc.T("LibraryPlaylists.UnstarFromSidebar"), "MenuLineStar"));
            MenuV2.IconPath(Unstar.Icon)?.Classes.Add("mv2-pinned");

            items.Add(new Separator());
            Delete = MenuV2.Row(host, Loc.T("LibraryPlaylists.DeletePlaylist"), "MenuLineTrash");
            Delete.Classes.Add("danger");
            items.Add(Delete);
        }

        public void Bind(PlaylistNavItem item, LibraryPlaylistsViewModel vm)
        {
            Menu.DataContext = item;
            Set(QuickPlay, vm.PlayPlaylistCommand, item);
            Set(QuickShuffle, vm.ShufflePlaylistCommand, item);
            Set(QuickPlayNext, vm.PlayNextPlaylistCommand, item);
            Set(QuickAddToQueue, vm.AddPlaylistToQueueCommand, item);
            Set(View, vm.OpenPlaylistCommand, item);
            Set(Edit, vm.EditPlaylistCommand, item);
            Set(Export, vm.ExportPlaylistCommand, item);
            Set(Star, vm.TogglePinCommand, item);
            Set(Unstar, vm.TogglePinCommand, item);
            Set(Delete, vm.DeletePlaylistCommand, item);
            Star.IsVisible = !item.IsPinned;
            Unstar.IsVisible = item.IsPinned;
            MenuV2.RefreshLayout(Menu.Items);
        }

        private static void Set(Button b, System.Windows.Input.ICommand c, object p) { b.Command = c; b.CommandParameter = p; }
        private static void Set(MenuItem m, System.Windows.Input.ICommand c, object p) { m.Command = c; m.CommandParameter = p; }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is LibraryPlaylistsViewModel vm)
        {
            var sv = this.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is LibraryPlaylistsViewModel vm && vm.SavedScrollOffset > 0)
        {
            Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;

                // The give-up check comes FIRST. It used to sit after `if (sv == null)
                // return;`, so if FindDescendantOfType<ScrollViewer>() ever failed to
                // resolve, opacity was never restored and the whole tab rendered blank
                // with no way out.
                var sv = this.FindDescendantOfType<ScrollViewer>();
                if (sv == null)
                {
                    if (attempts >= 10)
                    {
                        Opacity = 1;
                        CancelPendingScrollRestore();
                    }
                    return;
                }

                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                Opacity = 1;
                CancelPendingScrollRestore();
            };

            LayoutUpdated += _pendingScrollRestore;
        }
    }
}
