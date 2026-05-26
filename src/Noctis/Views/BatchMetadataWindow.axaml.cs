using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class BatchMetadataWindow : Window
{
    public BatchMetadataWindow()
    {
        InitializeComponent();
    }

    public BatchMetadataWindow(BatchMetadataViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }
}
