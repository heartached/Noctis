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
    // See LibrarySongsView for why this is data-tracked rather than container-tracked.
    private readonly HashSet<Track> _selectedTracks = new();
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;

    // ── Drag-reorder state (pointer-tracked, mirrors Queue's pill preview) ──
    private const double PlaylistDragThreshold = 6.0;
    private Point _dragStartPos;
    private bool _dragActive;
    private Track? _dragTrack;
    private int _dragSourceIndex = -1;
    private double _dragRowOffsetY;
    private ListBoxItem? _dragHiddenItem;

    public PlaylistView()
    {
        InitializeComponent();

        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a row.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);

        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

        // Wire any containers that were realized before this subscription.
        // Without this, right-click on row padding/edges (outside the inner Grid's
        // own ContextMenu) silently does nothing because ContextRequested has no
        // listener at the ListBoxItem level.
        foreach (var container in TrackList.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
                WireTrackItem(item);
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void WireTrackItem(ListBoxItem item)
    {
        item.ContextRequested -= OnTrackItemContextRequested;
        item.ContextRequested += OnTrackItemContextRequested;
        item.RemoveHandler(PointerPressedEvent, OnTrackRowPointerPressed);
        item.AddHandler(PointerPressedEvent, OnTrackRowPointerPressed, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerMovedEvent, OnTrackRowPointerMoved);
        item.AddHandler(PointerMovedEvent, OnTrackRowPointerMoved, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerReleasedEvent, OnTrackRowPointerReleased);
        item.AddHandler(PointerReleasedEvent, OnTrackRowPointerReleased, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost);
        item.AddHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost, RoutingStrategies.Tunnel);
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
        if (item.DataContext is not Track track) return;

        MultiSelectHelper.HandleTrackRowClickByData(item, track, e, _selectedTracks);

        if (_selectedTracks.Count > 0)
            Focus();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm && vm.IsDescriptionOpen)
            return;
        MultiSelectHelper.HandleTrackSelectAllByData(e, TrackList, _selectedTracks);
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
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveTrackCommand,
            convertCommand: vm.ConvertTracksCommand,
            scanReplayGainCommand: vm.ScanReplayGainCommand,
            startRadioCommand: vm.StartRadioCommand,
            snoozeCommand: vm.SnoozeForMonthCommand);
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
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

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
            WireTrackItem(item);
            MultiSelectHelper.SyncContainerVisual(item, _selectedTracks);
        }
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextMenu = null;
            item.Classes.Remove("ctrl-selected");
            item.RemoveHandler(PointerPressedEvent, OnTrackRowPointerPressed);
            item.RemoveHandler(PointerMovedEvent, OnTrackRowPointerMoved);
            item.RemoveHandler(PointerReleasedEvent, OnTrackRowPointerReleased);
            item.RemoveHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost);
        }
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is PlaylistViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

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

    // ── Drag-reorder handlers (pointer-tracked, mirrors Queue's pill preview) ──
    //
    // The dragged row is rendered as a floating preview (#PlaylistDragPreview) that
    // follows the pointer Y. The original row's Opacity is set to 0 while the drag
    // is active so its slot stays reserved. On release we compute the target index
    // and call vm.MoveTrack. No DragDrop.DoDragDrop is used.

    private void OnTrackRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
        if (DataContext is not PlaylistViewModel vm) return;
        if (vm.IsSmartPlaylist) return;
        // No reordering while a search filter is active: indexes refer to the
        // filtered view, and persisting from it would drop the hidden tracks.
        if (!string.IsNullOrWhiteSpace(vm.SearchText)) return;
        // No reordering while a non-Manual sort is active (displayed order != saved order).
        if (vm.SortMode != PlaylistSortMode.Manual) return;
        if (item.DataContext is not Track track) return;

        _dragTrack = track;
        _dragSourceIndex = vm.Tracks.IndexOf(track);
        _dragRowOffsetY = e.GetPosition(item).Y;
        _dragStartPos = e.GetPosition(this);
        _dragActive = false;
    }

    private void OnTrackRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTrack == null) return;
        if (sender is not ListBoxItem item) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragStartPos.X) < PlaylistDragThreshold &&
                Math.Abs(pos.Y - _dragStartPos.Y) < PlaylistDragThreshold)
                return;

            StartPlaylistDrag(item, e);
        }

        UpdatePlaylistDragPreviewPosition(e);
        UpdatePlaylistDropIndicator(e);
    }

    private async void OnTrackRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragActive)
        {
            await CommitPlaylistDropAsync(e);
            ResetPlaylistDragState();
            e.Pointer.Capture(null);
        }
        else
        {
            ResetPlaylistDragState();
        }
    }

    private void OnTrackRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Treat lost capture as a cancel — restore visuals without performing the move.
        ResetPlaylistDragState();
    }

    private void StartPlaylistDrag(ListBoxItem rowItem, PointerEventArgs e)
    {
        _dragActive = true;
        e.Pointer.Capture(rowItem);

        var preview = this.FindControl<Border>("PlaylistDragPreview");
        if (preview != null && _dragTrack != null)
        {
            preview.DataContext = _dragTrack;
            preview.IsVisible = true;
        }

        if (_dragSourceIndex >= 0)
        {
            _dragHiddenItem = TrackList.ContainerFromIndex(_dragSourceIndex) as ListBoxItem;
            if (_dragHiddenItem != null)
                _dragHiddenItem.Opacity = 0;
        }
    }

    private void UpdatePlaylistDragPreviewPosition(PointerEventArgs e)
    {
        var wrapper = this.FindControl<Grid>("TrackListWrapper");
        var preview = this.FindControl<Border>("PlaylistDragPreview");
        if (wrapper == null || preview == null) return;
        if (preview.RenderTransform is not TranslateTransform tt) return;

        var pointerInWrapper = e.GetPosition(wrapper).Y;
        tt.Y = pointerInWrapper - _dragRowOffsetY;
    }

    private void UpdatePlaylistDropIndicator(PointerEventArgs e)
    {
        var indicator = this.FindControl<Border>("PlaylistDropIndicator");
        var wrapper = this.FindControl<Grid>("TrackListWrapper");
        if (indicator == null || wrapper == null) return;

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

    private async System.Threading.Tasks.Task CommitPlaylistDropAsync(PointerEventArgs e)
    {
        if (DataContext is not PlaylistViewModel vm) return;
        if (_dragSourceIndex < 0) return;

        var posInList = e.GetPosition(TrackList);
        var toIndex = GetPlaylistDropTargetIndex(posInList);
        if (toIndex < 0) return; // no realized rows to target — treat as cancel
        if (toIndex >= vm.Tracks.Count) toIndex = vm.Tracks.Count - 1;

        if (_dragSourceIndex != toIndex)
            await vm.MoveTrack(_dragSourceIndex, toIndex);
    }

    private int GetPlaylistDropTargetIndex(Point posInList)
    {
        // Virtualized list: only realized containers are inspectable. If the
        // pointer isn't over one (e.g. the padding gap below the last row),
        // fall back to the vertically nearest realized row instead of the list
        // end, so the committed move matches what the drop indicator implied.
        int nearestIndex = -1;
        double nearestDistance = double.MaxValue;

        for (int i = 0; i < TrackList.ItemCount; i++)
        {
            var container = TrackList.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), TrackList);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + container.Bounds.Height;

            if (posInList.Y >= top && posInList.Y < bottom)
                return i;

            var distance = posInList.Y < top ? top - posInList.Y : posInList.Y - bottom;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private void ResetPlaylistDragState()
    {
        var preview = this.FindControl<Border>("PlaylistDragPreview");
        if (preview != null)
        {
            preview.IsVisible = false;
            preview.DataContext = null;
        }

        if (_dragHiddenItem != null)
        {
            _dragHiddenItem.Opacity = 1.0;
            _dragHiddenItem = null;
        }

        var indicator = this.FindControl<Border>("PlaylistDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;

        _dragActive = false;
        _dragTrack = null;
        _dragSourceIndex = -1;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        ResetPlaylistDragState();

        if (DataContext is PlaylistViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        _selectedTracks.Clear();
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove("ctrl-selected");
        if (DataContext is PlaylistViewModel selVm) selVm.CtrlSelectedTracks = new List<Track>();

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
