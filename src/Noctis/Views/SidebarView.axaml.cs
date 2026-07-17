using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    // ── Rail search pill open/close animation ──
    // Same mechanism as MenuOpenAnimation (per-instance transitions, settle on the
    // next frame, animate-then-close); scoped here because that helper is
    // specialized to ContextMenu/MenuFlyout. The pill is a non-light-dismiss Popup
    // so it stays open while the user interacts with the filtered page beneath it.

    private const double SearchAnimMs = 150;
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
        // Slide in from the search icon: start hidden + nudged left, settle into
        // place on the next frame so the transitions animate the change.
        var target = SearchPopupContent;
        _searchCloseAnimating = false;
        EnsureSearchTransitions(target, TimeSpan.FromMilliseconds(SearchAnimMs));
        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse("translateX(-10px)");
        Dispatcher.UIThread.Post(() =>
        {
            target.Opacity = 1;
            target.RenderTransform = TransformOperations.Parse("translateX(0px)");
            SearchBox.Focus();
        }, DispatcherPriority.Render);
    }

    private void CloseSearchPopup(TopBarViewModel topBar)
    {
        if (_searchCloseAnimating) return;

        // Mirror of the open animation: same distance, duration and easing.
        _searchCloseAnimating = true;
        var target = SearchPopupContent;
        EnsureSearchTransitions(target, TimeSpan.FromMilliseconds(SearchAnimMs));
        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse("translateX(-10px)");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchAnimMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _searchCloseAnimating = false;
            topBar.IsSearchOpen = false;
        };
        timer.Start();
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

    private static void EnsureSearchTransitions(Control control, TimeSpan duration)
    {
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = new CubicEaseOut() },
        };
    }

    private void UnsubscribeFromViewModel()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        AttachTopBar(null);
    }
}
