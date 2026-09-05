using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsStudioDialog : Window
{
    public LyricsStudioDialog()
    {
        InitializeComponent();
    }

    public LyricsStudioDialog(LyricsStudioViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }
}
