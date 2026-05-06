using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class HomeView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    // Cache populators per ContextMenu instance (each DataTemplate item gets its own).
    private readonly Dictionary<ContextMenu, PlaylistMenuPopulator> _populators = new();
    private readonly HashSet<Button> _selectedTiles = new();

    public HomeView()
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

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not HomeViewModel vm) return;
        if (sender is not ContextMenu contextMenu) return;

        // Push ctrl-selected albums to ViewModel so commands can operate on all of them
        vm.CtrlSelectedAlbums = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);

        if (!_populators.TryGetValue(contextMenu, out var populator))
        {
            MenuItem? parentItem = null;
            Separator? separator = null;

            foreach (var item in contextMenu.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    parentItem = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }

            if (parentItem == null || separator == null)
                return;

            populator = new PlaylistMenuPopulator(parentItem, separator);
            _populators[contextMenu] = populator;
        }

        // Determine which command to use based on DataContext type.
        if (contextMenu.DataContext is Track track)
            populator.Populate(vm.Playlists, vm.AddTrackToExistingPlaylistCommand,
                playlist => new object[] { track, playlist });
        else if (contextMenu.DataContext is Album album)
            populator.Populate(vm.Playlists, vm.AddAlbumToExistingPlaylistCommand,
                playlist => new object[] { album, playlist });
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
