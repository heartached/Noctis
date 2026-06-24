using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Compact always-on-top player window (artwork, track info, transport, progress).
/// Opened/closed by clicking the cover art in the bottom player bar; closing it
/// restores the main window (handled by <see cref="MainWindow.ToggleMiniPlayer"/>).
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const int CloseAnimationMs = 170;
    private bool _closeAnimationDone;

    // Matches the slowest volume-popout transition (0.16s) so it stays mapped long
    // enough for the fade-out + slide to finish before IsVisible flips to false.
    private static readonly TimeSpan VolumePopoutExitDuration = TimeSpan.FromMilliseconds(180);
    private Avalonia.Threading.DispatcherTimer? _volumeHideTimer;
    private bool _volumeOpen;

    private const double MiniVolumeThumbSize = 14;
    private readonly TranslateTransform _miniVolumeThumbTransform = new();
    private bool _isMiniVolumeDragging;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        // Seek commits follow the same BeginSeek/EndSeek protocol as the playback bar
        // so drags update the UI live and send a single debounced seek on release.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.PointerCaptureLost += (_, _) => (DataContext as PlayerViewModel)?.EndSeek();

        // Volume pill: custom pointer handling drives the value from the pointer
        // position (vertical) like the playback bar's pill slider, and the track
        // fill + thumb are positioned by PillSliderVisualHelper. Live drags commit
        // once on release so VLC isn't hammered with rapid changes (anti-crackle).
        MiniVolumeThumb.RenderTransform = _miniVolumeThumbTransform;
        MiniVolumeSlider.AddHandler(PointerPressedEvent, OnMiniVolumePressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        MiniVolumeSlider.AddHandler(PointerMovedEvent, OnMiniVolumeMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        MiniVolumeSlider.AddHandler(PointerReleasedEvent, OnMiniVolumeReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        MiniVolumeSlider.PointerCaptureLost += OnMiniVolumeCaptureLost;
        MiniVolumeSlider.PropertyChanged += OnMiniVolumePropertyChanged;
        MiniVolumeSlider.SizeChanged += (_, _) => UpdateMiniVolumeVisual();

        // The root border starts faded/scaled-down in XAML; flipping the values
        // once the window is shown lets its transitions play the open animation.
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        };
    }

    // Any close path (close button, toggling from the player bar) first plays the
    // reverse fade/scale, then really closes once the animation has run.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeAnimationDone)
        {
            e.Cancel = true;
            _closeAnimationDone = true;
            RootBorder.Opacity = 0;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.92)");
            Avalonia.Threading.DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(CloseAnimationMs));
        }
        base.OnClosing(e);
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e) =>
        (DataContext as PlayerViewModel)?.BeginSeek();

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        (DataContext as PlayerViewModel)?.EndSeek();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Click the volume icon to toggle the slider; clicking again closes it.
    private void OnVolumeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_volumeOpen)
            CloseVolumePopout();
        else
            OpenVolumePopout();
    }

    // Once open, leaving the icon+slider area dismisses it. The StackPanel's transparent
    // background keeps the small gap between the icon and slider "hovered" so crossing it
    // doesn't fire a premature exit; mid-drag is ignored (the captured pointer can drift off).
    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_volumeOpen || _isMiniVolumeDragging)
            return;
        CloseVolumePopout();
    }

    // Open/close mirror the playback bar's flyout: fade + slide-down on open, reverse
    // on close, and unmap only once the exit transition has finished so it doesn't snap.
    private void OpenVolumePopout()
    {
        _volumeOpen = true;
        _volumeHideTimer?.Stop();
        _volumeHideTimer = null;

        VolumePopout.Opacity = 0;
        SetVolumePopoutOffset(-6);
        VolumePopout.IsVisible = true;

        // Apply the shown target on the next render tick so the transition animates
        // from the hidden state instead of snapping; the slider also needs a laid-out
        // size before its pill visual can be positioned.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            VolumePopout.Opacity = 1;
            SetVolumePopoutOffset(0);
            UpdateMiniVolumeVisual();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void CloseVolumePopout()
    {
        _volumeOpen = false;

        VolumePopout.Opacity = 0;
        SetVolumePopoutOffset(-6);

        _volumeHideTimer?.Stop();
        _volumeHideTimer = new Avalonia.Threading.DispatcherTimer { Interval = VolumePopoutExitDuration };
        _volumeHideTimer.Tick += (_, _) =>
        {
            _volumeHideTimer?.Stop();
            _volumeHideTimer = null;
            VolumePopout.IsVisible = false;
        };
        _volumeHideTimer.Start();
    }

    private void SetVolumePopoutOffset(double y)
    {
        if (VolumePopout.RenderTransform is TranslateTransform t)
            t.Y = y;
    }

    // ── Volume pill slider (vertical) ────────────────────────
    private void OnMiniVolumePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isMiniVolumeDragging = true;
        e.Pointer.Capture(slider);
        slider.Value = PillSliderVisualHelper.GetValueFromPointerVertical(slider, e.GetPosition(slider), MiniVolumeThumbSize);
        e.Handled = true;
    }

    private void OnMiniVolumeMoved(object? sender, PointerEventArgs e)
    {
        if (!_isMiniVolumeDragging || sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointerVertical(slider, e.GetPosition(slider), MiniVolumeThumbSize);
        e.Handled = true;
    }

    private void OnMiniVolumeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isMiniVolumeDragging) return;
        _isMiniVolumeDragging = false;
        e.Pointer.Capture(null);
        (DataContext as PlayerViewModel)?.CommitVolume();
        e.Handled = true;

        // PointerExited is suppressed while the pointer is captured, so if the drag
        // ended outside the volume area, dismiss the slider here.
        var p = e.GetPosition(VolumeArea);
        if (_volumeOpen &&
            (p.X < 0 || p.Y < 0 || p.X > VolumeArea.Bounds.Width || p.Y > VolumeArea.Bounds.Height))
            CloseVolumePopout();
    }

    private void OnMiniVolumeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isMiniVolumeDragging) return;
        _isMiniVolumeDragging = false;
        (DataContext as PlayerViewModel)?.CommitVolume();
    }

    private void OnMiniVolumePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty || e.Property.Name is nameof(Bounds))
            UpdateMiniVolumeVisual();
    }

    private void UpdateMiniVolumeVisual()
    {
        if (MiniVolumeSlider == null ||
            MiniVolumeTrackBackground == null ||
            MiniVolumeTrackFill == null ||
            MiniVolumeThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisualVertical(
            MiniVolumeSlider,
            MiniVolumeTrackBackground,
            MiniVolumeTrackFill,
            MiniVolumeThumb,
            _miniVolumeThumbTransform,
            MiniVolumeThumbSize);
    }

    // The window has no title bar, so any press on the artwork (not on a control) drags it.
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Avalonia.Visual source)
        {
            foreach (var ancestor in source.GetSelfAndVisualAncestors())
            {
                if (ancestor is Button or Slider)
                    return;
            }
        }

        BeginMoveDrag(e);
    }
}
