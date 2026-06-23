using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class DuplicateFinderDialog : Window
{
    public DuplicateFinderDialog()
    {
        InitializeComponent();
    }

    public DuplicateFinderDialog(DuplicateFinderViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }
}
