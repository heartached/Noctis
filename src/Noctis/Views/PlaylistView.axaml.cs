using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class PlaylistView : UserControl
{
    private EventHandler? _pendingScrollRestore;
    private readonly HashSet<ListBoxItem> _selectedTrackItems = new();
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;

    // ── Drag-reorder state ──
    private Point _dragStartPoint;
    private bool _dragStarted;
    private Track? _dragTrack;
    private ListBoxItem? _dragSourceItem;

    public PlaylistView()
    {
        InitializeComponent();

        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Reset shared menu so it picks up new VM commands
        _menuBuilder?.Reset();
        _menuBuilder = null;
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source is not ListBoxItem)
            source = source.Parent as Control;
        if (source is not ListBoxItem item) return;

        MultiSelectHelper.HandleTrackRowClick(item, e, _selectedTrackItems);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm && vm.IsDescriptionOpen)
            return;
        MultiSelectHelper.HandleTrackSelectAll(e, TrackList, _selectedTrackItems);
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        if (_menuBuilder != null) return _menuBuilder.Menu;

        if (DataContext is not PlaylistViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        return _menuBuilder.Build("Remove from Playlist",
            "avares://Noctis/Assets/Icons/Remove%20from%20Playlist%20ICON.png", this);
    }

    private void BindContextMenuToTrack(Track track)
    {
        GetOrCreateContextMenu();
        var vm = DataContext as PlaylistViewModel;
        if (vm == null || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayFromCommand,
            shuffleCommand: vm.ShuffleAllCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToNewPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveTrackCommand,
            playlists: vm.Playlists,
            addToExistingPlaylistCommand: vm.AddToExistingPlaylistCommand);
    }

    private void DetachMenuFromOwner()
    {
        if (_menuOwnerItem != null)
        {
            _menuOwnerItem.ContextMenu = null;
            _menuOwnerItem = null;
        }
        // Also detach from any button that previously owned the menu
        if (_menuBuilder?.Menu?.Parent is Control parent)
        {
            parent.ContextMenu = null;
        }
    }

    private void OnOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Track track) return;
        if (DataContext is PlaylistViewModel vm)
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();

        if (menu.IsOpen) { menu.Close(); return; }

        // Detach from previous owner and attach to the button so Open() doesn't
        // throw "Cannot show ContextMenu on a different control".
        DetachMenuFromOwner();
        btn.ContextMenu = menu;
        _menuOwnerItem = null; // btn is not a ListBoxItem — track via ContextMenu directly

        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.Open(btn);
        e.Handled = true;
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested += OnTrackItemContextRequested;
            item.AddHandler(PointerPressedEvent, OnTrackRowPointerPressed, RoutingStrategies.Tunnel);
            item.AddHandler(PointerMovedEvent, OnTrackRowPointerMoved, RoutingStrategies.Tunnel);
        }
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextMenu = null;
            item.RemoveHandler(PointerPressedEvent, OnTrackRowPointerPressed);
            item.RemoveHandler(PointerMovedEvent, OnTrackRowPointerMoved);
        }
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is PlaylistViewModel vm)
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();
        if (menu.IsOpen)
            menu.Close();

        DetachMenuFromOwner();
        _menuOwnerItem = item;
        item.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(item);
        e.Handled = true;
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-taps on the 3-dot options button — it should only open the menu
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not PlaylistViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromCommand.Execute(track);
        }
    }

    // ── Drag-reorder handlers ──────────────────────────────────

    private void OnTrackRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
        if (DataContext is PlaylistViewModel vm && vm.IsSmartPlaylist) return;

        _dragStartPoint = e.GetPosition(item);
        _dragStarted = false;
        _dragTrack = item.DataContext as Track;
    }

    private async void OnTrackRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (_dragTrack == null || _dragStarted) return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(item);
        if (Math.Abs(pos.Y - _dragStartPoint.Y) < 4 && Math.Abs(pos.X - _dragStartPoint.X) < 4)
            return;

        _dragStarted = true;

        // Apply drag visual
        var vm = DataContext as PlaylistViewModel;
        if (vm != null)
        {
            var idx = vm.Tracks.IndexOf(_dragTrack);
            if (idx >= 0)
            {
                _dragSourceItem = TrackList.ContainerFromIndex(idx) as ListBoxItem;
                if (_dragSourceItem != null)
                    _dragSourceItem.Opacity = 0.35;
            }
        }

        var data = new DataObject();
        data.Set("NoctisPlaylistTrack", _dragTrack);
        data.Set(DragFileBehavior.InternalDragFormat, true);

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch
        {
            // Drag cancelled — non-critical
        }
        finally
        {
            ResetPlaylistDragVisuals();
            _dragTrack = null;
            _dragStarted = false;
        }
    }

    private void ResetPlaylistDragVisuals()
    {
        if (_dragSourceItem != null)
        {
            _dragSourceItem.Opacity = 1.0;
            _dragSourceItem = null;
        }

        var indicator = this.FindControl<Border>("PlaylistDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("NoctisPlaylistTrack"))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        var wrapper = this.FindControl<Grid>("TrackListWrapper");
        var indicator = this.FindControl<Border>("PlaylistDropIndicator");
        if (wrapper == null || indicator == null) return;

        var pointerInWrapper = e.GetPosition(wrapper);
        double? indicatorY = null;

        for (int i = 0; i < TrackList.ItemCount; i++)
        {
            var container = TrackList.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), wrapper);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + container.Bounds.Height;
            var mid = (top + bottom) / 2;

            if (pointerInWrapper.Y >= top && pointerInWrapper.Y < mid)
            {
                indicatorY = top;
                break;
            }
            if (pointerInWrapper.Y >= mid && pointerInWrapper.Y < bottom)
            {
                indicatorY = bottom;
                break;
            }
        }

        if (indicatorY != null)
        {
            if (indicator.RenderTransform is TranslateTransform transform)
                transform.Y = indicatorY.Value;
            indicator.IsVisible = true;
        }
        else
        {
            indicator.IsVisible = false;
        }
    }

    private void OnPlaylistDragLeave(object? sender, DragEventArgs e)
    {
        var indicator = this.FindControl<Border>("PlaylistDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;
    }

    private async void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PlaylistViewModel vm) return;
        if (e.Data.Get("NoctisPlaylistTrack") is not Track draggedTrack) return;

        var fromIndex = vm.Tracks.IndexOf(draggedTrack);
        if (fromIndex < 0) return;

        var toIndex = GetPlaylistDropTargetIndex(e);
        if (toIndex < 0) toIndex = vm.Tracks.Count - 1;
        if (toIndex >= vm.Tracks.Count) toIndex = vm.Tracks.Count - 1;

        if (fromIndex != toIndex)
            await vm.MoveTrack(fromIndex, toIndex);

        ResetPlaylistDragVisuals();
        e.Handled = true;
    }

    private int GetPlaylistDropTargetIndex(DragEventArgs e)
    {
        var pos = e.GetPosition(TrackList);

        for (int i = 0; i < TrackList.ItemCount; i++)
        {
            var container = TrackList.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), TrackList);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + container.Bounds.Height;
            var midpoint = top + container.Bounds.Height / 2;

            if (pos.Y < midpoint && pos.Y >= top)
                return i;
            if (pos.Y >= midpoint && pos.Y < bottom)
                return i;
        }

        return TrackList.ItemCount - 1;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        ResetPlaylistDragVisuals();

        TrackList.RemoveHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        TrackList.RemoveHandler(DragDrop.DropEvent, OnPlaylistDrop);
        TrackList.RemoveHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave);

        if (DataContext is PlaylistViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            TrackList.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Wire drag-drop handlers for reorder (handledEventsToo to bypass window-level handlers)
        TrackList.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        TrackList.AddHandler(DragDrop.DropEvent, OnPlaylistDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        TrackList.AddHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);

        if (DataContext is PlaylistViewModel vm && vm.SavedScrollOffset > 0)
        {
            var targetOffset = vm.SavedScrollOffset;

            _pendingScrollRestore = (s, args) =>
            {
                var sv = TrackList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                CancelPendingScrollRestore();
            };

            TrackList.LayoutUpdated += _pendingScrollRestore;
        }
    }

}
