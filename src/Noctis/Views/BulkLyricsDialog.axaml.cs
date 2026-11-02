using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class BulkLyricsDialog : Window
{
    public BulkLyricsDialog()
    {
        InitializeComponent();
    }

    public BulkLyricsDialog(BulkLyricsViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; a running fetch keeps going.
        vm.Closed += (_, _) => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes (or hides a running fetch) the same way the Close / Hide button does.
        if (e.Key == Key.Escape && DataContext is BulkLyricsViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
