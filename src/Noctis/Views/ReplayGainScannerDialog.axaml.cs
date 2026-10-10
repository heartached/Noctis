using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ReplayGainScannerDialog : Window
{
    public ReplayGainScannerDialog()
    {
        InitializeComponent();
    }

    public ReplayGainScannerDialog(ReplayGainScannerViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is Cancel: it stops a running scan first, and closes when idle.
        if (e.Key == Key.Escape && DataContext is ReplayGainScannerViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReplayGainScannerViewModel vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Loc.T("ReplayGainScanner.AddFiles"),
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Audio")
                    {
                        Patterns = MetadataService.SupportedExtensions.Select(ext => "*" + ext).ToArray(),
                    },
                },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            await vm.AddFilesAsync(paths);
        }
        catch (Exception ex)
        {
            DebugLog.Write("ReplayGain", $"Add files failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels the scan when the window closes by any route.
    ///
    /// The only cancellation path used to be the Cancel button, so Alt+F4 (or an
    /// owner-driven close) left ScanAsync running on a thread pool thread with no visible
    /// UI and no way to stop it — still writing tags to the user's files, and liable to
    /// be killed mid-file.Save() when the process exited.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as ReplayGainScannerViewModel)?.CancelForClose();
        base.OnClosing(e);
    }
}
