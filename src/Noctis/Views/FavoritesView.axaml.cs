using System.Collections.Generic;
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
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();
    private readonly HashSet<Button> _selectedTiles = new();

    public FavoritesView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && !(source is Button b && b.Classes.Contains("album-tile")))
            source = source.Parent as Control;
        if (source is not Button tile) return;

        MultiSelectHelper.HandleAlbumTileClick(tile, e, _selectedTiles);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var allTiles = new List<Button>();
        foreach (var desc in this.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        MultiSelectHelper.HandleAlbumSelectAll(e, allTiles, _selectedTiles);
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FavoritesViewModel vm) return;
        if (sender is not ContextMenu ctx) return;

        // Push ctrl-selected items to ViewModel so commands can operate on all of them
        vm.CtrlSelectedItems = MultiSelectHelper.GetSelectedData<FavoriteItem>(_selectedTiles);

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

        var favoriteItem = ctx.DataContext as FavoriteItem;
        populator.Populate(vm.Playlists, vm.AddItemToExistingPlaylistCommand,
            playlist => new object[] { favoriteItem!, playlist });
    }

    /// <summary>Left-click handler: play track or open album depending on item type.</summary>
    private void OnFavoriteCardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FavoriteItem item) return;
        if (DataContext is not FavoritesViewModel vm) return;

        // If there are ctrl-selected tiles, a normal click already cleared them in the tunnel handler
        if (_selectedTiles.Count > 0) return;

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
