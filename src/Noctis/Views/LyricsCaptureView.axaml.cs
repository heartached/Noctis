using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsCaptureView : UserControl
{
    private LyricsCaptureViewModel? _subscribedVm;
    private int _lastScrolledIndex = -1;
    private DispatcherTimer? _scrollAnimTimer;
    private DispatcherTimer? _scrollRetryTimer;

    // Fades the top of the lyrics list so lines dissolve behind the centered cover art.
    private static readonly IBrush LyricsTopFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.20),
            new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 1.0),
        }
    };

    private const double VolumeThumbSize = 14;
    private readonly TranslateTransform _volumeThumbTransform = new();
    private bool _isVolumeDragging;

    public LyricsCaptureView()
    {
        InitializeComponent();

        CaptureVolumeThumb.RenderTransform = _volumeThumbTransform;
        CaptureVolumeSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeSliderPressed, RoutingStrategies.Tunnel);
        CaptureVolumeSlider.AddHandler(InputElement.PointerMovedEvent, OnVolumeSliderMoved, RoutingStrategies.Tunnel);
        CaptureVolumeSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeSliderReleased, RoutingStrategies.Tunnel);
        CaptureVolumeSlider.PointerCaptureLost += OnVolumeSliderCaptureLost;
        CaptureVolumeSlider.PropertyChanged += OnVolumeSliderPropertyChanged;
        CaptureVolumeSlider.SizeChanged += (_, _) => UpdateVolumeSliderVisual();
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Focus();
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnViewPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        if (DataContext is LyricsCaptureViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = vm;
        }
        UpdateLyricsFade();
    }

    // The top fade only applies in the centered-art layout, where lyrics scroll under the art.
    private void UpdateLyricsFade()
    {
        if (CaptureScrollViewer == null) return;
        CaptureScrollViewer.OpacityMask =
            _subscribedVm?.IsArtCentered == true ? LyricsTopFadeMask : null;
    }

    // When the overlay becomes visible, jump straight to the current line (no glide).
    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && _subscribedVm != null)
        {
            _lastScrolledIndex = -1;
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: false);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsCaptureViewModel.ActiveLineIndex) && _subscribedVm != null)
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: true);
        else if (e.PropertyName == nameof(LyricsCaptureViewModel.IsArtCentered))
            UpdateLyricsFade();
    }

    private void ScrollToActiveLine(int index, bool animate)
    {
        _scrollRetryTimer?.Stop();
        _scrollRetryTimer = null;

        if (index < 0)
        {
            _lastScrolledIndex = -1;
            return;
        }
        if (index == _lastScrolledIndex) return;

        CancelScrollAnimation();

        // Retry until the list is laid out: on first entry the ItemsControl may not have
        // measured its items yet, which would compute a wrong (displaced) scroll offset.
        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollRetryTimer = timer;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (TryScrollToLine(index, animate) || attempts > 40)
            {
                timer.Stop();
                if (_scrollRetryTimer == timer) _scrollRetryTimer = null;
            }
        };
        timer.Start();
    }

    /// <summary>Returns true once the layout is ready and the scroll has been applied.</summary>
    private bool TryScrollToLine(int index, bool animate)
    {
        try
        {
            if (CaptureLyricsItems == null || index >= CaptureLyricsItems.ItemCount) return false;
            if (CaptureScrollViewer == null) return false;

            var presenter = CaptureLyricsItems.GetVisualDescendants()
                .OfType<ItemsPresenter>()
                .FirstOrDefault();
            if (presenter == null) return false;

            if (presenter.GetVisualChildren().FirstOrDefault() is not Panel panel) return false;
            if (panel.Children.Count <= index) return false;

            var viewportHeight = CaptureScrollViewer.Viewport.Height;
            if (viewportHeight <= 0) return false;

            var targetChild = panel.Children[index];
            var childHeight = targetChild.Bounds.Height;
            if (childHeight <= 0) return false;

            var childBounds = targetChild.TransformToVisual(CaptureScrollViewer);
            if (childBounds == null) return false;

            var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
            var activeLineCenter = childTop + (childHeight / 2.0);
            var targetOffset = CaptureScrollViewer.Offset.Y + activeLineCenter - (viewportHeight / 2.0);
            targetOffset = Math.Max(0, targetOffset);

            var currentOffset = CaptureScrollViewer.Offset.Y;
            var diff = Math.Abs(targetOffset - currentOffset);
            _lastScrolledIndex = index;

            if (!animate || diff < 2)
            {
                CaptureScrollViewer.Offset = new Vector(0, targetOffset);
                return true;
            }

            AnimateScroll(currentOffset, targetOffset,
                (int)Math.Min(1050, Math.Max(650, diff * 0.85)));
            return true;
        }
        catch
        {
            return true;
        }
    }

    private void AnimateScroll(double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollAnimTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (CaptureScrollViewer == null)
            {
                CancelScrollAnimation();
                return;
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / durationMs);
            var eased = Easing.SmootherStep(t);
            CaptureScrollViewer.Offset = new Vector(0, from + (to - from) * eased);

            if (t >= 1.0)
            {
                CaptureScrollViewer.Offset = new Vector(0, to);
                CancelScrollAnimation();
            }
        };
        timer.Start();
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty || e.Property == IsEnabledProperty)
            UpdateVolumeSliderVisual();
    }

    private void OnVolumeSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;

        _isVolumeDragging = true;
        slider.Value = PillSliderVisualHelper.GetValueFromPointerVertical(
            slider, e.GetPosition(slider), VolumeThumbSize);
        e.Handled = true;
    }

    private void OnVolumeSliderMoved(object? sender, PointerEventArgs e)
    {
        if (!_isVolumeDragging || sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointerVertical(
            slider, e.GetPosition(slider), VolumeThumbSize);
        e.Handled = true;
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isVolumeDragging)
            _isVolumeDragging = false;

        if (DataContext is LyricsCaptureViewModel vm)
            vm.CommitVolume();
    }

    private void OnVolumeSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isVolumeDragging) return;

        _isVolumeDragging = false;
        if (DataContext is LyricsCaptureViewModel vm)
            vm.CommitVolume();
    }

    private void UpdateVolumeSliderVisual()
    {
        if (CaptureVolumeSlider == null ||
            CaptureVolumeTrackBackground == null ||
            CaptureVolumeTrackFill == null ||
            CaptureVolumeThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisualVertical(
            CaptureVolumeSlider,
            CaptureVolumeTrackBackground,
            CaptureVolumeTrackFill,
            CaptureVolumeThumb,
            _volumeThumbTransform,
            VolumeThumbSize);
    }

    private void CancelScrollAnimation()
    {
        _scrollAnimTimer?.Stop();
        _scrollAnimTimer = null;
    }

    // Seek on pointer press rather than Button.Click: a click only fires if press and
    // release land on the same control, so a stray move (or auto-scroll) drops the seek.
    private void OnLyricLinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is LyricLine line
            && DataContext is LyricsCaptureViewModel vm)
        {
            vm.SeekToLineCommand.Execute(line);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is LyricsCaptureViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
