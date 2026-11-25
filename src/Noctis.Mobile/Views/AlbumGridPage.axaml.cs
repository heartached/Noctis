using System.ComponentModel;
using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class AlbumGridPage : UserControl
{
    private AlbumGridPageViewModel? _vm;

    public AlbumGridPage()
    {
        InitializeComponent();
        AlbumRows.ContainerPrepared += (_, _) => Services.FrameStallMonitor.Count("album rows prepared");
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as AlbumGridPageViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>A new sort starts the grid at its top, not wherever the old order was.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlbumGridPageViewModel.Sort) or nameof(AlbumGridPageViewModel.IsDescending))
            AlbumScroll.Offset = default;
    }
}
