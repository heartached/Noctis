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
        vm.AnimatedFrameRendered += OnAnimatedFrameRendered;
        Closed += (_, _) =>
        {
            vm.AnimatedFrameRendered -= OnAnimatedFrameRendered;
            vm.Detach();
        };
    }

    /// <summary>The live-preview WriteableBitmap is mutated in place each frame; the
    /// Image doesn't know, so repaint it explicitly.</summary>
    private void OnAnimatedFrameRendered()
        => this.FindControl<Image>("AnimatedPreviewImage")?.InvalidateVisual();

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
        // async void: an escaped exception would crash the app.
        try
        {
            var vm = Vm;
            if (vm?.CurrentPng is not { } png)
                return;
            var status = await PngExportHelper.SavePngAsync(this, png, vm.SuggestedFileName);
            if (status != null)
                vm.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Save failed: {ex.Message}");
            Vm?.ReportStatus("Save failed.");
        }
    }

    private async void OnSaveVideoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            var vm = Vm;
            if (vm is null || !vm.CanExportVideo)
                return;

            var path = await MediaExportHelper.PickMp4PathAsync(this, vm.SuggestedVideoFileName);
            if (path is null)
                return; // cancelled

            var status = await vm.ExportClipAsync(path);
            vm.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Video export failed: {ex.Message}");
            Vm?.ReportStatus("Video export failed.");
        }
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
