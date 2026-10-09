using Avalonia.Controls;
using Avalonia.Input;
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
        // PillDialogHost turns this into the animated close (once, however many paths ask).
        vm.Closed += (_, _) => Close();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QueryBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Close does.
        if (e.Key == Key.Escape && DataContext is YouTubeDownloadViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
