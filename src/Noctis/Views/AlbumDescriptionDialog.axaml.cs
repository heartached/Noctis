using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AlbumDescriptionDialog : Window
{
    public AlbumDescriptionDialog()
    {
        InitializeComponent();
    }

    public AlbumDescriptionDialog(AlbumDetailViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Block clicks on overlay — only X button closes the dialog
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Prevent clicks inside the card from closing the dialog
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(AlbumDetailViewModel vm)
    {
        var dialog = new AlbumDescriptionDialog(vm);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
