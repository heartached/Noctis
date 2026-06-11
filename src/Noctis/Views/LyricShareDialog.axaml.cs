using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
    }

    private LyricShareViewModel? Vm => DataContext as LyricShareViewModel;

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

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save lyric card",
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = "png",
            FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
        });
        if (file == null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
            vm.ReportStatus("Saved");
        }
        catch (Exception ex)
        {
            vm.ReportStatus($"Save failed: {ex.Message}");
        }
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.CurrentPng is not { } png || Clipboard is not { } clipboard)
            return;

        try
        {
            var transfer = new DataTransfer();
            // Raw PNG bytes under the platform "PNG" format — understood by
            // most image-aware apps (Discord, GIMP, Paint.NET, browsers).
            transfer.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("PNG"), png));

            // Also put a temp .png file on the clipboard so chat apps and
            // file managers can paste it as an attachment.
            var tempPath = Path.Combine(Path.GetTempPath(), vm.SuggestedFileName);
            await File.WriteAllBytesAsync(tempPath, png);
            var tempFile = await StorageProvider.TryGetFileFromPathAsync(tempPath);
            if (tempFile != null)
                transfer.Add(DataTransferItem.CreateFile(tempFile));

            await clipboard.SetDataAsync(transfer);
            vm.ReportStatus("Copied to clipboard");
        }
        catch (Exception ex)
        {
            vm.ReportStatus($"Copy failed: {ex.Message}");
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
