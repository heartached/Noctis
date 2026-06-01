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
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryAlbumsView : UserControl
{
    private LibraryAlbumsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    private readonly HashSet<Button> _selectedTiles = new();

    public LibraryAlbumsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
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

        MultiSelectHelper.HandleAlbumTileClick(tile, e, _selectedTiles);

        // Ensure this view has focus so Ctrl+A reaches OnViewKeyDown
        if (_selectedTiles.Count > 0)
            Focus();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        MultiSelectHelper.HandleAlbumSelectAll(e, CollectAlbumTiles(), _selectedTiles);
    }

    private List<Button> CollectAlbumTiles()
    {
        var allTiles = new List<Button>();
        foreach (var desc in AlbumListBox.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        return allTiles;
    }

    /// <summary>Returns the list of ctrl-selected albums, or a single-item list with the given album if none are selected.</summary>
    private List<Album> GetSelectedAlbumsOr(Album? fallback)
    {
        var selected = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);
        if (selected.Count > 0) return selected;
        if (fallback != null) return new List<Album> { fallback };
        return new List<Album>();
    }

    private void OnAlbumContextMenuOpening(object? sender, CancelEventArgs e)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(sender as ContextMenu);

        if (DataContext is not LibraryAlbumsViewModel vm) return;

        // Push ctrl-selected albums to ViewModel so commands can operate on all of them
        vm.CtrlSelectedAlbums = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.FilteredAlbumRows.CollectionChanged -= OnFilteredRowsChanged;

        _vm = DataContext as LibraryAlbumsViewModel;
        if (_vm != null)
            _vm.FilteredAlbumRows.CollectionChanged += OnFilteredRowsChanged;
    }

    private void OnFilteredRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll to top when rows change due to any active filter (search text or artist)
        // BUT skip if a scroll restore is pending (returning from album detail)
        if (_vm?.HasActiveFilter != true && _vm?.IsArtistFiltered != true)
            return;
        if (_pendingScrollRestore != null || (_vm != null && _vm.SavedScrollOffset > 0))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                sv.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (e.NewSize.Width <= 0 || DataContext is not LibraryAlbumsViewModel vm)
            return;

        // DockPanel has Margin="12,8,12,0" → 24px horizontal margin
        // Each tile has Margin="2" (4px horiz) + Button Padding="2" (4px horiz)
        var usable = e.NewSize.Width - 24;
        var tileContentWidth = usable / 5.0 - 8;
        var newSize = Math.Max(80, tileContentWidth);

        if (Math.Abs(newSize - vm.TileArtworkSize) < 0.5)
            return;

        // Save and restore scroll position so sidebar hover doesn't reset scroll
        var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
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

        if (DataContext is LibraryAlbumsViewModel vm)
        {
            var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
            _vm.FilteredAlbumRows.CollectionChanged -= OnFilteredRowsChanged;

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        MultiSelectHelper.ClearAlbumSelections(_selectedTiles);
        if (DataContext is LibraryAlbumsViewModel selVm) selVm.CtrlSelectedAlbums = new List<Album>();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            AlbumListBox.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            AlbumListBox.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is LibraryAlbumsViewModel vm && vm.SavedScrollOffset > 0)
        {
            var targetOffset = vm.SavedScrollOffset;

            // Restore scroll position after layout without hiding the view.
            // Using a single-shot LayoutUpdated avoids the Opacity=0 blank flash.
            _pendingScrollRestore = (s, args) =>
            {
                var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                CancelPendingScrollRestore();
            };

            AlbumListBox.LayoutUpdated += _pendingScrollRestore;
        }
        else if (DataContext is LibraryAlbumsViewModel activeVm
                 && (activeVm.IsArtistFiltered || activeVm.HasActiveFilter))
        {
            _pendingScrollRestore = (s, args) =>
            {
                var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                sv.Offset = new Vector(0, 0);
                CancelPendingScrollRestore();
            };

            AlbumListBox.LayoutUpdated += _pendingScrollRestore;
        }
    }

}
