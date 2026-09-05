using Avalonia.Controls;
using Avalonia.Interactivity;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class YouTubeDownloadDialog : Window
{
    public YouTubeDownloadDialog()
    {
        InitializeComponent();
    }

    public YouTubeDownloadDialog(YouTubeDownloadViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QueryBox.Focus();
    }
}
