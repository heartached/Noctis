using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Helpers;

namespace Noctis.Views;

public partial class ConfirmationDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    public ConfirmationDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task<bool> ShowAsync(string message)
    {
        var dialog = new ConfirmationDialog(message);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            return false;
        }

        return dialog.Confirmed;
    }
}
