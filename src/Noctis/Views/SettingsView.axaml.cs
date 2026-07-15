using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SettingsView : UserControl
{
    private const double PreampThumbSize = 14;
    private const double PreampDefault = 0.0;
    private const double CrossfadeDurationDefault = 6.0; // matches the Settings reset value

    private SettingsViewModel? _trackedViewModel;
    private readonly TranslateTransform _preampThumbTransform = new();
    private bool _isPreampDragging;
    private DateTime _lastPreampPressAt = DateTime.MinValue;
    private Point _lastPreampPressPosition;

    public SettingsView()
    {
        InitializeComponent();

        // Wire up the Add Folder button to open a native folder picker
        AddFolderButton.Click += OnAddFolderClicked;
        DataContextChanged += OnSettingsDataContextChanged;

        // Pre-amp pill slider: the visible track/fill/thumb are drawn on a Canvas and
        // positioned from code-behind. The real Slider is transparent and only handles
        // pointer input (drag + double-press-to-reset), mirroring the metadata volume slider.
        PreampThumb.RenderTransform = _preampThumbTransform;
        PreampSlider.AddHandler(InputElement.PointerPressedEvent, OnPreampPointerPressed, RoutingStrategies.Tunnel);
        PreampSlider.AddHandler(InputElement.PointerMovedEvent, OnPreampPointerMoved, RoutingStrategies.Tunnel);
        PreampSlider.AddHandler(InputElement.PointerReleasedEvent, OnPreampPointerReleased, RoutingStrategies.Tunnel);
        PreampSlider.PointerCaptureLost += OnPreampCaptureLost;
        PreampSlider.PropertyChanged += OnPreampSliderPropertyChanged;
        PreampSlider.SizeChanged += (_, _) => UpdatePreampVisual();
        DispatcherTimer.RunOnce(UpdatePreampVisual, TimeSpan.FromMilliseconds(10));

        // Drop focus from the ListenBrainz token box when the user clicks anywhere
        // outside it, same behaviour as the top-bar search box. Tunnel so it runs
        // before inner controls handle the press.
        AddHandler(PointerPressedEvent, OnSettingsPointerPressed, RoutingStrategies.Tunnel);

        // The EQ preset combo lives inside SettingsScrollViewer; its momentum-scroll
        // behavior would otherwise consume wheel events routed through it, including
        // events over the open dropdown. Suspend it while the dropdown is open so the
        // wheel reaches the popup's own ScrollViewer (same fix as the genre combo).
        if (this.FindControl<ComboBox>("EqPresetCombo") is { } eqCombo)
        {
            eqCombo.DropDownOpened += OnEqPresetDropDownOpened;
            eqCombo.DropDownClosed += OnEqPresetDropDownClosed;
        }
    }

    // Enter in the ffmpeg path box re-probes the path so the user gets an explicit
    // "validate now" affordance after pasting. The Text binding already updates the
    // status live on each edit; this is a deliberate confirmation step.
    private void OnFfmpegPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && DataContext is SettingsViewModel vm)
        {
            vm.RefreshFfmpegStatus();
            e.Handled = true;
        }
    }

    // Enter in the profile name box commits the edit and drops focus, so the caret/edit
    // affordance disappears (same defocus target used when clicking outside the token box).
    private void OnProfileNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            SettingsScrollViewer?.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }
    }

    private void OnEqPresetDropDownOpened(object? sender, EventArgs e)
    {
        if (SettingsScrollViewer is not null)
            MomentumScrollBehavior.SetIsEnabled(SettingsScrollViewer, false);
    }

    private void OnEqPresetDropDownClosed(object? sender, EventArgs e)
    {
        if (SettingsScrollViewer is not null)
            MomentumScrollBehavior.SetIsEnabled(SettingsScrollViewer, true);
    }

    private void OnSettingsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tokenBox = this.FindControl<TextBox>("ListenBrainzTokenBox");
        if (tokenBox is not { IsFocused: true })
            return;

        bool insideBox = false;
        if (e.Source is Visual clickSource)
        {
            Visual? v = clickSource;
            while (v != null)
            {
                if (ReferenceEquals(v, tokenBox)) { insideBox = true; break; }
                v = v.GetVisualParent();
            }
        }

        if (!insideBox)
            Dispatcher.UIThread.Post(() => SettingsScrollViewer.Focus(NavigationMethod.Pointer),
                DispatcherPriority.Background);
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
        switch (e.PropertyName)
        {
            // The media-folders card is the first card on the Library tab (the view model
            // already switched tabs), so landing there is just a scroll to the top.
            case nameof(SettingsViewModel.MediaFoldersScrollRequest):
            case nameof(SettingsViewModel.SelectedSettingsTab):
                ScrollToTop();
                break;

            // Version-manager download started: bring the progress bar + Cancel
            // button into view (the release list can push them off-screen).
            case nameof(SettingsViewModel.IsDevDownloading):
                if (_trackedViewModel?.IsDevDownloading == true)
                    Dispatcher.UIThread.Post(
                        () => DevDownloadPanel.BringIntoView(),
                        DispatcherPriority.Loaded);
                break;
        }
    }

    private void ScrollToTop()
    {
        Dispatcher.UIThread.Post(
            () => SettingsScrollViewer.Offset = new Vector(SettingsScrollViewer.Offset.X, 0),
            DispatcherPriority.Loaded);
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

            // Copy the picked image into the data root's profile dir so the avatar
            // survives the source being moved or deleted later.
            var dir = System.IO.Path.Combine(Helpers.AppPaths.DataRoot, "profile");
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

    private void OnPreampSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.ReplayGainPreampDb = PreampDefault;
    }

    // Double-tapping the crossfade duration slider restores the default duration
    // (same affordance as the ReplayGain pre-amp slider).
    private void OnCrossfadeSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CrossfadeDuration = CrossfadeDurationDefault;
            e.Handled = true;
        }
    }

    // Double-tapping an EQ gain slider resets that band to 0 dB (same affordance
    // as the ReplayGain pre-amp slider).
    private void OnEqGainSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Slider { DataContext: EqBandViewModel band })
        {
            band.GainDb = 0;
            e.Handled = true;
        }
    }

    private void OnPreampSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty ||
            e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdatePreampVisual();
        }
    }

    private void UpdatePreampVisual()
    {
        if (PreampSlider == null ||
            PreampTrackBackground == null ||
            PreampTrackFill == null ||
            PreampThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            PreampSlider,
            PreampTrackBackground,
            PreampTrackFill,
            PreampThumb,
            _preampThumbTransform,
            PreampThumbSize,
            enabledBackgroundOpacity: 0.4,
            disabledBackgroundOpacity: 0.2);
    }

    private void OnPreampPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(slider);
        if (IsPreampDoublePress(position))
        {
            _isPreampDragging = false;
            _lastPreampPressAt = DateTime.MinValue;
            e.Pointer.Capture(null);
            if (DataContext is SettingsViewModel vm)
                vm.ReplayGainPreampDb = PreampDefault;
            e.Handled = true;
            return;
        }

        _lastPreampPressAt = DateTime.UtcNow;
        _lastPreampPressPosition = position;
        _isPreampDragging = true;
        e.Pointer.Capture(slider);
        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, position, PreampThumbSize);
        e.Handled = true;
    }

    private void OnPreampPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPreampDragging) return;
        if (sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, e.GetPosition(slider), PreampThumbSize);
        e.Handled = true;
    }

    private void OnPreampPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPreampDragging) return;

        _isPreampDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPreampCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPreampDragging = false;
    }

    private bool IsPreampDoublePress(Point position)
    {
        var elapsed = DateTime.UtcNow - _lastPreampPressAt;
        if (elapsed > TimeSpan.FromMilliseconds(400))
            return false;

        var dx = position.X - _lastPreampPressPosition.X;
        var dy = position.Y - _lastPreampPressPosition.Y;
        return dx * dx + dy * dy <= 36;
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
