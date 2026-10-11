using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class FavoritesView : UserControl
{
    private EventHandler? _pendingScrollRestore;
    // Track selection by the FavoriteItem itself (not the tile Button) so row
    // virtualization recycling the tiles on scroll doesn't drop the ctrl-selected
    // highlight. See MultiSelectHelper's "Data-tracked album-tile variants".
    private readonly HashSet<FavoriteItem> _selectedItems = new();

    public FavoritesView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a tile.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && !(source is Button b && b.Classes.Contains("album-tile")))
            source = source.Parent as Control;
        if (source is not Button tile) return;
        if (tile.DataContext is not FavoriteItem item) return;

        if (!MultiSelectHelper.HandleAlbumTileClickByData(tile, item, e, _selectedItems)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            // Plain left-click clears any existing selection (mirrors prior behavior).
            MultiSelectHelper.ClearAlbumSelectionsByData(_selectedItems, CollectTiles());
        }

        // Ensure this view has focus so Ctrl+A reaches OnViewKeyDown
        if (_selectedItems.Count > 0)
            Focus();
    }

    /// <summary>Re-apply the ctrl-selected visual as tiles are (re)realized on scroll.</summary>
    private void OnAlbumTileLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button tile)
            MultiSelectHelper.SyncAlbumTileVisual(tile, _selectedItems);
    }

    private List<Button> CollectTiles()
    {
        var allTiles = new List<Button>();
        foreach (var desc in this.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        return allTiles;
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectedItems.Count > 0)
        {
            MultiSelectHelper.ClearAlbumSelectionsByData(_selectedItems, CollectTiles());
            if (DataContext is FavoritesViewModel vm) vm.CtrlSelectedItems = new List<FavoriteItem>();
            e.Handled = true;
            return;
        }

        var allItems = (DataContext as FavoritesViewModel)?.FavoriteItemRows.SelectMany(r => r.Items)
                       ?? Enumerable.Empty<FavoriteItem>();
        MultiSelectHelper.HandleAlbumSelectAllByData(e, allItems, CollectTiles(), _selectedItems);
    }

    // ── Tile menu: the shared v2 menus (10-09 redesign), bound to the tile on open ──
    // A favourite song gets the track menu, a favourite album the album menu. The VM's
    // FavoriteItem commands (which act on the Ctrl-selection) stay the targets: the
    // builders hand their rows the Track / Album, and FavoriteItemCommand maps that back
    // to the item the menu was opened on.

    private TrackContextMenuBuilder? _trackMenuBuilder;
    private AlbumContextMenuBuilder? _albumMenuBuilder;
    /// <summary>Culture each menu's strings were read in (they are read once, at Build).</summary>
    private string? _trackMenuCulture, _albumMenuCulture;
    /// <summary>The album menu's View Album row (the album builder has no such entry).</summary>
    private MenuItem? _albumViewAlbum;
    private Control? _menuOwner;
    /// <summary>The item the open menu acts on.</summary>
    private FavoriteItem? _menuItem;
    private readonly Dictionary<ICommand, FavoriteItemCommand> _itemCommands = new();

    private FavoriteItemCommand ItemCommand(ICommand inner)
    {
        if (!_itemCommands.TryGetValue(inner, out var command))
            _itemCommands[inner] = command = new FavoriteItemCommand(inner, () => _menuItem);
        return command;
    }

    private void OnFavoriteTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenItemMenu(tile))
            e.Handled = true;
    }

    private bool OpenItemMenu(Button tile)
    {
        if (tile.DataContext is not FavoriteItem item) return false;
        if (DataContext is not FavoritesViewModel vm) return false;

        // Push ctrl-selected items to ViewModel so commands can operate on all of them
        vm.CtrlSelectedItems = _selectedItems.ToList();
        _menuItem = item;

        ContextMenu menu;
        if (item.IsAlbum)
        {
            var builder = GetAlbumMenu();
            builder.Bind(
                item.Album!,
                playCommand: ItemCommand(vm.PlayItemCommand),
                shuffleCommand: ItemCommand(vm.ShuffleItemCommand),
                playNextCommand: ItemCommand(vm.PlayNextItemCommand),
                addToQueueCommand: ItemCommand(vm.AddItemToQueueCommand),
                addToPlaylistCommand: ItemCommand(vm.AddItemToNewPlaylistCommand),
                toggleFavoritesCommand: ItemCommand(vm.RemoveItemFavoriteCommand),
                openMetadataCommand: ItemCommand(vm.OpenItemMetadataCommand),
                showInExplorerCommand: ItemCommand(vm.ShowItemInExplorerCommand),
                removeCommand: ItemCommand(vm.RemoveItemFromLibraryCommand),
                convertCommand: ItemCommand(vm.ConvertItemCommand),
                scanReplayGainCommand: ItemCommand(vm.ScanItemReplayGainCommand),
                searchLyricsCommand: vm.SearchLyricsAlbumCommand,
            snoozeCommand: vm.SnoozeAlbumForMonthCommand);
            _albumViewAlbum!.Command = ItemCommand(vm.ViewItemAlbumCommand);
            _albumViewAlbum.CommandParameter = item.Album;
            MenuV2.RefreshLayout(builder.Menu.Items);
            menu = builder.Menu;
        }
        else
        {
            var builder = GetTrackMenu();
            builder.Bind(
                item.Track!,
                playCommand: ItemCommand(vm.PlayItemCommand),
                shuffleCommand: ItemCommand(vm.ShuffleItemCommand),
                playNextCommand: ItemCommand(vm.PlayNextItemCommand),
                addToQueueCommand: ItemCommand(vm.AddItemToQueueCommand),
                addToPlaylistCommand: ItemCommand(vm.AddItemToNewPlaylistCommand),
                toggleFavoriteCommand: ItemCommand(vm.RemoveItemFavoriteCommand),
                openMetadataCommand: ItemCommand(vm.OpenItemMetadataCommand),
                searchLyricsCommand: vm.SearchLyricsTrackCommand,
                showInExplorerCommand: ItemCommand(vm.ShowItemInExplorerCommand),
                removeCommand: ItemCommand(vm.RemoveItemFromLibraryCommand),
                convertCommand: ItemCommand(vm.ConvertItemCommand),
                scanReplayGainCommand: ItemCommand(vm.ScanItemReplayGainCommand),
                viewAlbumCommand: ItemCommand(vm.ViewItemAlbumCommand),
                viewArtistCommand: vm.ViewArtistCommand);
            // The builder leaves Shuffle without a parameter (track lists shuffle the whole
            // list); the item command needs to know which favourite it is.
            builder.Shuffle.CommandParameter = item.Track;
            builder.QuickShuffle.CommandParameter = item.Track;
            menu = builder.Menu;
        }

        OpenMenu(menu, tile);
        return true;
    }

    private TrackContextMenuBuilder GetTrackMenu()
    {
        // Rebuilt after a live language switch so the menu follows it.
        if (_trackMenuBuilder == null || _trackMenuCulture != Loc.Instance.Culture.Name)
        {
            _trackMenuCulture = Loc.Instance.Culture.Name;
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build(Loc.T("Favorites.RemoveFromLibrary"), null, this, v2: true, removeIsDanger: true);
        }
        return _trackMenuBuilder;
    }

    private AlbumContextMenuBuilder GetAlbumMenu()
    {
        if (_albumMenuBuilder == null || _albumMenuCulture != Loc.Instance.Culture.Name)
        {
            _albumMenuCulture = Loc.Instance.Culture.Name;
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build(Loc.T("Favorites.RemoveFromLibrary"), this, v2: true, removeIsDanger: true);
            // The old Favorites menu offered View Album on albums too; it gets its own group
            // under the playlist/favourite rows, where the track menu keeps it.
            var items = _albumMenuBuilder.Menu.Items;
            var at = items.IndexOf(_albumMenuBuilder.Unfavorite) + 1;
            _albumViewAlbum = MenuV2.Row(this, Loc.T("Favorites.ViewAlbum"), "MenuLineAlbum");
            items.Insert(at, new Separator());
            items.Insert(at + 1, _albumViewAlbum);
        }
        return _albumMenuBuilder;
    }

    private void OpenMenu(ContextMenu menu, Control owner)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        MenuOpenAnimation.CloseNow(menu);

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

    /// <summary>Left-click handler: play track or open album depending on item type.</summary>
    private void OnFavoriteCardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FavoriteItem item) return;
        // Button.Click bubbles: the hover Play button inside the tile raises Click too and
        // it lands here with the inner button as Source. Only the tile's own click counts,
        // otherwise Play also opened the album / restarted the track.
        if (!ReferenceEquals(e.Source, button)) return;
        if (DataContext is not FavoritesViewModel vm) return;

        // If there are ctrl-selected tiles, a normal click already cleared them in the tunnel handler
        if (_selectedItems.Count > 0) return;

        if (item.IsAlbum)
            vm.OpenAlbumCommand.Execute(item.Album);
        else
            vm.PlayTrackCommand.Execute(item.Track);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (e.NewSize.Width <= 0 || DataContext is not FavoritesViewModel vm)
            return;

        // DockPanel has Margin="12,8,12,8" → 24px horizontal margin.
        // Column count and tile size (each tile carries 8px of margin+padding chrome)
        // are computed in the view model, which folds in the cover-size setting.
        var usable = e.NewSize.Width - 24;

        // Save and restore scroll position so sidebar hover doesn't reset scroll
        var sv = FavoritesList.FindDescendantOfType<ScrollViewer>();
        var savedY = sv?.Offset.Y ?? 0;

        if (!vm.UpdateGridMetrics(usable))
            return;

        if (sv != null && savedY > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = new Vector(0, Math.Min(savedY, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            }, DispatcherPriority.Background);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is FavoritesViewModel vm)
        {
            var sv = FavoritesList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        MultiSelectHelper.ClearAlbumSelectionsByData(_selectedItems, CollectTiles());
        if (DataContext is FavoritesViewModel selVm) selVm.CtrlSelectedItems = new List<FavoriteItem>();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            FavoritesList.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            FavoritesList.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is FavoritesViewModel vm && vm.SavedScrollOffset > 0)
        {
            FavoritesList.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = FavoritesList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                FavoritesList.Opacity = 1;
                CancelPendingScrollRestore();
            };

            FavoritesList.LayoutUpdated += _pendingScrollRestore;
        }
    }

    /// <summary>Tile hover dots: the same menu a right-click on the tile opens, bound
    /// afresh (a recycled tile may still hold the shared menu bound to another item).
    /// OpenItemMenu also pushes the current selection.</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                OpenItemMenu(tile);
                break;
            }
        }
        e.Handled = true;
    }
}

/// <summary>
/// A Favorites menu row's command: the shared builders hand each row the Track or Album,
/// while the view model's commands take the <see cref="FavoriteItem"/> (and act on the
/// Ctrl-selection it belongs to). Maps the row's parameter back to the item the menu was
/// opened on; anything else cannot run.
/// </summary>
internal sealed class FavoriteItemCommand : ICommand
{
    public ICommand Inner { get; }
    private readonly Func<FavoriteItem?> _current;

    public FavoriteItemCommand(ICommand inner, Func<FavoriteItem?> current)
    {
        Inner = inner;
        _current = current;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => Inner.CanExecuteChanged += value;
        remove => Inner.CanExecuteChanged -= value;
    }

    public FavoriteItem? Resolve(object? parameter)
    {
        if (parameter is FavoriteItem item) return item;
        var current = _current();
        if (current == null || parameter == null) return null;
        return ReferenceEquals(current.Track, parameter) || ReferenceEquals(current.Album, parameter) ? current : null;
    }

    public bool CanExecute(object? parameter) => Resolve(parameter) is { } item && Inner.CanExecute(item);

    public void Execute(object? parameter)
    {
        if (Resolve(parameter) is { } item)
            Inner.Execute(item);
    }
}
