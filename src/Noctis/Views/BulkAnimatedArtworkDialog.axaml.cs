using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class BulkAnimatedArtworkDialog : Window
{
    public BulkAnimatedArtworkDialog()
    {
        InitializeComponent();
    }

    public BulkAnimatedArtworkDialog(BulkAnimatedArtworkViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; a running lookup keeps going.
        vm.Closed += (_, _) => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape hides the same way Close / Hide does.
        if (e.Key == Key.Escape && DataContext is BulkAnimatedArtworkViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
