using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MetadataWindow : Window
{
    public MetadataWindow()
    {
        InitializeComponent();

        if (this.FindControl<ComboBox>("GenreCombo") is { } genreCombo)
        {
            genreCombo.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnGenreComboWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
    }

    public MetadataWindow(MetadataViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    private void OnVolumeAdjustSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            vm.VolumeAdjust = 0;
    }

    private void OnGenreComboWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsDropDownOpen)
            return;

        e.Handled = true;

        var scrollViewer = cb.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null)
            return;

        const double lineHeight = 50.0;
        var newY = scrollViewer.Offset.Y - e.Delta.Y * lineHeight;
        newY = System.Math.Max(0, System.Math.Min(newY, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newY);
    }
}
