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
        DataContextChanged += OnDataContextChanged;
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
            _vm.PropertyChanged += OnViewModelPropertyChanged;
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
        }
        _topBarVm = topBar;
        if (_topBarVm != null)
        {
            _topBarVm.SearchOpenRequested += OnSearchOpenRequested;
            _topBarVm.SearchCloseRequested += OnSearchCloseRequested;
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

    private (ListBoxItem? Container, PlaylistNavItem? Item) HitPlaylistRow(DragEventArgs e)
    {
        var pos = e.GetPosition(PlaylistList);
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

        var (container, item) = HitPlaylistRow(e);
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

            var (_, item) = HitPlaylistRow(e);
            if (!CanAcceptTracks(item) || _vm == null || item?.PlaylistId is not { } targetId) return;
            e.Handled = true;
            await _vm.AddTracksToPlaylist(targetId, tracks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Drop failed: {ex.Message}");
        }
    }

    // ── Playlist reorder (pointer-tracked, same liquid motion as the queue) ──
    //
    // A playlist row lifts into a card (#PlaylistDragCard, drawn with the list's own row
    // template) that springs after the pointer while the other rows slide apart to open a
    // gap where it will land (LiquidReorder). Over a folder header the gap closes and the
    // folder lights up instead: dropping there moves the playlist into it. On release the
    // card glides to its spot and SidebarViewModel.MovePlaylistAsync commits.
    // A folder header drags the same way, its open playlists riding in the card as one
    // block that lands between the other folders (SidebarViewModel.MoveFolderAsync).

    private const double PlaylistDragThreshold = 6;
    private PlaylistNavItem? _dragItem;
    private Point _dragStart;
    private double _dragGrabY;
    private bool _dragActive;
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
            if (!(_dragItem.IsFolder ? StartFolderDrag(e) : StartPlaylistDrag(e))) return;
        }

        var cardTop = e.GetPosition(PlaylistListHost).Y - _dragGrabY;
        Liquid.MoveCardTo(cardTop);
        if (_dragItem.IsFolder)
        {
            Liquid.TargetIndex = NearestFolderSlot(cardTop);
            e.Handled = true;
            return;
        }
        var target = LiquidReorder.NearestSlot(PlaylistList, PlaylistListHost, cardTop + Liquid.Pitch / 2);
        if (target < 0) return;

        // A folder header is a destination, not a slot: close the gap and light it up.
        if (target < _vm.SidebarRows.Count && _vm.SidebarRows[target].IsFolder)
        {
            SetDropHighlight(PlaylistList.ContainerFromIndex(target) as ListBoxItem);
            Liquid.TargetIndex = Liquid.SourceIndex;
            _folderTarget = _vm.SidebarRows[target];
        }
        else
        {
            SetDropHighlight(null);
            Liquid.TargetIndex = target;
            _folderTarget = null;
        }
        e.Handled = true;
    }

    private PlaylistNavItem? _folderTarget;

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
        var block = FolderBlocks().Find(b => ReferenceEquals(b.Header, _dragItem));
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

    /// <summary>Each folder header with the rows it carries: itself plus its open playlists.</summary>
    private List<(PlaylistNavItem Header, int Start, int Count)> FolderBlocks()
    {
        var blocks = new List<(PlaylistNavItem Header, int Start, int Count)>();
        if (_vm == null) return blocks;
        var rows = _vm.SidebarRows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsFolder) continue;
            var end = i + 1;
            while (end < rows.Count && rows[end].IsInFolder) end++;
            blocks.Add((rows[i], i, end - i));
        }
        return blocks;
    }

    /// <summary>
    /// Row where the dragged folder block would start if dropped now: in front of a folder
    /// above it, after one below it (the rows it passes close up behind it), or its own spot,
    /// whichever is nearest the card. Folders only move among folders; pinned and loose
    /// playlists keep their sections.
    /// </summary>
    private int NearestFolderSlot(double cardTop)
    {
        var source = Liquid.SourceIndex;
        var best = source;
        var bestDist = Math.Abs(cardTop - FolderSlotTop(source));
        foreach (var (_, start, count) in FolderBlocks())
        {
            if (start == source) continue;
            var slot = start < source ? start : start + count - Liquid.SourceCount;
            var dist = Math.Abs(cardTop - FolderSlotTop(slot));
            if (dist < bestDist) { bestDist = dist; best = slot; }
        }
        return best;
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
            // A press on a folder header that never became a drag is a click: toggle it.
            if (_dragItem is { IsFolder: true } clicked && ReferenceEquals(RowOf(e.Source)?.DataContext, clicked))
                _vm?.ToggleFolderExpansion(clicked.Label);
            _dragItem = null;
            return;
        }
        e.Handled = true;

        var vm = _vm;
        var dragged = _dragItem;
        var folder = _folderTarget;
        var source = Liquid.SourceIndex;
        var target = Liquid.TargetIndex;
        _dragActive = false;
        _dragItem = null;
        _folderTarget = null;
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

    private void SettleFolderDrag(SidebarViewModel vm, PlaylistNavItem folder, int source, int target)
    {
        // Dragged up, the block lands in front of the folder whose place it took; dragged
        // down, after the folder whose rows it passed. Back on its own spot nothing moves.
        var blocks = FolderBlocks();
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
        SetDropHighlight(null);
        Liquid.Cancel();
    }

    private void OnNavListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _vm == null || sender is not ListBox source)
            return;

        if (source.SelectedItem is not NavItem selected)
            return;

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

    // ── Rail search capsule morph animation ──
    // Same mechanism as MenuOpenAnimation (per-instance transitions, settle on the
    // next frame, animate-then-close); scoped here because that helper is
    // specialized to ContextMenu/MenuFlyout. The capsule is anchored pixel-exact
    // over the search button's icon circle (see the Popup comment in XAML), so
    // hiding the button while the popup is open and growing the capsule rightward
    // from the bare 32px circle reads as the button morphing into the pill. The
    // pill is a non-light-dismiss Popup so it stays open while the user interacts
    // with the filtered page beneath it — except when it is EMPTY, where a click
    // anywhere else dismisses it (see OnHostPointerPressed).

    private const double SearchOpenMs = 320;
    private const double SearchCloseMs = 240;      // total, including the lead below
    private const double SearchCloseLeadMs = 50;   // field starts fading before the width moves
    // The capsule sits a lip's width left of the icon (Border Padding.Left in XAML),
    // so the magnifier lands INSIDE the rounded cap instead of on its curve while
    // staying exactly over the (hidden) rail button's glyph.
    private const double SearchCapsuleLip = 8;
    private const double SearchCapsuleClosedWidth = 32 + SearchCapsuleLip;   // rail circle + left lip
    private const double SearchCapsuleOpenWidth = 225 + SearchCapsuleLip;    // + 1.5px borders, 30 icon cap, 180 field, 12 right pad
    // Both settled rail states put SearchIconHost at x=16 (expanded: 6 panel margin +
    // 10 padding; collapsed: centered to the same spot — see the rail-action styles).
    // X must be this CONSTANT, not a live measurement: the hover collapse animates the
    // icon through ~80px, and an open landing inside that window used to pin the
    // capsule wherever the slide happened to be. 16 − 2 circle overhang − lip.
    private const double SearchCapsuleX = 16 - 2 - SearchCapsuleLip;
    private bool _searchCloseAnimating;

    private void OnSearchButtonClick(object? sender, RoutedEventArgs e)
    {
        var topBar = _vm?.TopBar;
        if (topBar == null) return;

        if (topBar.IsSearchOpen)
            CloseSearchPopup(topBar);
        else
            topBar.OpenSearchCommand.Execute(null);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        var topBar = _vm?.TopBar;
        if (topBar != null) CloseSearchPopup(topBar);
    }

    private void OnSearchPopupOpened(object? sender, EventArgs e)
    {
        EnsureSearchPopupPinned();

        // Morph out of the button: hide the real button (the capsule's left cap is
        // a pixel-exact copy of its icon circle), snap to the bare circle without
        // animating (transitions left over from a prior open would tween the reset
        // itself), then grow rightward while the field fades/slides in on the next
        // frame so the transitions animate the change.
        _searchCloseAnimating = false;
        SearchButton.Opacity = 0;
        SearchPopupContent.Transitions = null;
        SearchFieldArea.Transitions = null;
        SearchPopupContent.Width = SearchCapsuleClosedWidth;
        SearchFieldArea.Opacity = 0;
        SearchFieldArea.RenderTransform = TransformOperations.Parse("translateX(-6px)");
        EnsureSearchTransitions(opening: true);
        Dispatcher.UIThread.Post(() =>
        {
            SearchPopupContent.Width = SearchCapsuleOpenWidth;
            SearchFieldArea.Opacity = 1;
            SearchFieldArea.RenderTransform = TransformOperations.Parse("translateX(0px)");
            SearchBox.Focus();
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Pins the capsule against the stationary sidebar root (the popup's anchor).
    /// X is the settled-rail constant — see SearchCapsuleX. Y is measured live: the
    /// vertical stack never animates, so that read cannot catch a transition
    /// mid-slide (-2: the capsule overhangs the 28px icon grid by 2px per side).
    /// Called at attach (so the FIRST open of the run doesn't spend a frame at the
    /// Popup's default 0,0 offsets before Opened runs) and again on every open.
    /// </summary>
    private void EnsureSearchPopupPinned()
    {
        SearchPopup.HorizontalOffset = SearchCapsuleX;
        if (SearchIconHost.TranslatePoint(new Point(0, -2), this) is { } capsuleOrigin)
            SearchPopup.VerticalOffset = capsuleOrigin.Y;
    }

    private void CloseSearchPopup(TopBarViewModel topBar)
    {
        if (_searchCloseAnimating) return;

        // Mirror of the open animation: collapse back to the icon circle, then
        // close the popup (its Closed handler restores the real button, so the
        // hand-off happens while both are pixel-identical circles).
        _searchCloseAnimating = true;
        EnsureSearchTransitions(opening: false);
        SearchPopupContent.Width = SearchCapsuleClosedWidth;
        SearchFieldArea.Opacity = 0;
        SearchFieldArea.RenderTransform = TransformOperations.Parse("translateX(-6px)");

        // +1 frame so the width lands on the closed circle before the popup goes.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchCloseMs + 16) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _searchCloseAnimating = false;
            topBar.IsSearchOpen = false;
        };
        timer.Start();
    }

    private void OnSearchPopupClosed(object? sender, EventArgs e)
    {
        // Restore the real button whenever the popup actually closes — including
        // orphan closes where a view model flips IsSearchOpen without the collapse
        // animation running. Clear (not set) so the :disabled style opacity still
        // applies.
        SearchButton.ClearValue(OpacityProperty);
    }

    private void OnSearchOpenRequested(object? sender, EventArgs e)
    {
        // An open request landing while the pill is already up re-focuses the
        // box (a fresh open is focused by OnSearchPopupOpened instead).
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchPopup.IsOpen) SearchBox.Focus();
        }, DispatcherPriority.Render);
    }

    private void OnSearchCloseRequested(object? sender, EventArgs e)
    {
        // Ctrl+F toggling an open pill shut: same collapse path as Esc.
        var topBar = _vm?.TopBar;
        if (topBar != null) CloseSearchPopup(topBar);
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
        CloseSearchPopup(topBar);
    }

    private bool IsInsideSearchCapsule(Visual node)
    {
        for (Visual? v = node; v != null; v = v.GetVisualParent())
            if (ReferenceEquals(v, SearchPopupContent)) return true;
        return false;
    }

    private void EnsureSearchTransitions(bool opening)
    {
        // CubicBezierEase, not SplineEasing (mis-stores Y1 and can freeze after frame 1).
        // Staggered so the field never fights the width: on open the capsule leads and
        // the text follows once there is room; on close the text is gone before the
        // shrinking edge reaches it, so nothing is visibly clipped mid-collapse.
        if (opening)
        {
            var grow = new CubicBezierEase(0.32, 0.72, 0, 1);   // long, soft settle
            SearchPopupContent.Transitions = new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(SearchOpenMs), Easing = grow },
            };
            SearchFieldArea.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Delay = TimeSpan.FromMilliseconds(70), Easing = grow },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(280), Delay = TimeSpan.FromMilliseconds(40), Easing = grow },
            };
        }
        else
        {
            var shrink = new CubicBezierEase(0.4, 0, 0.2, 1);   // standard ease-in-out
            SearchPopupContent.Transitions = new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(SearchCloseMs - SearchCloseLeadMs), Delay = TimeSpan.FromMilliseconds(SearchCloseLeadMs), Easing = shrink },
            };
            SearchFieldArea.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(110), Easing = shrink },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = shrink },
            };
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        AttachTopBar(null);
    }
}
