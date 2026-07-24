using Avalonia.Controls;
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
        vm.Closed += (_, _) => Close();
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
