using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.ComponentModel;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _trackedViewModel;
    private double _customHue;
    private double _customSaturation = 1;
    private double _customValue = 1;

    public SettingsView()
    {
        InitializeComponent();

        // Wire up the Add Folder button to open a native folder picker
        AddFolderButton.Click += OnAddFolderClicked;
        DataContextChanged += OnSettingsDataContextChanged;
        CustomColorSpectrum.SizeChanged += (_, _) => UpdateCustomColorPickerVisuals();
        CustomColorHueTrack.SizeChanged += (_, _) => UpdateCustomColorPickerVisuals();
    }

    private void OnSettingsDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;

        _trackedViewModel = DataContext as SettingsViewModel;

        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;

        SyncCustomColorPickerFromViewModel();
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.MediaFoldersScrollRequest))
            ScrollToMediaFoldersSection();
        else if (e.PropertyName == nameof(SettingsViewModel.PickerColor) ||
                 e.PropertyName == nameof(SettingsViewModel.ActiveAccentHex))
            SyncCustomColorPickerFromViewModel();
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

    private void OnCustomColorSpectrumPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateSpectrumFromPointer(e);
        e.Pointer.Capture(CustomColorSpectrum);
        e.Handled = true;
    }

    private void OnCustomColorSpectrumPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(CustomColorSpectrum).Properties.IsLeftButtonPressed)
        {
            UpdateSpectrumFromPointer(e);
            e.Handled = true;
        }
        else
            e.Pointer.Capture(null);
    }

    private void OnCustomColorHuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateHueFromPointer(e);
        e.Pointer.Capture(CustomColorHueTrack);
        e.Handled = true;
    }

    private void OnCustomColorHuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(CustomColorHueTrack).Properties.IsLeftButtonPressed)
        {
            UpdateHueFromPointer(e);
            e.Handled = true;
        }
        else
            e.Pointer.Capture(null);
    }

    private void UpdateSpectrumFromPointer(PointerEventArgs e)
    {
        var bounds = CustomColorSpectrum.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var p = e.GetPosition(CustomColorSpectrum);
        _customSaturation = Math.Clamp(p.X / bounds.Width, 0, 1);
        _customValue = 1 - Math.Clamp(p.Y / bounds.Height, 0, 1);
        ApplyCustomPickerColor();
    }

    private void UpdateHueFromPointer(PointerEventArgs e)
    {
        var width = CustomColorHueTrack.Bounds.Width;
        if (width <= 0)
            return;

        var p = e.GetPosition(CustomColorHueTrack);
        _customHue = Math.Clamp(p.X / width, 0, 1) * 360;
        ApplyCustomPickerColor();
    }

    private void ApplyCustomPickerColor()
    {
        if (DataContext is SettingsViewModel vm)
            vm.PickerColor = FromHsv(_customHue, _customSaturation, _customValue);

        UpdateCustomColorPickerVisuals();
    }

    private void SyncCustomColorPickerFromViewModel()
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        ToHsv(vm.PickerColor, out var hue, out var saturation, out var value);
        if (saturation > 0.001)
            _customHue = hue;

        _customSaturation = saturation;
        _customValue = value;
        UpdateCustomColorPickerVisuals();
    }

    private void UpdateCustomColorPickerVisuals()
    {
        if (CustomColorSpectrum == null ||
            CustomColorHueTrack == null ||
            CustomColorHueWash == null ||
            CustomColorSpectrumThumb == null ||
            CustomColorHueThumb == null)
            return;

        var hueColor = FromHsv(_customHue, 1, 1);
        CustomColorHueWash.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(hueColor, 1),
            }
        };

        var spectrumWidth = CustomColorSpectrum.Bounds.Width;
        var spectrumHeight = CustomColorSpectrum.Bounds.Height;
        if (spectrumWidth > 0 && spectrumHeight > 0)
        {
            Canvas.SetLeft(CustomColorSpectrumThumb, (_customSaturation * spectrumWidth) - (CustomColorSpectrumThumb.Width / 2));
            Canvas.SetTop(CustomColorSpectrumThumb, ((1 - _customValue) * spectrumHeight) - (CustomColorSpectrumThumb.Height / 2));
        }

        var hueWidth = CustomColorHueTrack.Bounds.Width;
        if (hueWidth > 0)
        {
            Canvas.SetLeft(
                CustomColorHueThumb,
                Math.Clamp((_customHue / 360) * hueWidth - (CustomColorHueThumb.Width / 2), 0, hueWidth - CustomColorHueThumb.Width));
            Canvas.SetTop(CustomColorHueThumb, (CustomColorHueTrack.Bounds.Height - CustomColorHueThumb.Height) / 2);
        }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - chroma;

        var (r1, g1, b1) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        hue = delta switch
        {
            0 => 0,
            _ when max == r => 60 * (((g - b) / delta) % 6),
            _ when max == g => 60 * (((b - r) / delta) + 2),
            _ => 60 * (((r - g) / delta) + 4)
        };
        if (hue < 0)
            hue += 360;

        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }
}
