using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class HomeView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    // Cache populators per ContextMenu instance (each DataTemplate item gets its own).
    private readonly Dictionary<ContextMenu, PlaylistMenuPopulator> _populators = new();

    public HomeView()
    {
        InitializeComponent();
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm) return;
        if (sender is not ContextMenu contextMenu) return;

        if (!_populators.TryGetValue(contextMenu, out var populator))
        {
            // Find the "Add to Playlist" parent and its separator within this context menu.
            MenuItem? parentItem = null;
            Separator? separator = null;
            string? menuName = null;

            foreach (var item in contextMenu.Items)
            {
                if (item is MenuItem mi && (mi.Name == "AddToPlaylistMenuItem1" || mi.Name == "AddToPlaylistMenuItem2"))
                {
                    parentItem = mi;
                    menuName = mi.Name;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep && (sep.Name == "PlaylistSeparator1" || sep.Name == "PlaylistSeparator2"))
                        {
                            separator = sep;
                            break;
                        }
                    }
                    break;
                }
            }

            if (parentItem == null || separator == null)
                return;

            populator = new PlaylistMenuPopulator(parentItem, separator);
            _populators[contextMenu] = populator;
        }

        // Determine which command to use based on which menu this is.
        // Track menu has AddToPlaylistMenuItem1, album menu has AddToPlaylistMenuItem2.
        foreach (var item in contextMenu.Items)
        {
            if (item is MenuItem mi)
            {
                if (mi.Name == "AddToPlaylistMenuItem1")
                {
                    populator.Populate(vm.Playlists, vm.AddTrackToExistingPlaylistCommand);
                    return;
                }
                if (mi.Name == "AddToPlaylistMenuItem2")
                {
                    populator.Populate(vm.Playlists, vm.AddAlbumToExistingPlaylistCommand);
                    return;
                }
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is HomeViewModel vm)
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

        if (DataContext is HomeViewModel vm && vm.SavedScrollOffset > 0)
        {
            Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = this.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                if (sv.Extent.Height < targetOffset && attempts < 50)
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
