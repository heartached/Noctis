using Avalonia.Controls;
using Avalonia.Input;
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

        // Drop an export file anywhere on the dialog instead of hunting for it in the picker.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // If a playlist link is already on the clipboard, offer it (Deezer imports at once,
        // other services get the "export it like this" guidance).
        Opened += async (_, _) =>
        {
            try
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
                vm.OfferClipboardText(await clipboard.GetTextAsync());
            }
            catch
            {
                // Clipboard access can fail on some desktops; the dialog works without it.
            }
        };
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistImportViewModel vm) return;
            var file = (e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>()).OfType<IStorageFile>().FirstOrDefault();
            var path = file?.TryGetLocalPath();
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                await vm.LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistImportDialog] Drop failed: {ex.Message}");
        }
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
