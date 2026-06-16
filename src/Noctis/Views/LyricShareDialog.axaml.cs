using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricShareDialog : Window
{
    public LyricShareDialog()
    {
        InitializeComponent();
    }

    public LyricShareDialog(LyricShareViewModel vm) : this()
    {
        DataContext = vm;
        vm.ScrollToLineRequested += OnScrollToLine;
        Closed += (_, _) => vm.Detach();
    }

    private LyricShareViewModel? Vm => DataContext as LyricShareViewModel;

    /// <summary>Brings the currently-playing lyric line into view during playback sync.</summary>
    private void OnScrollToLine(int index)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ItemsControl>("LineItems") is not { } list)
                return;
            if (index < 0 || index >= list.ItemCount)
                return;
            list.ContainerFromIndex(index)?.BringIntoView();
        });
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click outside the card closes the dialog.
        Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Prevent clicks inside the card from reaching the overlay.
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

    public static async Task ShowAsync(LyricShareViewModel vm)
    {
        var dialog = new LyricShareDialog(vm);

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
