using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class QuickPlaylistDialog : Window
{
    public QuickPlaylistDialog()
    {
        InitializeComponent();
    }

    public QuickPlaylistDialog(QuickPlaylistViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close.
        vm.Closed += (_, _) => Close();
        Opened += (_, _) => SearchBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Cancel does.
        if (e.Key == Key.Escape && DataContext is QuickPlaylistViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
