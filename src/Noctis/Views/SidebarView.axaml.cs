using Avalonia.Controls;
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

    private void UnsubscribeFromViewModel()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
