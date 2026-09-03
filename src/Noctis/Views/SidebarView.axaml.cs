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
        // Drop target: tracks dragged from any list land in a playlist; a dragged
        // playlist row reorders / moves into a folder. Payloads come from DragFileBehavior.
        DragDrop.SetAllowDrop(PlaylistList, true);
        PlaylistList.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        PlaylistList.AddHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave);
        PlaylistList.AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
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

    // ── Playlist list as a drop target ──

    private ListBoxItem? _dropHighlighted;

    private (ListBoxItem? Container, PlaylistNavItem? Item, bool PlaceAfter) HitPlaylistRow(DragEventArgs e)
    {
        var pos = e.GetPosition(PlaylistList);
        foreach (var container in PlaylistList.GetRealizedContainers())
        {
            if (container is not ListBoxItem row) continue;
            var origin = row.TranslatePoint(new Point(0, 0), PlaylistList);
            if (origin == null) continue;
            var rect = new Rect(origin.Value, row.Bounds.Size);
            if (!rect.Contains(pos)) continue;
            var placeAfter = pos.Y > rect.Y + rect.Height / 2;
            return (row, row.DataContext as PlaylistNavItem, placeAfter);
        }
        return (null, null, false);
    }

    private void SetDropHighlight(ListBoxItem? row)
    {
        if (ReferenceEquals(_dropHighlighted, row)) return;
        _dropHighlighted?.Classes.Set("drop-target", false);
        _dropHighlighted = row;
        _dropHighlighted?.Classes.Set("drop-target", true);
    }

    private static bool CanAccept(PlaylistNavItem? item, IReadOnlyList<Track>? tracks, Guid? playlistId)
    {
        if (item == null) return false;
        if (tracks is { Count: > 0 })
            return !item.IsFolder && !item.IsSmartPlaylist && item.PlaylistId != null;
        if (playlistId != null)
            return item.IsFolder || (item.PlaylistId != null && item.PlaylistId != playlistId);
        return false;
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        var tracks = Helpers.DragFileBehavior.GetDraggedTracks(e.Data);
        var playlistId = Helpers.DragFileBehavior.GetDraggedPlaylistId(e.Data);
        if (tracks == null && playlistId == null) return; // external file drag — the window handles it

        var (container, item, _) = HitPlaylistRow(e);
        var ok = CanAccept(item, tracks, playlistId);
        SetDropHighlight(ok ? container : null);
        e.DragEffects = !ok ? DragDropEffects.None
            : playlistId != null ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPlaylistDragLeave(object? sender, DragEventArgs e) => SetDropHighlight(null);

    private async void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            SetDropHighlight(null);
            var tracks = Helpers.DragFileBehavior.GetDraggedTracks(e.Data);
            var playlistId = Helpers.DragFileBehavior.GetDraggedPlaylistId(e.Data);
            if (tracks == null && playlistId == null) return;

            var (_, item, placeAfter) = HitPlaylistRow(e);
            if (!CanAccept(item, tracks, playlistId) || _vm == null || item == null) return;
            e.Handled = true;

            if (tracks is { Count: > 0 } && item.PlaylistId is { } targetId)
                await _vm.AddTracksToPlaylist(targetId, tracks);
            else if (playlistId is { } dragged)
                await _vm.MovePlaylistAsync(dragged, item, placeAfter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SidebarView] Drop failed: {ex.Message}");
        }
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

    private const double SearchAnimMs = 250;
    // The capsule sits a lip's width left of the icon (Border Padding.Left in XAML),
    // so the magnifier lands INSIDE the rounded cap instead of on its curve while
    // staying exactly over the (hidden) rail button's glyph.
    private const double SearchCapsuleLip = 8;
    private const double SearchCapsuleClosedWidth = 32 + SearchCapsuleLip;   // rail circle + left lip
    private const double SearchCapsuleOpenWidth = 224 + SearchCapsuleLip;    // + borders, 30 icon cap, 180 field, 12 right pad
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
        SearchFieldArea.RenderTransform = TransformOperations.Parse("translateX(-8px)");
        EnsureSearchTransitions(TimeSpan.FromMilliseconds(SearchAnimMs));
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
        EnsureSearchTransitions(TimeSpan.FromMilliseconds(SearchAnimMs));
        SearchPopupContent.Width = SearchCapsuleClosedWidth;
        SearchFieldArea.Opacity = 0;
        SearchFieldArea.RenderTransform = TransformOperations.Parse("translateX(-8px)");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchAnimMs) };
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

    private void EnsureSearchTransitions(TimeSpan duration)
    {
        // ~cubic-bezier(.2,.8,.2,1): fast start, gentle settle.
        var easing = new SplineEasing(0.2, 0.8, 0.2, 1);
        SearchPopupContent.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = duration, Easing = easing },
        };
        SearchFieldArea.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = easing },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = easing },
        };
    }

    private void UnsubscribeFromViewModel()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        AttachTopBar(null);
    }
}
