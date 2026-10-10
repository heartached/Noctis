using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
        ChooseFileButton.Click += OnChooseFile;

        // Drop an export file anywhere on the dialog instead of hunting for it in the picker;
        // the start panel lights its ring while a file is over the window.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragHighlight(false));
        AddHandler(DragDrop.DropEvent, OnDrop);

        // If a playlist link is already on the clipboard, offer it (Deezer imports at once,
        // other services get the "export it like this" guidance).
        Opened += async (_, _) =>
        {
            try
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
                vm.OfferClipboardText(await clipboard.TryGetTextAsync());
            }
            catch
            {
                // Clipboard access can fail on some desktops; the dialog works without it.
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Cancel does.
        if (e.Key == Key.Escape && DataContext is PlaylistImportViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    private void SetDragHighlight(bool on) => DropZone.Classes.Set("drag", on);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var isFile = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = isFile ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragHighlight(isFile);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            SetDragHighlight(false);
            if (DataContext is not PlaylistImportViewModel vm) return;
            var file = (e.DataTransfer.TryGetFiles() ?? Enumerable.Empty<IStorageItem>()).OfType<IStorageFile>().FirstOrDefault();
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
                Title = Localization.Loc.T("Import.PickerTitle"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(Localization.Loc.T("Import.PickerFilter")) { Patterns = new[] { "*.csv", "*.json", "*.m3u", "*.m3u8" } },
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
