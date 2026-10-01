using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ContextSheet : UserControl
{
    public ContextSheet()
    {
        InitializeComponent();
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as ShellViewModel)?.CloseSheet();
        e.Handled = true;
    }
}
