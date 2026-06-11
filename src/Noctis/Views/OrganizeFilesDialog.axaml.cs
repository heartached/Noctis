using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class OrganizeFilesDialog : Window
{
    public OrganizeFilesDialog()
    {
        InitializeComponent();
    }

    public OrganizeFilesDialog(OrganizeFilesViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }
}
