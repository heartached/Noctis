using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MetadataWindow : Window
{
    public MetadataWindow()
    {
        InitializeComponent();
    }

    public MetadataWindow(MetadataViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
