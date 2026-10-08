using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MetadataFinderDialog : Window
{
    public MetadataFinderDialog()
    {
        InitializeComponent();
    }

    public MetadataFinderDialog(MetadataFinderViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
        Closed += (_, _) => vm.StopIdentify();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the header X does (Cancel: stops any running identify).
        if (e.Key == Key.Escape && DataContext is MetadataFinderViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
