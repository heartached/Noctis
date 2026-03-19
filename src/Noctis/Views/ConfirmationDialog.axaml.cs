using Avalonia;
using Avalonia.Controls;

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

    /// <summary>
    /// Shows a confirmation dialog and returns true if the user confirmed.
    /// </summary>
    public static async Task<bool> ShowAsync(string message)
    {
        var dialog = new ConfirmationDialog(message);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
        }
        else
        {
            return false;
        }

        return dialog.Confirmed;
    }
}
