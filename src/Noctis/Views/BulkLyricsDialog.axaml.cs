using Avalonia.Controls;
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
        vm.Closed += (_, _) => Close();
    }
}
