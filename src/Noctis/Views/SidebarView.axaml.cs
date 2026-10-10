using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SidebarView : UserControl
{
    private bool _isSyncingSelection;
    private SidebarViewModel? _vm;
    private TopBarViewModel? _topBarVm;

    public SidebarView()
    {
        InitializeComponent();
        // A click on the already-selected row never raises SelectionChanged, so
        // navigating back out of a detail page whose origin section is still
        // highlighted (Home → album → click Home) was a dead click. Tunnel the
        // press so we see it before the ListBoxItem commits the (same) selection.
        foreach (var list in GetNavLists())
            list.AddHandler(PointerPressedEvent, OnNavListPointerPressed, RoutingStrategies.Tunnel);
        // Drop target: tracks dragged from any list land in a playlist (payload from
        // DragFileBehavior).
        DragDrop.SetAllowDrop(PlaylistList, true);
        PlaylistList.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        PlaylistList.AddHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave);
        PlaylistList.AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
        // Playlist rows reorder / move into folders with an in-app pointer drag.
        PlaylistList.AddHandler(PointerPressedEvent, OnPlaylistRowPointerPressed, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerMovedEvent, OnPlaylistRowPointerMoved, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerReleasedEvent, OnPlaylistRowPointerReleased, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerCaptureLostEvent, OnPlaylistRowPointerCaptureLost);
        PlaylistList.ContainerPrepared += OnPlaylistContainerPrepared;
        PlaylistList.LayoutUpdated += UpdateGroupTrays;
        DataContextChanged += OnDataContextChanged;
        SearchPopupContent.AddHandler(KeyDownEvent, OnSearchCapsuleKeyDown, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) =>
        {
            UnsubscribeFromViewModel();
            DetachOutsideClickWatcher();
        };
        AttachedToVisualTree += (_, _) =>
        {
            AttachOutsideClickWatcher();
            // After first layout, so the very first open of the run is already pinned
            // instead of spending a frame at the Popup's default 0,0 offsets.
            Dispatcher.UIThread.Post(EnsureSearchPopupPinned, DispatcherPriority.Loaded);
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeFromViewModel();
        _vm = DataContext as SidebarViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.FolderRowsInserted += OnFolderRowsInserted;
            _vm.FoldFolderRows = FoldFolderRowsAsync;
        }
        AttachTopBar(_vm?.TopBar);

        SyncSelectionFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.SelectedNavItem))
            SyncSelectionFromViewModel();
        else if (e.PropertyName == nameof(SidebarViewModel.TopBar))
            AttachTopBar(_vm?.TopBar);
    }

    // TopBar is assigned to the sidebar VM after composition, so (re)subscribe
    // whenever it changes rather than only at DataContext time.
    private void AttachTopBar(TopBarViewModel? topBar)
    {
        if (ReferenceEquals(_topBarVm, topBar)) return;
        if (_topBarVm != null)
        {
            _topBarVm.SearchOpenRequested -= OnSearchOpenRequested;
            _topBarVm.SearchCloseRequested -= OnSearchCloseRequested;
            _topBarVm.PropertyChanged -= OnTopBarPropertyChanged;
        }
        _topBarVm = topBar;
        if (_topBarVm != null)
        {
            _topBarVm.SearchOpenRequested += OnSearchOpenRequested;
            _topBarVm.SearchCloseRequested += OnSearchCloseRequested;
            _topBarVm.PropertyChanged += OnTopBarPropertyChanged;
            if (_topBarVm.IsSearchOpen) SyncSearchPopupToViewModel();
        }
    }

    private void OnNavListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Right-click on a playlist row / folder header opens its context menu without
        // the ListBox also selecting (= navigating to) the row underneath the menu.
        if (sender is ListBox rightList && ReferenceEquals(rightList, PlaylistList)
            && e.GetCurrentPoint(rightList).Properties.IsRightButtonPressed)
        {
            var target = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
            if (target?.DataContext is PlaylistNavItem && target.ContextMenu is { } menu)
            {
                e.Handled = true;
                menu.DataContext = target.DataContext;
                menu.Open(target);
            }
            return;
        }

        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        // Only plain section rows re-navigate. Playlist rows build a fresh view per
        // navigation (and folder headers only toggle), so re-firing those would stack
        // duplicate history entries instead of escaping a detail page.
        if (_vm?.SelectedNavItem is not { } current || current is PlaylistNavItem)
            return;
        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is NavItem pressed && ReferenceEquals(pressed, current))
            _vm.RequestNavigation(pressed);
    }

    // ── Playlist list as a drop target for TRACKS dragged from any list ──

    private ListBoxItem? _dropHighlighted;

    private (ListBoxItem? Container, PlaylistNavItem? Item) HitPlaylistRow(Point pos)
    {
        foreach (var container in PlaylistList.GetRealizedContainers())
        {
            if (container is not ListBoxItem row) continue;
            var origin = row.TranslatePoint(new Point(0, 0), PlaylistList);
            if (origin == null) continue;
            // Inflated by the rows' 2px vertical margin so the gap between two rows still
            // hits one of them — otherwise the highlight blinked off between rows.
            var rect = new Rect(origin.Value, row.Bounds.Size).Inflate(new Thickness(0, 2));
            if (rect.Contains(pos))
                return (row, row.DataContext as PlaylistNavItem);
        }
        return (null, null);
    }

    private void SetDropHighlight(ListBoxItem? row)
    {
        if (ReferenceEquals(_dropHighlighted, row)) return;
        _dropHighlighted?.Classes.Set("drop-target", false);
        _dropHighlighted = row;
        _dropHighlighted?.Classes.Set("drop-target", true);
    }

    private static bool CanAcceptTracks(PlaylistNavItem? item)
        => item is { IsFolder: false, IsSmartPlaylist: false, PlaylistId: not null };

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        var tracks = Helpers.DragFileBehavior.GetDraggedTracks(e.DataTransfer);
        if (tracks is not { Count: > 0 }) return; // external file drag — the window handles it

        var (container, item) = HitPlaylistRow(e.GetPosition(PlaylistList));
        var ok = CanAcceptTracks(item);
        SetDropHighlight(ok ? container : null);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPlaylistDragLeave(object? sender, DragEventArgs e)
    {
        // Avalonia raises DragLeave every time the pointer crosses from one element INSIDE
        // a row to another (name → art → count) and it bubbles up here. Clearing on those
        // made the highlighted pill blink off and fade back in on every small move (the
        // "pill changes shade" report, 09-22). Only a leave out of the whole list clears it.
        var pos = e.GetPosition(PlaylistList);
        if (new Rect(PlaylistList.Bounds.Size).Contains(pos)) return;
        SetDropHighlight(null);
    }

    private async void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            SetDropHighlight(null);
            var tracks = Helpers.DragFileBehavior.GetDraggedTracks(e.DataTransfer);
            if (tracks is not { Count: > 0 }) return;

            var (_, item) = HitPlaylistRow(e.GetPosition(PlaylistList));
            if (!CanAcceptTracks(item) || _vm == null || item?.PlaylistId is not { } targetId) return;
            e.Handled = true;
            await _vm.AddTracksToPlaylist(targetId, tracks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Drop failed: {ex.Message}");
        }
    }

    // ── Files dragged in from outside the app (GitHub #108) ──
    // The window routes those drops (the drop-import state lives there), so it asks here
    // which playlist row is under the pointer; the row lights like a track drop target.

    /// <summary>The manual playlist whose row is under the pointer, lit; null (and nothing
    /// lit) elsewhere. <paramref name="positionIn"/> maps the pointer into a visual's
    /// coordinates (DragEventArgs.GetPosition).</summary>
    internal Guid? TrackExternalFileDrop(Func<Visual, Point> positionIn)
    {
        var pos = positionIn(PlaylistList);
        // Rows scrolled out of the list keep realized containers above / below it, so a
        // pointer over the nav items must not reach them: only the list's own area counts.
        if (!PlaylistList.IsEffectivelyVisible || !new Rect(PlaylistList.Bounds.Size).Contains(pos))
        {
            SetDropHighlight(null);
            return null;
        }
        var (container, item) = HitPlaylistRow(pos);
        var ok = CanAcceptTracks(item);
        SetDropHighlight(ok ? container : null);
        return ok ? item!.PlaylistId : null;
    }

    /// <summary>Clears the row lit by <see cref="TrackExternalFileDrop"/>.</summary>
    internal void EndExternalFileDrop() => SetDropHighlight(null);

    // ── Playlist reorder (pointer-tracked, same liquid motion as the queue) ──
    //
    // A playlist row lifts into a card (#PlaylistDragCard, drawn with the list's own row
    // template) that springs after the pointer while the other rows slide apart to open a
    // gap where it will land (LiquidReorder). Over a folder header the gap closes and the
    // folder lights up instead: dropping there moves the playlist into it. Near the top edge
    // of a header (or the bottom edge of a closed one, or of an open folder's last row) the
    // gap opens above / below the whole folder instead, and the playlist lands there
    // outside it (SidebarViewModel.MovePlaylistNextToFolderAsync). On release the card
    // glides to its spot and SidebarViewModel.MovePlaylistAsync commits.
    // A folder header drags the same way, its open playlists riding in the card as one
    // block that lands anywhere among the other folders and loose playlists
    // (SidebarViewModel.MoveFolderAsync); the pinned playlists keep their own section.

    private const double PlaylistDragThreshold = 6;
    private PlaylistNavItem? _dragItem;
    private Point _dragStart;
    private double _dragGrabY;
    private bool _dragActive;
    // The press travelled past the drag threshold: even a drag that was refused (a lone
    // folder) is then no click, so its release must not fold / unfold the header.
    private bool _dragMoved;
    private LiquidReorder? _liquid;

    private LiquidReorder Liquid => _liquid ??= new LiquidReorder(
        this, PlaylistList, PlaylistDragCard, () =>
        {
            PlaylistDragCardContent.Content = null;
            PlaylistDragCard.CornerRadius = new CornerRadius(999); // back to the pill after a folder block
        });

    private static ListBoxItem? RowOf(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    private void OnPlaylistRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PlaylistList).Properties.IsLeftButtonPressed) return;
        if (RowOf(e.Source) is not { DataContext: PlaylistNavItem item } row
            || item is { IsFolder: false, PlaylistId: null })
            return;
        // The ListBox selects on a mouse PRESS and selecting a folder header toggles it, so an
        // open folder would fold shut before it could be dragged. Headers skip the selection
        // and toggle on release instead, when the press did not become a drag.
        if (item.IsFolder) e.Handled = true;

        // A new press lands before the last drop's glide finished: commit it now.
        Liquid.FinishNow();

        _dragItem = item;
        _dragStart = e.GetPosition(PlaylistList);
        _dragGrabY = e.GetPosition(row).Y;
        _dragActive = false;
        _dragMoved = false;
    }

    private void OnPlaylistRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem == null || _vm == null || Liquid.IsSettling) return;
        if (!e.GetCurrentPoint(PlaylistList).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(PlaylistList);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragStart.X) < PlaylistDragThreshold &&
                Math.Abs(pos.Y - _dragStart.Y) < PlaylistDragThreshold)
                return;
            _dragMoved = true;
            if (!(_dragItem.IsFolder ? StartFolderDrag(e) : StartPlaylistDrag(e))) return;
        }

        var cardTop = e.GetPosition(PlaylistListHost).Y - _dragGrabY;
        if (_dragItem.IsFolder) cardTop = ClampToTopLevel(cardTop);
        Liquid.MoveCardTo(cardTop);
        if (_dragItem.IsFolder)
        {
            Liquid.TargetIndex = NearestTopLevelSlot(cardTop);
            e.Handled = true;
            return;
        }
        var probe = cardTop + Liquid.Pitch / 2;
        var target = LiquidReorder.NearestSlot(PlaylistList, PlaylistListHost, probe);
        if (target < 0) return;

        var rows = _vm.SidebarRows;
        var edge = target < rows.Count && target != Liquid.SourceIndex ? EdgeOf(target, probe) : 0;
        _folderTarget = null;
        _besideFolder = null;
        if (target < rows.Count && rows[target].IsFolder && edge < 0)
        {
            // Top edge of a header: above the whole folder, outside it.
            SetDropHighlight(null);
            Liquid.TargetIndex = BeforeRow(target);
            _besideFolder = (rows[target], false);
        }
        else if (target < rows.Count && rows[target].IsFolder && edge > 0 && !HasOpenRows(target))
        {
            // Bottom edge of a closed header: below the folder, outside it.
            SetDropHighlight(null);
            Liquid.TargetIndex = AfterRow(target);
            _besideFolder = (rows[target], true);
        }
        else if (target < rows.Count && rows[target].IsFolder)
        {
            // A folder header is a destination, not a slot: close the gap and light it up.
            SetDropHighlight(PlaylistList.ContainerFromIndex(target) as ListBoxItem);
            Liquid.TargetIndex = Liquid.SourceIndex;
            _folderTarget = rows[target];
        }
        else if (edge > 0 && LastOpenRowHeader(target) is { } header)
        {
            // Bottom edge of an open folder's last row: below the folder, outside it.
            SetDropHighlight(null);
            Liquid.TargetIndex = AfterRow(target);
            _besideFolder = (header, true);
        }
        else
        {
            SetDropHighlight(null);
            Liquid.TargetIndex = target;
        }
        e.Handled = true;
    }

    private PlaylistNavItem? _folderTarget;
    // Dropping lands the playlist outside this folder, right above / below it.
    private (PlaylistNavItem Header, bool After)? _besideFolder;

    /// <summary>-1 when <paramref name="probe"/> (the card's centre) is in the top quarter of
    /// row <paramref name="index"/>'s slot, +1 in the bottom quarter, else 0.</summary>
    private int EdgeOf(int index, double probe)
    {
        if (PlaylistList.ContainerFromIndex(index) is not Control row
            || LiquidReorder.SlotTop(PlaylistList, index, PlaylistListHost) is not { } top)
            return 0;
        var offset = probe - (top + row.Bounds.Height / 2);
        var edge = Liquid.Pitch / 4;
        return offset < -edge ? -1 : offset > edge ? 1 : 0;
    }

    /// <summary>Target that opens the gap right above row <paramref name="index"/>.</summary>
    private int BeforeRow(int index) => Liquid.SourceIndex < index ? index - 1 : index;

    /// <summary>Target that opens the gap right below row <paramref name="index"/>.</summary>
    private int AfterRow(int index) => Liquid.SourceIndex < index ? index : index + 1;

    /// <summary>The next row after <paramref name="index"/> other than the dragged one.</summary>
    private int NextRow(int index) => index + 1 == Liquid.SourceIndex ? index + 2 : index + 1;

    /// <summary>The folder header at <paramref name="header"/> shows playlists besides the dragged one.</summary>
    private bool HasOpenRows(int header)
    {
        var rows = _vm!.SidebarRows;
        var next = NextRow(header);
        return next < rows.Count && rows[next].IsInFolder;
    }

    /// <summary>The header of the open folder whose last shown row (besides the dragged one)
    /// is <paramref name="index"/>; null when that row is not one.</summary>
    private PlaylistNavItem? LastOpenRowHeader(int index)
    {
        var rows = _vm!.SidebarRows;
        if (index >= rows.Count || !rows[index].IsInFolder) return null;
        var next = NextRow(index);
        if (next < rows.Count && rows[next].IsInFolder) return null;
        var header = index;
        while (header > 0 && !rows[header].IsFolder) header--;
        return rows[header].IsFolder ? rows[header] : null;
    }

    private bool StartPlaylistDrag(PointerEventArgs e)
    {
        if (_vm == null || _dragItem == null) return false;
        var source = _vm.SidebarRows.IndexOf(_dragItem);
        if (source < 0 || PlaylistList.ContainerFromIndex(source) is not ListBoxItem row) return false;

        _dragActive = true;
        e.Pointer.Capture(PlaylistList);

        PlaylistDragCardContent.ContentTemplate = PlaylistList.ItemTemplate;
        PlaylistDragCardContent.Content = _dragItem;
        PlaylistDragCard.Height = row.Bounds.Height;
        Liquid.Pitch = row.Bounds.Height + row.Margin.Top + row.Margin.Bottom;
        Liquid.SourceIndex = Liquid.TargetIndex = source;
        // Start exactly over the grabbed row so the lift reads as the row rising.
        var rowTop = row.TranslatePoint(new Point(0, 0), PlaylistListHost)?.Y ?? 0;
        Liquid.Begin(rowTop);
        return true;
    }

    /// <summary>Lifts a folder header together with its open playlists: one card, one block.</summary>
    private bool StartFolderDrag(PointerEventArgs e)
    {
        if (_vm == null || _dragItem == null) return false;
        // A folder with no other folder or loose playlist has nowhere to go: the press stays
        // a click (folds / unfolds on release).
        var blocks = TopLevelBlocks();
        if (blocks.Count < 2) return false;
        var block = blocks.Find(b => ReferenceEquals(b.Header, _dragItem));
        if (block.Header == null || PlaylistList.ContainerFromIndex(block.Start) is not ListBoxItem row) return false;

        _dragActive = true;
        e.Pointer.Capture(PlaylistList);

        // Every row of the block in the list's own template, each where it sits in the list:
        // the card's padding stands in for the row's, the stack spacing for the rest of the pitch.
        var pitch = row.Bounds.Height + row.Margin.Top + row.Margin.Bottom;
        var rowContent = row.Bounds.Height - PlaylistDragCard.Padding.Top - PlaylistDragCard.Padding.Bottom;
        var rows = new StackPanel { Spacing = pitch - rowContent };
        foreach (var item in _vm.SidebarRows.Skip(block.Start).Take(block.Count))
            rows.Children.Add(new ContentControl { Content = item, ContentTemplate = PlaylistList.ItemTemplate, Height = rowContent });
        PlaylistDragCardContent.ContentTemplate = null;
        PlaylistDragCardContent.Content = rows;
        PlaylistDragCard.Height = row.Bounds.Height + (block.Count - 1) * pitch;
        // Round like a single row's ends; one long pill would cut into the corner rows.
        PlaylistDragCard.CornerRadius = new CornerRadius(row.Bounds.Height / 2);
        Liquid.Pitch = block.Count * pitch;
        Liquid.SourceIndex = Liquid.TargetIndex = block.Start;
        Liquid.SourceCount = block.Count;
        var rowTop = row.TranslatePoint(new Point(0, 0), PlaylistListHost)?.Y ?? 0;
        Liquid.Begin(rowTop);
        return true;
    }

    /// <summary>The top-level entries below the pinned playlists, each with the rows it
    /// carries: a folder header plus its open playlists, or one loose playlist.</summary>
    private List<(PlaylistNavItem Header, int Start, int Count)> TopLevelBlocks()
    {
        var blocks = new List<(PlaylistNavItem Header, int Start, int Count)>();
        if (_vm == null) return blocks;
        var rows = _vm.SidebarRows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].IsFolder)
            {
                var end = i + 1;
                while (end < rows.Count && rows[end].IsInFolder) end++;
                blocks.Add((rows[i], i, end - i));
            }
            else if (rows[i] is { IsPinned: false, IsInFolder: false, PlaylistId: not null })
            {
                blocks.Add((rows[i], i, 1));
            }
        }
        return blocks;
    }

    /// <summary>
    /// Row where the dragged folder block would start if dropped now: in front of an entry
    /// above it, after one below it (the rows it passes close up behind it), or its own spot,
    /// whichever is nearest the card. The entries are the other folders and the loose
    /// playlists, the same slots a playlist drag offers; the pinned playlists keep their section.
    /// </summary>
    private int NearestTopLevelSlot(double cardTop)
    {
        var source = Liquid.SourceIndex;
        var best = source;
        var bestDist = Math.Abs(cardTop - FolderSlotTop(source));
        foreach (var (_, start, count) in TopLevelBlocks())
        {
            if (start == source) continue;
            var slot = start < source ? start : start + count - Liquid.SourceCount;
            var dist = Math.Abs(cardTop - FolderSlotTop(slot));
            if (dist < bestDist) { bestDist = dist; best = slot; }
        }
        return best;
    }

    /// <summary>Keeps the folder card between the first top-level entry's top and the last
    /// one's bottom, so it never floats over the pinned playlists it cannot land among.</summary>
    private double ClampToTopLevel(double cardTop)
    {
        var blocks = TopLevelBlocks();
        if (blocks.Count == 0) return cardTop;
        var (_, lastStart, lastCount) = blocks[^1];
        return Math.Clamp(cardTop, FolderSlotTop(blocks[0].Start), FolderSlotTop(lastStart + lastCount - Liquid.SourceCount));
    }

    /// <summary>Card top for the dragged block starting at row <paramref name="slot"/>. The
    /// rows share one pitch, so it is whole rows away from the block's own (layout) slot.</summary>
    private double FolderSlotTop(int slot)
    {
        var sourceTop = LiquidReorder.SlotTop(PlaylistList, Liquid.SourceIndex, PlaylistListHost) ?? Liquid.CardY;
        return sourceTop + (slot - Liquid.SourceIndex) * (Liquid.Pitch / Liquid.SourceCount);
    }

    private void OnPlaylistRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragActive)
        {
            // A press on a folder header that never moved like a drag is a click: toggle it.
            if (!_dragMoved && _dragItem is { IsFolder: true } clicked && ReferenceEquals(RowOf(e.Source)?.DataContext, clicked))
                _ = _vm?.ToggleFolderAnimatedAsync(clicked);
            _dragItem = null;
            return;
        }
        e.Handled = true;

        var vm = _vm;
        var dragged = _dragItem;
        var folder = _folderTarget;
        var beside = _besideFolder;
        var source = Liquid.SourceIndex;
        var target = Liquid.TargetIndex;
        _dragActive = false;
        _dragItem = null;
        _folderTarget = null;
        _besideFolder = null;
        SetDropHighlight(null);
        // After _dragActive is cleared: releasing the capture raises CaptureLost, which
        // would otherwise read this drop as a cancel.
        e.Pointer.Capture(null);

        if (vm != null && dragged is { IsFolder: true } && source >= 0)
        {
            SettleFolderDrag(vm, dragged, source, target);
            return;
        }

        if (vm == null || dragged?.PlaylistId is not { } draggedId || source < 0)
        {
            Liquid.Cancel();
            return;
        }

        if (beside is { } besideFolder)
        {
            // Outside the folder even when the gap is its own spot (a playlist leaving the
            // folder it closes, a pinned one joining the loose ones above the first folder).
            var besideLanding = LiquidReorder.SlotTop(PlaylistList, target, PlaylistListHost) ?? Liquid.CardY;
            Liquid.Settle(besideLanding, () => _ = MoveNextToFolderSafeAsync(vm, draggedId, besideFolder.Header, besideFolder.After));
            return;
        }

        PlaylistNavItem? destination;
        bool placeAfter;
        int landingIndex;
        if (folder != null)
        {
            destination = folder;
            placeAfter = false;
            landingIndex = vm.SidebarRows.IndexOf(folder);
        }
        else
        {
            destination = target >= 0 && target < vm.SidebarRows.Count && target != source
                ? vm.SidebarRows[target]
                : null;
            // Dropped below its old spot = lands after the row it displaced, above = before.
            placeAfter = target > source;
            landingIndex = destination != null ? target : source;
        }

        var landing = LiquidReorder.SlotTop(PlaylistList, landingIndex, PlaylistListHost) ?? Liquid.CardY;
        Liquid.Settle(landing, () =>
        {
            if (destination != null)
                _ = MovePlaylistSafeAsync(vm, draggedId, destination, placeAfter);
        });
    }

    private static async Task MovePlaylistSafeAsync(SidebarViewModel vm, Guid draggedId, PlaylistNavItem target, bool placeAfter)
    {
        try
        {
            await vm.MovePlaylistAsync(draggedId, target, placeAfter);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the animation frame: an escaped exception would be lost.
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Playlist move failed: {ex.Message}");
        }
    }

    private static async Task MoveNextToFolderSafeAsync(SidebarViewModel vm, Guid draggedId, PlaylistNavItem folder, bool placeAfter)
    {
        try
        {
            await vm.MovePlaylistNextToFolderAsync(draggedId, folder, placeAfter);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the animation frame: an escaped exception would be lost.
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Playlist move failed: {ex.Message}");
        }
    }

    private void SettleFolderDrag(SidebarViewModel vm, PlaylistNavItem folder, int source, int target)
    {
        // Dragged up, the block lands in front of the entry whose place it took; dragged
        // down, after the entry whose rows it passed. Back on its own spot nothing moves.
        var blocks = TopLevelBlocks();
        var count = Liquid.SourceCount;
        var destination = target < source ? blocks.Find(b => b.Start == target).Header
            : target > source ? blocks.Find(b => b.Start + b.Count == target + count).Header
            : null;
        var placeAfter = target > source;

        var landing = FolderSlotTop(destination != null ? target : source);
        Liquid.Settle(landing, () =>
        {
            if (destination != null)
                _ = MoveFolderSafeAsync(vm, folder.Label, destination, placeAfter);
        });
    }

    private static async Task MoveFolderSafeAsync(SidebarViewModel vm, string folder, PlaylistNavItem target, bool placeAfter)
    {
        try
        {
            await vm.MoveFolderAsync(folder, target, placeAfter);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the animation frame: an escaped exception would be lost.
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Folder move failed: {ex.Message}");
        }
    }

    private void OnPlaylistRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Releasing the capture on drop raises this too; the glide owns cleanup then.
        if (!_dragActive) return;
        // Otherwise lost capture is a cancel: restore visuals without moving anything.
        _dragActive = false;
        _dragItem = null;
        _folderTarget = null;
        _besideFolder = null;
        SetDropHighlight(null);
        Liquid.Cancel();
    }

    private void OnNavListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _vm == null || sender is not ListBox source)
            return;

        if (source.SelectedItem is not NavItem selected)
            return;

        // Settings opens as a sheet over the current page (owner 10-08): its row must not take
        // the selection even for this click, or the highlight jumps to it and the sheet's
        // blurred backdrop — snapshotted inside this same click — shows it there until a
        // retake moves it back. Put the lists back on the current section first, then ask
        // for the sheet; the view model's selection never changes.
        if (SidebarViewModel.OpensSheet(selected))
        {
            SyncSelectionFromViewModel();
            _vm.RequestNavigation(selected);
            return;
        }

        _isSyncingSelection = true;
        try
        {
            if (!ReferenceEquals(_vm.SelectedNavItem, selected))
                _vm.SelectedNavItem = selected;

            foreach (var list in GetNavLists())
            {
                if (!ReferenceEquals(list, source) && list.SelectedItem != null)
                    list.SelectedItem = null;
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void SyncSelectionFromViewModel()
    {
        if (_isSyncingSelection)
            return;

        _isSyncingSelection = true;
        try
        {
            var selected = _vm?.SelectedNavItem;
            foreach (var list in GetNavLists())
            {
                if (selected != null && ListContainsItem(list, selected))
                    list.SelectedItem = selected;
                else
                    list.SelectedItem = null;
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private bool ListContainsItem(ListBox list, NavItem item)
    {
        foreach (var entry in list.ItemsSource ?? Array.Empty<object>())
        {
            if (ReferenceEquals(entry, item))
                return true;
        }

        return false;
    }

    private ListBox[] GetNavLists() => new[] { NavList, FavoritesList, PlaylistList };

    // ── Rail search capsule ──
    // A non-light-dismiss overlay Popup (it stays open while the user works with the
    // filtered page, except when EMPTY: see OnHostPointerPressed), pinned exactly over the
    // rail button, so hiding the button and growing the capsule out of its 48px hover disc
    // reads as the button turning into the pill. Popup.IsOpen follows TopBar.IsSearchOpen
    // through SyncSearchPopupToViewModel instead of a binding, so EVERY close (Esc, the
    // cap, Ctrl+F, a click outside, a navigation, search being disabled) plays the same
    // collapse, and an open landing mid-collapse turns it around from where it is.
    //
    // Motion: per-instance Transitions with CubicBezierEase (SplineEasing is broken here),
    // staggered so the field never fights the shape. Open: the shape leads with a long
    // ease-out, surface + shadow fade in as it leaves the rail, the text follows once there
    // is room. Close is quicker: the text goes first, the shape shrinks back to the disc,
    // and the surface dissolves into the rail just before the hand-off to the button.

    internal const double SearchOpenMs = 380;
    internal const double SearchCloseMs = 240;     // total, including the lead below
    private const double SearchCloseLeadMs = 40;   // field starts fading before the shape moves
    internal const double SearchCapsuleClosedSize = 48;  // the rail button's own disc (nav-row size)
    internal const double SearchCapsuleOpenHeight = 40;  // PillDialog's pill-field height
    internal const double SearchCapsuleOpenWidth = 250;  // 48 cap - 6 + 196 field + 12 right pad
    // The rail button's left edge in both settled rail states (RailActions' 6px margin; its
    // icon grid sits 10px in, at x=16, either way). A CONSTANT, not a live measurement:
    // the hover collapse animates the icon through ~80px, and an open landing inside that
    // window used to pin the capsule wherever the slide happened to be.
    internal const double SearchCapsuleX = 6;
    private const double SearchGlyphOpenOpacity = 0.75;
    private bool _searchCollapsing;
    private bool _searchLandOnHover;
    private DispatcherTimer? _searchCloseTimer;

    private void OnSearchButtonClick(object? sender, RoutedEventArgs e)
    {
        var topBar = _vm?.TopBar;
        if (topBar == null) return;

        if (topBar.IsSearchOpen)
            // From the cap the pointer sits on the disc the capsule lands on, which the
            // restored button paints with its hover fill: land tinted.
            CloseSearchPopup(landOnHover: ReferenceEquals(sender, SearchCapButton));
        else
            topBar.OpenSearchCommand.Execute(null);
    }

    // Tunnels on the whole capsule: Esc used to work only from the text box, so with focus
    // on the cap or the Clear button it fell through to the window, which wiped the query
    // and left the (now empty) pill on screen.
    private void OnSearchCapsuleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSearchPopup();
    }

    private void OnSearchClearClick(object? sender, RoutedEventArgs e)
    {
        // Clear empties the query, which hides this button while it holds focus (a click
        // focuses it), so focus fell out of the pill and the next query typed went nowhere.
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchPopup.IsOpen && !_searchCollapsing) SearchBox.Focus();
        }, DispatcherPriority.Input);
    }

    private void OnTopBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TopBarViewModel.IsSearchOpen))
            SyncSearchPopupToViewModel();
    }

    private void SyncSearchPopupToViewModel()
    {
        if (_topBarVm is not { } topBar) return;
        if (!topBar.IsSearchOpen)
            CollapseSearchCapsule();
        else if (!SearchPopup.IsOpen)
            SearchPopup.IsOpen = true;           // OnSearchPopupOpened runs the morph
        else if (_searchCollapsing)
            ReopenSearchCapsule();
    }

    private void OnSearchPopupOpened(object? sender, EventArgs e)
    {
        EnsureSearchPopupPinned();
        StopSearchCloseTimer();
        _searchCollapsing = false;

        // Snap (no transitions: leftovers from a prior open would tween the reset itself)
        // to the button's own disc, tinted when the pointer is on it (a click) so the
        // hand-off is pixel-identical, then grow on the next frame so the transitions
        // animate the change.
        var hovered = SearchButton.IsPointerOver;
        SearchButton.Opacity = 0;
        SetSearchTransitions(null);
        ApplySearchCapsulePose(open: false, tinted: hovered);
        Dispatcher.UIThread.Post(() =>
        {
            // Closed again before the first frame (a double click, Ctrl+F twice).
            if (_searchCollapsing || !SearchPopup.IsOpen) return;
            SetSearchTransitions(opening: true);
            ApplySearchCapsulePose(open: true, tinted: true);
            SearchBox.Focus();
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Pins the capsule against the stationary sidebar root (the popup's anchor).
    /// X is the settled-rail constant — see SearchCapsuleX. Y is measured live: the
    /// vertical stack never animates, so that read cannot catch a transition
    /// mid-slide (-10: the button's padding around the icon grid; the 48px capsule
    /// host covers the button row exactly).
    /// Called at attach (so the FIRST open of the run doesn't spend a frame at the
    /// Popup's default 0,0 offsets before Opened runs) and again on every open.
    /// </summary>
    private void EnsureSearchPopupPinned()
    {
        // The shadow room's padding sits around the capsule inside the popup.
        var room = SearchShadowRoom.Padding;
        SearchPopup.HorizontalOffset = SearchCapsuleX - room.Left;
        if (SearchIconHost.TranslatePoint(new Point(0, -10), this) is { } capsuleOrigin)
            SearchPopup.VerticalOffset = capsuleOrigin.Y - room.Top;
    }

    /// <summary>Every UI-side close: flips the view model, whose change plays the collapse
    /// (see SyncSearchPopupToViewModel). Without a view model, collapses directly.</summary>
    private void CloseSearchPopup(bool landOnHover = false)
    {
        _searchLandOnHover = landOnHover;
        if (_topBarVm is { IsSearchOpen: true } topBar)
            topBar.IsSearchOpen = false;
        else
            CollapseSearchCapsule();
        _searchLandOnHover = false;
    }

    private void CollapseSearchCapsule()
    {
        if (!SearchPopup.IsOpen || _searchCollapsing) return;

        // Mirror of the open: back to the button's disc, then close the popup (its Closed
        // handler restores the real button, so the hand-off happens while both are
        // pixel-identical discs).
        _searchCollapsing = true;
        PinSearchCapsuleWhereItIs();   // Esc / a second click during the open
        SetSearchTransitions(opening: false);
        ApplySearchCapsulePose(open: false, tinted: _searchLandOnHover);

        // A one-shot hand-off, not motion: the DispatcherTimer grid only decides when the
        // popup goes, +1 frame after the shrink has landed. One timer, stopped by any
        // reopen: a stale tick used to close a pill reopened in the meantime.
        StopSearchCloseTimer();
        _searchCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchCloseMs + 16) };
        _searchCloseTimer.Tick += OnSearchCloseTimerTick;
        _searchCloseTimer.Start();
    }

    private void OnSearchCloseTimerTick(object? sender, EventArgs e)
    {
        StopSearchCloseTimer();
        _searchCollapsing = false;
        SearchPopup.IsOpen = false;
    }

    /// <summary>An open request landing mid-collapse (a fast second click, Ctrl+F again):
    /// turn around from wherever the shape is instead of being dropped.</summary>
    private void ReopenSearchCapsule()
    {
        StopSearchCloseTimer();
        _searchCollapsing = false;
        PinSearchCapsuleWhereItIs();
        SetSearchTransitions(opening: true);
        ApplySearchCapsulePose(open: true, tinted: true);
        SearchBox.Focus();
    }

    /// <summary>
    /// Re-targeting a running transition restarts it from the property's BASE (local)
    /// value, not from where it is on screen (probed: a reopen at 243px snapped to the 48px
    /// disc and regrew; an Esc during the open jumped to full width before shrinking). So
    /// every reversal first pins each animated value where it currently is: GetValue
    /// returns the animated value, and dropping the transitions then writing it back as the
    /// local value changes nothing visible.
    /// </summary>
    private void PinSearchCapsuleWhereItIs()
    {
        var width = SearchPopupContent.Width;
        var height = SearchPopupContent.Height;
        var surface = SearchCapsuleSurface.Opacity;
        var tint = SearchCapsuleTint.Opacity;
        var ring = SearchCapsuleRing.Opacity;
        var glyph = SearchCapsuleGlyph.Opacity;
        var field = SearchFieldArea.Opacity;
        var slide = SearchFieldArea.RenderTransform;
        SetSearchTransitions(null);
        SearchPopupContent.Width = width;
        SearchPopupContent.Height = height;
        SearchCapsuleSurface.Opacity = surface;
        SearchCapsuleTint.Opacity = tint;
        SearchCapsuleRing.Opacity = ring;
        SearchCapsuleGlyph.Opacity = glyph;
        SearchFieldArea.Opacity = field;
        SearchFieldArea.RenderTransform = slide;
    }

    private void StopSearchCloseTimer()
    {
        if (_searchCloseTimer == null) return;
        _searchCloseTimer.Stop();
        _searchCloseTimer.Tick -= OnSearchCloseTimerTick;
        _searchCloseTimer = null;
    }

    private void OnSearchPopupClosed(object? sender, EventArgs e)
    {
        // Restore the real button whenever the popup actually closes, including closes
        // that skipped the collapse (the view detaching). Clear (not set) so the
        // :disabled style opacity still applies.
        StopSearchCloseTimer();
        _searchCollapsing = false;
        SearchButton.ClearValue(OpacityProperty);
        if (_topBarVm is { IsSearchOpen: true } topBar)
            topBar.IsSearchOpen = false;
    }

    private void OnSearchOpenRequested(object? sender, EventArgs e)
    {
        // An open request landing while the pill is already up re-focuses the
        // box (a fresh open is focused by OnSearchPopupOpened instead).
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchPopup.IsOpen && !_searchCollapsing) SearchBox.Focus();
        }, DispatcherPriority.Render);
    }

    private void OnSearchCloseRequested(object? sender, EventArgs e)
    {
        // Ctrl+F toggling an open pill shut: same collapse path as Esc.
        CloseSearchPopup();
    }

    // The pill stays up while the user works with the page it is filtering — but
    // an EMPTY pill filters nothing, so a click anywhere else reads as "done
    // searching" and collapses it. Typed text keeps it sticky. Watched on the
    // TopLevel because the capsule renders in its overlay layer, not under this
    // control.
    private TopLevel? _outsideClickHost;

    private void AttachOutsideClickWatcher()
    {
        DetachOutsideClickWatcher();
        _outsideClickHost = TopLevel.GetTopLevel(this);
        _outsideClickHost?.AddHandler(PointerPressedEvent, OnHostPointerPressed, RoutingStrategies.Tunnel);
    }

    private void DetachOutsideClickWatcher()
    {
        _outsideClickHost?.RemoveHandler(PointerPressedEvent, OnHostPointerPressed);
        _outsideClickHost = null;
    }

    private void OnHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var topBar = _vm?.TopBar;
        if (topBar is not { IsSearchOpen: true } || !string.IsNullOrEmpty(topBar.SearchText))
            return;
        if (e.Source is Visual source && IsInsideSearchCapsule(source))
            return;
        // Not marked handled: dismissal must not eat the click the user aimed at
        // the page (a nav item, a play button) — it lands normally.
        CloseSearchPopup();
    }

    private bool IsInsideSearchCapsule(Visual node)
    {
        for (Visual? v = node; v != null; v = v.GetVisualParent())
            if (ReferenceEquals(v, SearchPopupContent)) return true;
        return false;
    }

    private void ApplySearchCapsulePose(bool open, bool tinted)
    {
        SearchPopupContent.Width = open ? SearchCapsuleOpenWidth : SearchCapsuleClosedSize;
        SearchPopupContent.Height = open ? SearchCapsuleOpenHeight : SearchCapsuleClosedSize;
        SearchCapsuleSurface.Opacity = open ? 1 : 0;
        SearchCapsuleTint.Opacity = open || tinted ? 1 : 0;
        SearchCapsuleRing.Opacity = open ? 1 : 0;
        SearchCapsuleGlyph.Opacity = open ? SearchGlyphOpenOpacity : 1;
        SearchFieldArea.Opacity = open ? 1 : 0;
        SearchFieldArea.RenderTransform = TransformOperations.Parse(open ? "translateX(0px)" : "translateX(-8px)");
    }

    /// <summary>null clears every capsule transition (the snap to the start pose).</summary>
    private void SetSearchTransitions(bool? opening)
    {
        if (opening is not { } isOpening)
        {
            SearchPopupContent.Transitions = null;
            SearchCapsuleSurface.Transitions = null;
            SearchCapsuleTint.Transitions = null;
            SearchCapsuleRing.Transitions = null;
            SearchCapsuleGlyph.Transitions = null;
            SearchFieldArea.Transitions = null;
            return;
        }

        static DoubleTransition Fade(AvaloniaProperty property, double ms, Avalonia.Animation.Easings.Easing ease, double delayMs = 0) => new()
        {
            Property = property,
            Duration = TimeSpan.FromMilliseconds(ms),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = ease,
        };

        if (isOpening)
        {
            var grow = new CubicBezierEase(0.32, 0.72, 0, 1);    // long, soft settle
            var soft = new CubicBezierEase(0.25, 0.1, 0.25, 1);  // CSS "ease"
            SearchPopupContent.Transitions = new Transitions
            {
                Fade(WidthProperty, SearchOpenMs, grow),
                Fade(HeightProperty, SearchOpenMs, grow),
            };
            SearchCapsuleSurface.Transitions = new Transitions { Fade(Visual.OpacityProperty, 120, soft) };
            SearchCapsuleTint.Transitions = new Transitions { Fade(Visual.OpacityProperty, 120, soft) };
            SearchCapsuleRing.Transitions = new Transitions { Fade(Visual.OpacityProperty, 160, soft, 60) };
            SearchCapsuleGlyph.Transitions = new Transitions { Fade(Visual.OpacityProperty, 260, grow) };
            SearchFieldArea.Transitions = new Transitions
            {
                Fade(Visual.OpacityProperty, 220, grow, 90),
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(320), Delay = TimeSpan.FromMilliseconds(60), Easing = grow },
            };
        }
        else
        {
            var shrink = new CubicBezierEase(0.4, 0, 0.2, 1);    // standard ease-in-out
            var shapeMs = SearchCloseMs - SearchCloseLeadMs;
            SearchPopupContent.Transitions = new Transitions
            {
                Fade(WidthProperty, shapeMs, shrink, SearchCloseLeadMs),
                Fade(HeightProperty, shapeMs, shrink, SearchCloseLeadMs),
            };
            // The surface and the shadow dissolve into the rail over the last frames, as
            // the disc settles over the button (bare unless it lands on the pointer).
            SearchCapsuleSurface.Transitions = new Transitions { Fade(Visual.OpacityProperty, 90, shrink, SearchCloseMs - 90) };
            SearchCapsuleTint.Transitions = new Transitions { Fade(Visual.OpacityProperty, 90, shrink, SearchCloseMs - 90) };
            SearchCapsuleRing.Transitions = new Transitions { Fade(Visual.OpacityProperty, 120, shrink) };
            SearchCapsuleGlyph.Transitions = new Transitions { Fade(Visual.OpacityProperty, 160, shrink) };
            SearchFieldArea.Transitions = new Transitions
            {
                Fade(Visual.OpacityProperty, 100, shrink),
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = shrink },
            };
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.FolderRowsInserted -= OnFolderRowsInserted;
            if (_vm.FoldFolderRows == FoldFolderRowsAsync) _vm.FoldFolderRows = null;
        }
        AttachTopBar(null);
    }

    // ── Playlist folder fold ──
    // A folder's playlists are rows inserted and removed on toggle; these ease them in
    // and out with the Settings sub-menu Glide (see FoldingListBoxItem).

    /// <summary>Rows an expand just inserted, shut and eased open as their containers are made.</summary>
    private readonly HashSet<PlaylistNavItem> _pendingUnfold = new();

    private IEnumerable<PlaylistNavItem> FolderRows(string folder)
        => _vm?.SidebarRows.Where(r => !r.IsFolder && r.IsInFolder
               && string.Equals(r.Folder.Trim(), folder, StringComparison.OrdinalIgnoreCase))
           ?? Enumerable.Empty<PlaylistNavItem>();

    private void OnFolderRowsInserted(string folder)
    {
        foreach (var row in FolderRows(folder))
        {
            if (PlaylistList.ContainerFromItem(row) is FoldingListBoxItem existing)
            {
                existing.SnapShut();
                _ = existing.Unfold();
            }
            else _pendingUnfold.Add(row);
        }
        // Rows outside the viewport never get a container this pass; don't animate them later.
        Dispatcher.UIThread.Post(_pendingUnfold.Clear, DispatcherPriority.Background);
    }

    /// <summary>
    /// Stretches each open folder's tray (drawn by its header row, see the group-tray styles)
    /// from the header's top to the bottom of the group's last realized row, measured from the
    /// containers' real bounds every layout pass, so it tracks the fold and drag-reorder. The
    /// header container is put below its group (ZIndex) so the faint tray never lies over a
    /// recycled member that happens to come earlier in the panel's children.
    /// </summary>
    private void UpdateGroupTrays(object? sender, EventArgs e)
    {
        var rows = PlaylistList.GetRealizedContainers()
            .OfType<Control>()
            .Where(c => c.DataContext is PlaylistNavItem && c.IsVisible)
            .OrderBy(c => c.Bounds.Top)
            .ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var header = rows[i];
            var isHeader = header.DataContext is PlaylistNavItem { IsGroupHeader: true };
            var z = isHeader ? -1 : 0;
            if (header.ZIndex != z) header.ZIndex = z;
            if (!isHeader) continue;

            var tray = header.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("group-tray"));
            if (tray == null) continue;

            var bottom = header.Bounds.Bottom;
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (rows[j].DataContext is not PlaylistNavItem { IsGroupMember: true } and not PlaylistNavItem { IsGroupLast: true })
                    break;
                bottom = rows[j].Bounds.Bottom;
                if (rows[j].DataContext is PlaylistNavItem { IsGroupLast: true }) break;
            }
            var height = Math.Max(0, bottom - header.Bounds.Top);
            if (double.IsNaN(tray.Height) || Math.Abs(tray.Height - height) > 0.25)
                tray.Height = height;
            if (double.IsNaN(tray.Width) || Math.Abs(tray.Width - header.Bounds.Width) > 0.25)
                tray.Width = header.Bounds.Width;
        }
    }

    private void OnPlaylistContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not FoldingListBoxItem item
            || e.Container.DataContext is not PlaylistNavItem row
            || !_pendingUnfold.Remove(row)) return;
        // Shut before its first layout, so it never flashes in at full height.
        item.SnapShut();
        _ = item.Unfold();
    }

    private Task FoldFolderRowsAsync(string folder)
        => Task.WhenAll(FolderRows(folder)
            .Select(r => PlaylistList.ContainerFromItem(r))
            .OfType<FoldingListBoxItem>()
            .Select(c => c.Fold())
            .ToList());
}
