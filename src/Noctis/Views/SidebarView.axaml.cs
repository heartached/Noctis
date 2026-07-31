using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
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
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnsubscribeFromViewModel();
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
        if (_topBarVm != null) _topBarVm.SearchOpenRequested -= OnSearchOpenRequested;
        _topBarVm = topBar;
        if (_topBarVm != null) _topBarVm.SearchOpenRequested += OnSearchOpenRequested;
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
    // with the filtered page beneath it.

    private const double SearchAnimMs = 250;
    private const double SearchCapsuleClosedWidth = 32;  // matches the rail button circle
    private const double SearchCapsuleOpenWidth = 224;   // borders + 30 icon cap + 180 field + 12 right pad
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
        // Ctrl+F with the pill already open: re-focus the box (a fresh open is
        // focused by OnSearchPopupOpened instead).
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchPopup.IsOpen) SearchBox.Focus();
        }, DispatcherPriority.Render);
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
