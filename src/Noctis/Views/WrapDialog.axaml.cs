using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class WrapDialog : Window
{
    public WrapDialog()
    {
        InitializeComponent();
    }

    public WrapDialog(WrapViewModel vm) : this()
    {
        DataContext = vm;
    }

    private WrapViewModel? Vm => DataContext as WrapViewModel;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.CurrentPng is not { } png)
            return;
        var status = await PngExportHelper.SavePngAsync(this, png, vm.SuggestedFileName);
        if (status != null)
            vm.ReportStatus(status);
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.CurrentPng is not { } png)
            return;
        var status = await PngExportHelper.CopyPngAsync(this, png, vm.SuggestedFileName);
        if (status != null)
            vm.ReportStatus(status);
    }

    public static async Task ShowAsync(WrapViewModel vm)
    {
        var dialog = new WrapDialog(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
