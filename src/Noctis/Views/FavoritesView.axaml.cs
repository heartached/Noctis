using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
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

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(sender as ContextMenu);

        if (DataContext is not FavoritesViewModel vm) return;

        // Push ctrl-selected items to ViewModel so commands can operate on all of them
        vm.CtrlSelectedItems = _selectedItems.ToList();
    }

    /// <summary>Left-click handler: play track or open album depending on item type.</summary>
    private void OnFavoriteCardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FavoriteItem item) return;
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

        // DockPanel has Margin="12,8,12,8" → 24px horizontal margin
        // Each tile has Margin="2" (4px horiz) + Button Padding="2" (4px horiz)
        var usable = e.NewSize.Width - 24;
        var tileContentWidth = usable / 5.0 - 8;
        var newSize = Math.Max(80, tileContentWidth);

        if (Math.Abs(newSize - vm.TileArtworkSize) < 0.5)
            return;

        // Save and restore scroll position so sidebar hover doesn't reset scroll
        var sv = FavoritesList.FindDescendantOfType<ScrollViewer>();
        var savedY = sv?.Offset.Y ?? 0;

        vm.TileArtworkSize = newSize;

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
}
