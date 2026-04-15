using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class PlaylistDescriptionDialog : Window
{
    public PlaylistDescriptionDialog()
    {
        InitializeComponent();
    }

    public PlaylistDescriptionDialog(PlaylistViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(PlaylistViewModel vm)
    {
        var dialog = new PlaylistDescriptionDialog(vm);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
