using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SendToFolderDialog : Window
{
    public SendToFolderDialog()
    {
        InitializeComponent();
    }

    public SendToFolderDialog(SendToFolderViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Send to folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && DataContext is SendToFolderViewModel vm)
                vm.Destination = folders[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SendToFolderDialog] Folder pick failed: {ex.Message}");
        }
    }
}
