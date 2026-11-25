using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>GitHub #267: Get Genres from Discogs.</summary>
public partial class BulkDiscogsGenresDialog : Window
{
    public BulkDiscogsGenresDialog()
    {
        InitializeComponent();
    }

    public BulkDiscogsGenresDialog(BulkDiscogsGenresViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close.
        vm.Closed += (_, _) => Close();
        Closed += (_, _) => vm.OnDialogClosed();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes (or hides a running library lookup) like the Close / Hide button.
        if (e.Key == Key.Escape && DataContext is BulkDiscogsGenresViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
