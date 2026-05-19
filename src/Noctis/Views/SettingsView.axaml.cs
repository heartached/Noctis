using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.ComponentModel;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _trackedViewModel;

    public SettingsView()
    {
        InitializeComponent();

        // Wire up the Add Folder button to open a native folder picker
        AddFolderButton.Click += OnAddFolderClicked;
        DataContextChanged += OnSettingsDataContextChanged;
    }

    private void OnSettingsDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;

        _trackedViewModel = DataContext as SettingsViewModel;

        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.MediaFoldersScrollRequest))
            ScrollToMediaFoldersSection();
    }

    private void ScrollToMediaFoldersSection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var point = MediaFoldersSectionAnchor.TranslatePoint(new Point(0, 0), SettingsScrollViewer);
            if (point is not { } relativePoint)
                return;

            var maxY = Math.Max(0, SettingsScrollViewer.Extent.Height - SettingsScrollViewer.Viewport.Height);
            var targetY = Math.Clamp(SettingsScrollViewer.Offset.Y + relativePoint.Y - 12, 0, maxY);
            SettingsScrollViewer.Offset = new Vector(SettingsScrollViewer.Offset.X, targetY);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnSettingsDataContextChanged(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_trackedViewModel != null)
        {
            _trackedViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            _trackedViewModel = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnPickAvatarClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose Profile Picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });

            if (files.Count == 0) return;

            var sourcePath = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
                return;

            // Copy the picked image into %APPDATA%\Noctis\profile so the avatar survives the
            // source being moved or deleted later.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = System.IO.Path.Combine(appData, "Noctis", "profile");
            System.IO.Directory.CreateDirectory(dir);
            var ext = System.IO.Path.GetExtension(sourcePath);
            var target = System.IO.Path.Combine(dir, "avatar" + ext);

            // Remove stale avatars with a different extension so only one file is kept.
            foreach (var existing in System.IO.Directory.EnumerateFiles(dir, "avatar.*"))
            {
                if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { System.IO.File.Delete(existing); } catch { }
                }
            }

            System.IO.File.Copy(sourcePath, target, overwrite: true);
            vm.ProfileAvatarPath = target;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Avatar pick failed: {ex.Message}");
        }
    }

    private async void OnAddFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            // Get the top-level window for the dialog
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Music Folder",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var path = folders[0].Path.LocalPath;
                await vm.AddFolderPath(path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to add folder: {ex.Message}");
        }
    }

}
