using Avalonia;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.ViewModels;
using Avalonia.LogicalTree;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Views;

public partial class PlaybackBarView : UserControl
{
    public static readonly StyledProperty<bool> CompactWhenLyricsPageActiveProperty =
        AvaloniaProperty.Register<PlaybackBarView, bool>(
            nameof(CompactWhenLyricsPageActive),
            defaultValue: true);

    public bool CompactWhenLyricsPageActive
    {
        get => GetValue(CompactWhenLyricsPageActiveProperty);
        set => SetValue(CompactWhenLyricsPageActiveProperty, value);
    }

    private const double TrackTitleOverflowThreshold = 1.0;
    private const double TrackTitleScrollSpeed = 26.0;
    private const double TrackTitleBadgeSpacing = 6.0;
    private const double TrackTitleBadgeTrailingPadding = 8.0;
    private static readonly TimeSpan TrackTitleEdgePause = TimeSpan.FromMilliseconds(850);
    private readonly DispatcherTimer _trackTitleMarqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _trackTitleMarqueeClock = new();
    private PlayerViewModel? _observedPlayerViewModel;
    private double _trackTitleOverflow;
    private double _trackTitleOffset;
    private int _trackTitleDirection = -1;
    private double _trackTitlePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
    private bool _trackTitleUpdateScheduled;
    private bool _trackTitleResetPending;
    private const double SeekThumbSize = 12;
    private readonly TranslateTransform _seekThumbTransform = new();

    // Artist name marquee state (syncs with title marquee via same timer)
    private double _artistNameOverflow;
    private double _artistNameOffset;
    private int _artistNameDirection = -1;
    private double _artistNamePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
    private bool _artistNameUpdateScheduled;
    private bool _artistNameResetPending;

    // Seek slider drag state — only our code sets/clears this, preventing
    // stale Thumb state or stray pointer moves from triggering seeks.
    private bool _isSeekDragging;
    private bool _isVolumeDragging;
    private const double VolumeThumbSize = 12;
    private const double VolumeSliderVisualWidth = 84;
    private readonly TranslateTransform _volumeThumbTransform = new();

    public PlaybackBarView()
    {
        InitializeComponent();

        // Right-click on track info area opens the options flyout
        TrackInfoPanel.AddHandler(PointerReleasedEvent, OnTrackInfoRightClick, RoutingStrategies.Bubble);

        // Seek slider: use Tunnel routing so our handlers fire BEFORE the
        // Slider's internal Thumb/Track handlers.  When we mark Handled the
        // Thumb never starts its own drag → no capture conflict, no stuck state.
        SeekSlider.AddHandler(InputElement.PointerPressedEvent, OnSeekStart, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(InputElement.PointerMovedEvent, OnSeekMove, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(InputElement.PointerReleasedEvent, OnSeekEnd, RoutingStrategies.Tunnel);
        SeekSlider.PointerCaptureLost += OnSeekCaptureLost;
        SeekThumb.RenderTransform = _seekThumbTransform;
        SeekSlider.PropertyChanged += OnSeekSliderPropertyChanged;
        SeekSlider.SizeChanged += (_, _) => UpdateSeekSliderVisual();
        DispatcherTimer.RunOnce(UpdateSeekSliderVisual, TimeSpan.FromMilliseconds(10));

        // Handle volume slider interaction to show/hide percentage badge
        VolumeSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeSliderPressed, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(InputElement.PointerMovedEvent, OnVolumeSliderMoved, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeSliderReleased, RoutingStrategies.Tunnel);
        VolumeSlider.PointerCaptureLost += OnVolumeSliderCaptureLost;
        VolumeThumb.RenderTransform = _volumeThumbTransform;

        // Track volume changes to update percentage badge position
        VolumeSlider.PropertyChanged += OnVolumeSliderPropertyChanged;
        VolumeSlider.SizeChanged += (_, _) => UpdateVolumeSliderVisual();

        PropertyChanged += OnPlaybackBarPropertyChanged;
        TrackTitleTextBlock.PropertyChanged += OnTrackTitleTextBlockPropertyChanged;
        TrackTitleViewport.PropertyChanged += OnTrackTitleViewportPropertyChanged;
        ArtistNameTextBlock.PropertyChanged += OnArtistNameTextBlockPropertyChanged;
        ArtistNameViewport.PropertyChanged += OnArtistNameViewportPropertyChanged;
        _trackTitleMarqueeTimer.Tick += OnTrackTitleMarqueeTick;
        AttachedToVisualTree += OnPlaybackBarAttachedToVisualTree;
        DetachedFromVisualTree += OnPlaybackBarDetachedFromVisualTree;
        DataContextChanged += OnPlaybackBarDataContextChanged;

    }

    private void OnPlaybackBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == CompactWhenLyricsPageActiveProperty)
            UpdateIslandWidth();

        // The main-window bar stays mounted and IsVisible while the fullscreen lyrics page
        // is up — only its Opacity goes to 0 — so the 16 ms marquee timer went on mutating
        // TranslateTransform.X 60 times a second on a fully transparent control, for the
        // whole time the (already GPU-heavy) lyrics page was displayed.
        if (e.Property == OpacityProperty)
        {
            if (Opacity <= 0) StopTrackTitleMarqueeTimer();
            else ScheduleTrackTitleMarqueeUpdate();
        }
    }

