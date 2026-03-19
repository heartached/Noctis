using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryAlbumsView : UserControl
{
    private LibraryAlbumsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();

    public LibraryAlbumsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnAlbumContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryAlbumsViewModel vm) return;
        if (sender is not ContextMenu ctx) return;

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
        populator.Populate(vm.Playlists, vm.AddToExistingPlaylistCommand,
            playlist => new object[] { album!, playlist });
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
        // Each tile has Margin="2" (4px horiz) + Button Padding="4" (8px horiz)
        var usable = e.NewSize.Width - 24;
        var tileContentWidth = usable / 6.0 - 12;
        vm.TileArtworkSize = Math.Max(80, tileContentWidth);
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
            // Hide ListBox until scroll is restored to prevent flash-at-top flicker
            AlbumListBox.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = AlbumListBox.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                // Wait until the ScrollViewer extent is tall enough, with safety limit
                if (sv.Extent.Height < targetOffset && attempts < 50)
                    return;

                // Clamp to actual extent if content shrank since last visit
                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                AlbumListBox.Opacity = 1;
                CancelPendingScrollRestore();
            };

            AlbumListBox.LayoutUpdated += _pendingScrollRestore;
        }
    }

}
