using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SpectrogramWindow : Window
{
    private SpectrogramViewModel? _vm;

    public SpectrogramWindow()
    {
        InitializeComponent();
        // The plot's busy placeholder keeps the composed-image height so the card is the
        // same height before and after the analysis lands (width is left to the window:
        // the card caps at 1180 and the image scales down on smaller windows).
        BusyPanel.MinHeight = SpectrogramViewModel.ComposedSize.Height;
        KeyDown += OnKeyDown;
    }

    public SpectrogramWindow(SpectrogramViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.Closed += (_, _) => Close();
        Closed += (_, _) => vm.Dispose();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm == null) return;
        // Axis labels sit on the black plot panel, so they are always light; the
        // theme text brush is only used when it reads on black (dark themes do).
        if (this.TryFindResource("SystemControlForegroundBaseHighBrush", out var brush) && brush is ISolidColorBrush solid
            && solid.Color.R + solid.Color.G + solid.Color.B > 3 * 128)
            _vm.AxisForeground = solid;
        else
            _vm.AxisForeground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        _ = _vm.RunAsync();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm?.CloseCommand.Execute(null);
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click on the dimmed backdrop closes, like the Settings modal.
        e.Handled = true;
        _vm?.CloseCommand.Execute(null);
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Stop the card's own clicks from reaching the backdrop handler above.
        e.Handled = true;
    }
}
