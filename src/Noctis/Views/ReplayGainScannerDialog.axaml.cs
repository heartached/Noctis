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
}
