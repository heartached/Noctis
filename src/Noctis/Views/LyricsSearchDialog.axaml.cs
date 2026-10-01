using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsSearchDialog : Window
{
    public LyricsSearchDialog()
    {
        InitializeComponent();
    }

    public LyricsSearchDialog(LyricsSearchViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the header X does (and cancels a running search).
        if (e.Key == Key.Escape && DataContext is LyricsSearchViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
