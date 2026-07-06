using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AudioConverterDialog : Window
{
    public AudioConverterDialog()
    {
        InitializeComponent();
    }

    public AudioConverterDialog(AudioConverterViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
    }

    private async void OnBrowseOutputClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick output folder",
                AllowMultiple = false,
            });

            if (folders.Count > 0 && DataContext is AudioConverterViewModel vm)
                vm.OutputFolder = folders[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioConverterDialog] Folder pick failed: {ex.Message}");
        }
    }
}
