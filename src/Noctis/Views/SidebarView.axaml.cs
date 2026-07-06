using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        SyncSelectionFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.SelectedNavItem))
            SyncSelectionFromViewModel();
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

    // ── Rail search flyout open/close animation ──
    // Same mechanism as MenuOpenAnimation (per-instance transitions, settle on the
    // next frame, cancel-then-animate close); scoped here because that helper is
    // specialized to ContextMenu/MenuFlyout.

    private const double SearchOpenMs = 150;
    private const double SearchCloseMs = 120;
    private bool _searchCloseAnimating;
    private bool _searchCloseAfterAnimation;

    private void OnSearchFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout flyout || flyout.Content is not Control content)
            return;

        // Slide in from the search icon: start hidden + nudged left, settle into
        // place on the next frame so the transitions animate the change.
        EnsureSearchTransitions(content, TimeSpan.FromMilliseconds(SearchOpenMs));
        content.Opacity = 0;
        content.RenderTransform = TransformOperations.Parse("translateX(-10px)");
        Dispatcher.UIThread.Post(() =>
        {
            content.Opacity = 1;
            content.RenderTransform = TransformOperations.Parse("translateX(0px)");
            var box = content as TextBox ?? content.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            box?.Focus();
        }, DispatcherPriority.Render);
    }

    private void OnSearchFlyoutClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Flyout flyout || flyout.Content is not Control content)
            return;

        if (_searchCloseAfterAnimation)
        {
            _searchCloseAfterAnimation = false;
            return;
        }

        if (_searchCloseAnimating)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _searchCloseAnimating = true;
        EnsureSearchTransitions(content, TimeSpan.FromMilliseconds(SearchCloseMs));
        content.Opacity = 0;
        content.RenderTransform = TransformOperations.Parse("translateX(-8px)");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchCloseMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _searchCloseAnimating = false;
            if (flyout.IsOpen)
            {
                _searchCloseAfterAnimation = true;
                flyout.Hide();
            }
        };
        timer.Start();
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
    }
}
