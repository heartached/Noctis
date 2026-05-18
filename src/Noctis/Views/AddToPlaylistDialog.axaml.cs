using Avalonia.Controls;
using Avalonia.Input;

namespace Noctis.Views;

public partial class AddToPlaylistDialog : Window
{
    public AddToPlaylistDialog()
    {
        InitializeComponent();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}