    private void OnPlaybackBarAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
        DispatcherTimer.RunOnce(RefreshTrackInfoLayout, TimeSpan.FromMilliseconds(10));
        UpdateIslandWidth();
    }

    private void OnPlaybackBarDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Safety: ensure seek drag state is fully cleared on detach
        if (_isSeekDragging)
        {
            _isSeekDragging = false;
            if (DataContext is PlayerViewModel vm)
                vm.EndSeek();
        }

        StopTrackTitleMarqueeTimer();
    }

    private void OnPlaybackBarDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedPlayerViewModel != null)
            _observedPlayerViewModel.PropertyChanged -= OnObservedPlayerViewModelPropertyChanged;

        _observedPlayerViewModel = DataContext as PlayerViewModel;

        if (_observedPlayerViewModel != null)
            _observedPlayerViewModel.PropertyChanged += OnObservedPlayerViewModelPropertyChanged;

        // The width depends on the observed view model, so it can only be resolved once
        // the DataContext lands. If that happens after attach the pill would otherwise sit
        // at the base width until the next IsLyricsPageActive change — which, entering the
        // lyrics page, has already fired.
        UpdateIslandWidth();
        ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
        DispatcherTimer.RunOnce(RefreshTrackInfoLayout, TimeSpan.FromMilliseconds(10));
    }

    private void OnObservedPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) ||
            e.PropertyName == nameof(PlayerViewModel.TrackTitleMarqueeEnabled))
        {
            ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        }

        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) ||
            e.PropertyName == nameof(PlayerViewModel.ArtistMarqueeEnabled))
        {
            ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
        }

        if (e.PropertyName == nameof(PlayerViewModel.IsLyricsPageActive))
        {
            UpdateIslandWidth();
        }

        if (e.PropertyName == nameof(PlayerViewModel.State))
        {
            ScheduleTrackTitleMarqueeUpdate();
            ScheduleArtistNameMarqueeUpdate();
        }
    }

    private void OnTrackTitleTextBlockPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
            return;
        }

        if (e.Property == Visual.BoundsProperty)
            ScheduleTrackTitleMarqueeUpdate();
    }

    private void OnTrackTitleViewportPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
            ScheduleTrackTitleMarqueeUpdate();
    }

    private void ScheduleTrackTitleMarqueeUpdate(bool resetAnimation = false)
    {
        if (resetAnimation)
            _trackTitleResetPending = true;

        if (_trackTitleUpdateScheduled)
            return;

        _trackTitleUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _trackTitleUpdateScheduled = false;
            var shouldReset = _trackTitleResetPending;
            _trackTitleResetPending = false;
            UpdateTrackTitleMarquee(shouldReset);
        }, DispatcherPriority.Render);
    }

    private void UpdateTrackTitleMarquee(bool resetAnimation)
    {
        if (DataContext is not PlayerViewModel vm || vm.CurrentTrack == null)
        {
            SetTrackTitleWidth(double.NaN);
            ResetTrackTitleMarquee();
            return;
        }

        var viewportWidth = TrackTitleViewport.Bounds.Width;
        if (viewportWidth <= 0)
            return;

        var textWidth = MeasureTrackTitleTextWidth();
        if (textWidth <= 0)
            return;

        var measuredOverflow = Math.Max(0, textWidth - viewportWidth);
        var hasOverflow = measuredOverflow > TrackTitleOverflowThreshold;
        _trackTitleOverflow = hasOverflow && ExplicitBadge.IsVisible
            ? measuredOverflow + TrackTitleBadgeTrailingPadding
            : measuredOverflow;
        var shouldAnimate = vm.TrackTitleMarqueeEnabled && hasOverflow;
        if (!shouldAnimate)
        {
            ApplyTrackTitleStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetTrackTitleWidth(double.NaN);

        if (resetAnimation || _trackTitleOffset < -_trackTitleOverflow || _trackTitleOffset > 0)
        {
            _trackTitleDirection = -1;
            _trackTitlePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
            SetTrackTitleOffset(0);
        }

        switch (vm.State)
        {
            case PlaybackState.Playing:
                StartTrackTitleMarqueeTimer();
                break;
            case PlaybackState.Paused:
                StopTrackTitleMarqueeTimer();
                break;
            default:
                ResetTrackTitleMarquee();
                break;
        }
    }

    private void StartTrackTitleMarqueeTimer()
    {
        // Opacity 0 means the bar is mounted but hidden behind the lyrics page; nothing
        // it animates can be seen, so a track change or a play/pause there must not
        // restart the timer either.
        if (_trackTitleMarqueeTimer.IsEnabled || VisualRoot == null || Opacity <= 0)
            return;

        _trackTitleMarqueeClock.Restart();
        _trackTitleMarqueeTimer.Start();
    }

    private void StopTrackTitleMarqueeTimer()
    {
        if (!_trackTitleMarqueeTimer.IsEnabled)
            return;

        _trackTitleMarqueeTimer.Stop();
        _trackTitleMarqueeClock.Reset();
    }

    private void ResetTrackTitleMarquee()
    {
        StopTrackTitleMarqueeTimer();
        _trackTitleOverflow = 0;
        _trackTitleDirection = -1;
        _trackTitlePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
        SetTrackTitleOffset(0);
    }

    private void ApplyTrackTitleStaticPresentation(bool constrainToViewport, double viewportWidth)
    {
        ResetTrackTitleMarquee();
        SetTrackTitleWidth(constrainToViewport ? viewportWidth : double.NaN);
    }

    private double MeasureTrackTitleTextWidth()
    {
        var text = TrackTitleTextBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            TrackTitleTextBlock.FlowDirection,
            new Typeface(
                TrackTitleTextBlock.FontFamily,
                TrackTitleTextBlock.FontStyle,
                TrackTitleTextBlock.FontWeight,
                TrackTitleTextBlock.FontStretch),
            TrackTitleTextBlock.FontSize,
            Brushes.Transparent);

        var width = formattedText.WidthIncludingTrailingWhitespace;

        // Include explicit badge width + spacing when visible
        if (ExplicitBadge.IsVisible)
            width += TrackTitleBadgeSpacing + GetExplicitBadgeWidth();

        return width;
    }

    private void SetTrackTitleWidth(double width)
    {
        // When constraining for static truncation, reserve space for the badge
        if (!double.IsNaN(width) && ExplicitBadge.IsVisible)
            width = Math.Max(0, width - TrackTitleBadgeSpacing - GetExplicitBadgeWidth());

        var currentWidth = TrackTitleTextBlock.Width;
        var widthsMatch = (double.IsNaN(currentWidth) && double.IsNaN(width)) ||
                          (!double.IsNaN(currentWidth) && !double.IsNaN(width) && Math.Abs(currentWidth - width) < 0.5);
        if (!widthsMatch)
            TrackTitleTextBlock.Width = width;
    }

    private double GetExplicitBadgeWidth()
    {
        if (ExplicitBadge.Bounds.Width > 0)
            return ExplicitBadge.Bounds.Width;

        return ExplicitBadge.DesiredSize.Width;
    }

    private void OnTrackTitleMarqueeTick(object? sender, EventArgs e)
    {
        if (DataContext is not PlayerViewModel { State: PlaybackState.Playing, CurrentTrack: not null } vm)
        {
            StopTrackTitleMarqueeTimer();
            return;
        }

        var elapsedMs = _trackTitleMarqueeClock.Elapsed.TotalMilliseconds;
        _trackTitleMarqueeClock.Restart();
        if (elapsedMs <= 0)
            return;

        var titleActive = vm.TrackTitleMarqueeEnabled && _trackTitleOverflow > TrackTitleOverflowThreshold;
        var artistActive = vm.ArtistMarqueeEnabled && _artistNameOverflow > TrackTitleOverflowThreshold;

        if (!titleActive && !artistActive)
        {
            StopTrackTitleMarqueeTimer();
            if (!titleActive) ResetTrackTitleMarquee();
            if (!artistActive) ResetArtistNameMarquee();
            return;
        }

        // Tick title marquee
        if (titleActive)
            TickMarquee(elapsedMs, ref _trackTitleOffset, ref _trackTitleDirection,
                ref _trackTitlePauseRemainingMs, _trackTitleOverflow, SetTrackTitleOffset);

        // Tick artist marquee (same speed, independent phase)
        if (artistActive)
            TickMarquee(elapsedMs, ref _artistNameOffset, ref _artistNameDirection,
                ref _artistNamePauseRemainingMs, _artistNameOverflow, SetArtistNameOffset);
    }

    private void TickMarquee(double elapsedMs, ref double offset, ref int direction,
        ref double pauseRemainingMs, double overflow, Action<double> setOffset)
    {
        if (pauseRemainingMs > 0)
        {
            pauseRemainingMs = Math.Max(0, pauseRemainingMs - elapsedMs);
            return;
        }

        var nextOffset = offset + (direction * TrackTitleScrollSpeed * elapsedMs / 1000.0);
        if (direction < 0 && nextOffset <= -overflow)
        {
            nextOffset = -overflow;
            direction = 1;
            pauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
        }
        else if (direction > 0 && nextOffset >= 0)
        {
            nextOffset = 0;
            direction = -1;
            pauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
        }

        setOffset(nextOffset);
    }

    private void SetTrackTitleOffset(double offset)
    {
        _trackTitleOffset = offset;

        if (TrackTitleContent.RenderTransform is TranslateTransform transform)
            transform.X = offset;
    }

    // ── Artist name marquee (mirrors title marquee, synced via same timer) ──

    private void OnArtistNameTextBlockPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
            return;
        }

        if (e.Property == Visual.BoundsProperty)
            ScheduleArtistNameMarqueeUpdate();
    }

    private void OnArtistNameViewportPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
            ScheduleArtistNameMarqueeUpdate();
    }

    private void ScheduleArtistNameMarqueeUpdate(bool resetAnimation = false)
    {
        if (resetAnimation)
            _artistNameResetPending = true;

        if (_artistNameUpdateScheduled)
            return;

        _artistNameUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _artistNameUpdateScheduled = false;
            var shouldReset = _artistNameResetPending;
            _artistNameResetPending = false;
            UpdateArtistNameMarquee(shouldReset);
        }, DispatcherPriority.Render);
    }

    private void UpdateArtistNameMarquee(bool resetAnimation)
    {
        if (DataContext is not PlayerViewModel vm || vm.CurrentTrack == null)
        {
            SetArtistNameWidth(double.NaN);
            ResetArtistNameMarquee();
            return;
        }

        var viewportWidth = ArtistNameViewport.Bounds.Width;
        if (viewportWidth <= 0)
            return;

        var textWidth = MeasureArtistNameTextWidth();
        if (textWidth <= 0)
            return;

        _artistNameOverflow = Math.Max(0, textWidth - viewportWidth);
        var hasOverflow = _artistNameOverflow > TrackTitleOverflowThreshold;
        var shouldAnimate = vm.ArtistMarqueeEnabled && hasOverflow;
        if (!shouldAnimate)
        {
            ApplyArtistNameStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetArtistNameWidth(double.NaN);

        if (resetAnimation || _artistNameOffset < -_artistNameOverflow || _artistNameOffset > 0)
        {
            _artistNameDirection = -1;
            _artistNamePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
            SetArtistNameOffset(0);
        }

        switch (vm.State)
        {
            case PlaybackState.Playing:
                StartTrackTitleMarqueeTimer();
                break;
            case PlaybackState.Paused:
                // Don't stop timer — title may still be animating
                break;
            default:
                ResetArtistNameMarquee();
                break;
        }
    }

    private void ResetArtistNameMarquee()
    {
        _artistNameOverflow = 0;
        _artistNameDirection = -1;
        _artistNamePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
        SetArtistNameOffset(0);
    }

    private void ApplyArtistNameStaticPresentation(bool constrainToViewport, double viewportWidth)
    {
        ResetArtistNameMarquee();
        SetArtistNameWidth(constrainToViewport ? viewportWidth : double.NaN);
    }

    private double MeasureArtistNameTextWidth()
    {
        var text = ArtistNameTextBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            ArtistNameTextBlock.FlowDirection,
            new Typeface(
                ArtistNameTextBlock.FontFamily,
                ArtistNameTextBlock.FontStyle,
                ArtistNameTextBlock.FontWeight,
                ArtistNameTextBlock.FontStretch),
            ArtistNameTextBlock.FontSize,
            Brushes.Transparent);

        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private void SetArtistNameWidth(double width)
    {
        var currentWidth = ArtistNameTextBlock.Width;
        var widthsMatch = (double.IsNaN(currentWidth) && double.IsNaN(width)) ||
                          (!double.IsNaN(currentWidth) && !double.IsNaN(width) && Math.Abs(currentWidth - width) < 0.5);
        if (!widthsMatch)
            ArtistNameTextBlock.Width = width;
    }

    private void SetArtistNameOffset(double offset)
    {
        _artistNameOffset = offset;

        if (ArtistNameTextBlock.RenderTransform is TranslateTransform transform)
            transform.X = offset;
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty)
        {
            UpdateVolumeSliderVisual();
        }
        else if (e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdateVolumeSliderVisual();
        }
    }

    private void OnSeekStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isSeekDragging = true;
        vm.BeginSeek();
        e.Pointer.Capture(slider);
        var position = e.GetPosition(slider);
        slider.Value = GetPercentageFromPointer(slider, position);
        e.Handled = true; // Prevent Slider's Thumb from starting its own drag
    }

    private void OnSeekMove(object? sender, PointerEventArgs e)
    {
        if (sender is not Slider slider) return;

        var position = e.GetPosition(slider);
        if (!_isSeekDragging) return; // Only process seeks during OUR drag

        slider.Value = GetPercentageFromPointer(slider, position);
        e.Handled = true;
    }

    private void OnSeekEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSeekDragging) return;
        _isSeekDragging = false;

        e.Pointer.Capture(null);

        if (DataContext is PlayerViewModel vm)
            vm.EndSeek();

        e.Handled = true;
    }

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isSeekDragging) return;
        _isSeekDragging = false;

        if (DataContext is PlayerViewModel vm)
            vm.EndSeek();
    }

    private void OnSeekSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty ||
            e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdateSeekSliderVisual();
        }
    }

    private void UpdateSeekSliderVisual()
    {
        if (SeekSlider == null ||
            SeekTrackBackground == null ||
            SeekTrackFill == null ||
            SeekThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            SeekSlider,
            SeekTrackBackground,
            SeekTrackFill,
            SeekThumb,
            _seekThumbTransform,
            SeekThumbSize);

        SeekTrackFill.Width = Math.Max(0, SeekTrackFill.Width - (SeekThumbSize / 2.0));
    }

    private static double GetPercentageFromPointer(Slider slider, Point position)
    {
        return PillSliderVisualHelper.GetValueFromPointer(slider, position, SeekThumbSize);
    }

    // Clicking the album-art thumbnail toggles the compact always-on-top mini player window.
    private void OnAlbumArtPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            mainWindow.ToggleMiniPlayer();
            e.Handled = true;
        }
    }

    private void ShowVolumeBubble(bool show)
    {
        if (show) UpdateVolumeBubble();
        VolumeBubble.Opacity = show ? 1 : 0;
    }

    // Keep the % bubble centered above the thumb (clamped to the flyout edges).
    private void UpdateVolumeBubble()
    {
        var value = (int)Math.Round(VolumeSlider.Value);
        VolumeBubbleText.Text = $"{value}%";
        var frac = Math.Clamp(VolumeSlider.Value / Math.Max(1, VolumeSlider.Maximum), 0, 1);
        var thumbCenter = frac * (VolumeSliderVisualWidth - VolumeThumbSize) + VolumeThumbSize / 2;
        // Measure the TEXT, not the bubble Border: setting Text only invalidates
        // the TextBlock's own measure, so Measure() on the still-valid Border is
        // a cached no-op and the clamp used the previous value's width — which
        // let wider readouts like "100%" hang past the popup edge and clip.
        VolumeBubbleText.Measure(Size.Infinity);
        // 16 = bubble Padding (7+7) + BorderThickness (1+1).
        var bubbleWidth = VolumeBubbleText.DesiredSize.Width + 16;
        var layerWidth = VolumeBubbleLayer.Bounds.Width > 0 ? VolumeBubbleLayer.Bounds.Width : 102;
        // 9 = flyout border (1) + pill padding (8) offsets the slider inside the pill.
        // 2px edge inset: at exactly layerWidth the border sits on the popup's last
        // pixel and gets shaved on fractional display scales.
        var left = 9 + thumbCenter - bubbleWidth / 2;
        Canvas.SetLeft(VolumeBubble, Math.Clamp(left, 2, Math.Max(2, layerWidth - bubbleWidth - 2)));
    }

    private void OnVolumeWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        var step = e.Delta.Y > 0 ? 5 : e.Delta.Y < 0 ? -5 : 0;
        if (step == 0) return;
        vm.UnmuteForAdjust();
        vm.Volume = Math.Clamp(vm.Volume + step, 0, 100);
        vm.CommitVolume();
        // Also reopen while the exit fade is running (still IsOpen, but the exit timer
        // is about to unmap it) — the user is actively adjusting the volume.
        if (!VolumeFlyout.IsOpen || _volumeFlyoutExitTimer != null) OpenVolumeFlyout();
        e.Handled = true;
    }

    private void OnVolumeSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        // Pressing the slider is an explicit "keep using this": cancel any pending
        // hover-close and, if the popup is mid exit-fade, revive it in place —
        // otherwise the exit timer unmaps it under the active drag.
        CancelVolumeFlyoutClose();
        if (_volumeFlyoutExitTimer != null)
        {
            CancelVolumeFlyoutExit();
            VolumeFlyoutContent.Opacity = 1.0;
            SetVolumeFlyoutOffset(0);
        }

        _isVolumeDragging = true;
        (DataContext as PlayerViewModel)?.UnmuteForAdjust();
        e.Pointer.Capture(slider);
        slider.Value = GetVolumeFromPointer(slider, e.GetPosition(slider));
        ShowVolumeBubble(true);
        e.Handled = true;
    }

    private void OnVolumeSliderMoved(object? sender, PointerEventArgs e)
    {
        if (!_isVolumeDragging || sender is not Slider slider) return;

        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed)
        {
            // The release never reached us (missed Released/CaptureLost, e.g. the
            // popup unmapped mid-drag). End the drag now — a latched-true
            // _isVolumeDragging would make ScheduleVolumeFlyoutClose a no-op forever,
            // leaving the flyout stuck open on this and every future open.
            _isVolumeDragging = false;
            ShowVolumeBubble(false);
            (DataContext as PlayerViewModel)?.CommitVolume();
            ReevaluateVolumeFlyoutHover(e);
            return;
        }

        slider.Value = GetVolumeFromPointer(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isVolumeDragging)
        {
            _isVolumeDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        ShowVolumeBubble(false);
        (DataContext as PlayerViewModel)?.CommitVolume();
        // The drag suppressed any hover-close; now that it's over, close if the
        // cursor ended up away from the icon and popup. Pass the event so the
        // check uses the actual release position — IsPointerOver is still pinned
        // to the captured slider's ancestors at this point and would read a stale
        // "over" even when the cursor ended up far away.
        ReevaluateVolumeFlyoutHover(e);
    }

    private void OnVolumeSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isVolumeDragging) return;

        _isVolumeDragging = false;
        ShowVolumeBubble(false);
        (DataContext as PlayerViewModel)?.CommitVolume();
        // Abnormal end of drag: there is no reliable pointer position here and
        // IsPointerOver may be stale (capture pinned it to the slider chain). If the
        // cursor really is outside, the popup will never receive another pointer
        // event to fire PointerExited — so always arm the close; re-entering the
        // icon or popup within the grace period cancels it as usual.
        ScheduleVolumeFlyoutClose();
    }

    private void UpdateVolumeSliderVisual()
    {
        if (VolumeSlider == null ||
            VolumeTrackBackground == null ||
            VolumeTrackFill == null ||
            VolumeThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            VolumeSlider,
            VolumeTrackBackground,
            VolumeTrackFill,
            VolumeThumb,
            _volumeThumbTransform,
            VolumeThumbSize,
            enabledBackgroundOpacity: 0.4,
            disabledBackgroundOpacity: 0.25);

        if (_isVolumeDragging)
            UpdateVolumeBubble();
    }

    private static double GetVolumeFromPointer(Slider slider, Point position)
    {
        return PillSliderVisualHelper.GetValueFromPointer(slider, position, VolumeThumbSize);
    }

    // Slim bar (3 transport, 4 right icons) — narrower than the old 5+5 layout.
    private const double IslandBaseWidth = 590;
    // Lyrics page hides the center track-info, so the pill only holds transport + right icons.
    private const double IslandLyricsPageWidth = 340;
    private static readonly TimeSpan VolumeFlyoutCloseDelay = TimeSpan.FromMilliseconds(140);
    // Matches the slowest entrance/exit transition on VolumeFlyoutContent (Y = 0.18s) so the
    // popup stays alive long enough for the slide-down + fade-out to finish before it unmaps.
    private static readonly TimeSpan VolumeFlyoutExitDuration = TimeSpan.FromMilliseconds(190);

    private DispatcherTimer? _volumeFlyoutCloseTimer;
    private DispatcherTimer? _volumeFlyoutExitTimer;

    private void OnVolumeIconClick(object? sender, RoutedEventArgs e)
    {
        // First click opens the popup without muting; subsequent clicks while it's open
        // toggle mute and keep the popup visible so the user can keep adjusting.
        // A click during the close animation (still IsOpen, but fading out) re-opens it.
        if (!VolumeFlyout.IsOpen || _volumeFlyoutExitTimer != null)
        {
            OpenVolumeFlyout();
            return;
        }

        if (_observedPlayerViewModel?.ToggleMuteCommand.CanExecute(null) == true)
            _observedPlayerViewModel.ToggleMuteCommand.Execute(null);
    }

    private void OpenVolumeFlyout()
    {
        CancelVolumeFlyoutClose();
        CancelVolumeFlyoutExit();
        // Start just below the final position, then ease upward as it fades in.
        VolumeFlyoutContent.Opacity = 0;
        SetVolumeFlyoutOffset(6);
        VolumeFlyout.IsOpen = true;
        UpdateVolumeSliderVisual();
        Dispatcher.UIThread.Post(() =>
        {
            VolumeFlyoutContent.Opacity = 1.0;
            SetVolumeFlyoutOffset(0);
        }, DispatcherPriority.Render);
        // The popup only ever closes off pointer-exit of the icon/popup (or the
        // post-drag reevaluation). If it was opened without the cursor anywhere near
        // — keyboard activation of the icon button fires Click too — no exit will
        // ever come, so re-check once the open settles and arm the hover-close then.
        Dispatcher.UIThread.Post(() => ReevaluateVolumeFlyoutHover(), DispatcherPriority.Input);
    }

    private void CloseVolumeFlyout()
    {
        CancelVolumeFlyoutClose();
        if (!VolumeFlyout.IsOpen) return;

        // Reverse of the open animation: slide back down + fade out, then unmap the popup
        // once the transition has finished (setting IsOpen=false immediately would snap it shut).
        VolumeFlyoutContent.Opacity = 0;
        SetVolumeFlyoutOffset(6);

        CancelVolumeFlyoutExit();
        _volumeFlyoutExitTimer = new DispatcherTimer { Interval = VolumeFlyoutExitDuration };
        _volumeFlyoutExitTimer.Tick += (_, _) =>
        {
            CancelVolumeFlyoutExit();
            VolumeFlyout.IsOpen = false;
        };
        _volumeFlyoutExitTimer.Start();
    }

    private void CancelVolumeFlyoutExit()
    {
        _volumeFlyoutExitTimer?.Stop();
        _volumeFlyoutExitTimer = null;
    }

    private void SetVolumeFlyoutOffset(double y)
    {
        if (VolumeFlyoutContent.RenderTransform is TranslateTransform transform)
            transform.Y = y;
    }

    // Pointer leaves the icon or the popup → schedule a close.
    // A brief grace period lets the cursor cross the small gap between the icon and the popup
    // without dismissing — if it re-enters either, we cancel the pending close.
    private void OnVolumeButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (VolumeFlyout.IsOpen)
            ScheduleVolumeFlyoutClose();
    }

    private void OnVolumeButtonPointerEntered(object? sender, PointerEventArgs e) =>
        CancelVolumeFlyoutClose();

    private void OnVolumeFlyoutPointerExited(object? sender, PointerEventArgs e) =>
        ScheduleVolumeFlyoutClose();

    private void OnVolumeFlyoutPointerEntered(object? sender, PointerEventArgs e) =>
        CancelVolumeFlyoutClose();

    private void ScheduleVolumeFlyoutClose()
    {
        // Never close mid-drag: while adjusting volume the captured pointer can drift off
        // the thin popup strip and fire PointerExited, which would otherwise dismiss it.
        if (_isVolumeDragging) return;

        CancelVolumeFlyoutClose();
        _volumeFlyoutCloseTimer = new DispatcherTimer { Interval = VolumeFlyoutCloseDelay };
        _volumeFlyoutCloseTimer.Tick += (_, _) => CloseVolumeFlyout();
        _volumeFlyoutCloseTimer.Start();
    }

    // After a drag ends, decide whether to keep the popup open: stay if the cursor is still
    // over the icon or popup, otherwise schedule the normal hover-close.
    // When the triggering pointer event is available, the check is geometric: while a
    // pointer is captured, Avalonia pins pointer-over to the captured element's ancestor
    // chain, so right after a captured drag VolumeFlyoutContent.IsPointerOver reads true
    // no matter where the cursor actually is — and since the popup then gets no further
    // pointer events, no PointerExited would ever fire and the flyout would stick open.
    private void ReevaluateVolumeFlyoutHover(PointerEventArgs? e = null)
    {
        if (!VolumeFlyout.IsOpen) return;

        if (e is null)
        {
            if (VolumeButton.IsPointerOver || VolumeFlyoutContent.IsPointerOver) return;
        }
        else if (IsPointerOverVolumeUi(e))
        {
            return;
        }

        ScheduleVolumeFlyoutClose();
    }

    // Position-based hit check against the popup content and the icon button.
    private bool IsPointerOverVolumeUi(PointerEventArgs e)
    {
        var contentPos = e.GetPosition(VolumeFlyoutContent);
        if (contentPos.X >= 0 && contentPos.Y >= 0 &&
            contentPos.X <= VolumeFlyoutContent.Bounds.Width &&
            contentPos.Y <= VolumeFlyoutContent.Bounds.Height)
            return true;

        // The icon button lives in the main window while the event comes from the
        // popup's root; GetPosition can't map across visual trees (it returns default
        // for an unreachable visual, which would false-positive at the button's 0,0).
        // Round-trip through screen coordinates instead.
        if (TopLevel.GetTopLevel(VolumeFlyoutContent) is { } popupTop &&
            TopLevel.GetTopLevel(VolumeButton) is { } mainTop)
        {
            var screenPoint = popupTop.PointToScreen(e.GetPosition(popupTop));
            var clientPoint = mainTop.PointToClient(screenPoint);
            if (mainTop.TranslatePoint(clientPoint, VolumeButton) is { } buttonPos &&
                buttonPos.X >= 0 && buttonPos.Y >= 0 &&
                buttonPos.X <= VolumeButton.Bounds.Width &&
                buttonPos.Y <= VolumeButton.Bounds.Height)
                return true;
        }

        return false;
    }

    private void CancelVolumeFlyoutClose()
    {
        _volumeFlyoutCloseTimer?.Stop();
        _volumeFlyoutCloseTimer = null;
    }

    /// <summary>Sizes the pill for the page it is on. Always an instant write — see the
    /// note on IslandBorder in the XAML for why the width must never animate.</summary>
    private void UpdateIslandWidth()
    {
        IslandBorder.Width = CompactWhenLyricsPageActive
                              && _observedPlayerViewModel?.IsLyricsPageActive == true
            ? IslandLyricsPageWidth
            : IslandBaseWidth;
    }

    private void OnTrackInfoRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not PlayerViewModel { CurrentTrack: not null }) return;

        OptionsButton.Flyout?.ShowAt(OptionsButton);
        e.Handled = true;
    }

    // Expands a submenu the instant the pointer enters its parent item, skipping the
    // default hover delay. Shared by the Sleep Timer and Lyrics Display menu items.
    private void OnExpandSubMenuPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is MenuItem item)
            item.IsSubMenuOpen = true;
    }

    private void OnLyricsButtonClick(object? sender, RoutedEventArgs e)
    {
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
            mainVm.ToggleLyricsCommand.Execute(null);
    }

    private void OnLyricsPanelButtonClick(object? sender, RoutedEventArgs e)
    {
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
            mainVm.ToggleLyricsPanelCommand.Execute(null);
    }

    private void RefreshTrackInfoLayout()
    {
        ResetTrackTitleMarquee();
        ResetArtistNameMarquee();
        ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
        UpdateSeekSliderVisual();
    }
}




