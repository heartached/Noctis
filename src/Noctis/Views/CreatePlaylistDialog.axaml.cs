using Avalonia.Controls;
using Avalonia.Input;

namespace Noctis.Views;

public partial class CreatePlaylistDialog : Window
{
    public CreatePlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameTextBox.Focus();
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
