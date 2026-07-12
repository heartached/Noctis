using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class PlaylistImportDialog : Window
{
    public PlaylistImportDialog()
    {
        InitializeComponent();
    }

    public PlaylistImportDialog(PlaylistImportViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
        ChooseFileButton.Click += OnChooseFile;
    }

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistImportViewModel vm) return;

            var topLevel = GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a playlist export",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Playlist exports") { Patterns = new[] { "*.csv", "*.json", "*.m3u", "*.m3u8" } },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count == 0) return;
            var path = files[0].Path.LocalPath;
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                await vm.LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistImportDialog] File pick failed: {ex.Message}");
        }
    }
}
