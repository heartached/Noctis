using Avalonia.Controls;
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
        vm.Closed += (_, _) => Close();
    }
}
