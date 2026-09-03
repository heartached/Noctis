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
using Avalonia.VisualTree;
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
    /// <summary>Rest at the start position between laps — full loop out the left edge and
    /// back in from the right, matching MarqueeTextBlock's behavior app-wide.</summary>
    private static readonly TimeSpan TrackTitleRestPause = TimeSpan.FromSeconds(7);
    // Frame-clock driven (TopLevel.RequestAnimationFrame), NOT a DispatcherTimer: a 16 ms
    // timer defaults to Background priority (starved by layout/render work) and beats
    // against the ~16.7 ms vsync — visible stutter. Same migration as MarqueeTextBlock,
    // the lyrics scroll, and SmoothScrollBehavior.
    private bool _marqueeRunning;
    private bool _marqueeFrameQueued;
    private long _marqueeLastTimestamp;
    private int _marqueeResumeGeneration;
    private PlayerViewModel? _observedPlayerViewModel;
    private double _trackTitleOverflow;
    private double _trackTitleTextWidth;
    private double _trackTitleViewportWidth;
    private double _trackTitleOffset;
    private double _trackTitlePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
    private bool _trackTitleUpdateScheduled;
    private bool _trackTitleResetPending;
    private const double SeekThumbSize = 12;
    private readonly TranslateTransform _seekThumbTransform = new();

    // Artist name marquee state (syncs with title marquee via same timer)
    private double _artistNameOverflow;
    private double _artistNameTextWidth;
    private double _artistNameViewportWidth;
    private double _artistNameOffset;
    private double _artistNamePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
    private bool _artistNameUpdateScheduled;
    private bool _artistNameResetPending;

    // Seek slider drag state — only our code sets/clears this, preventing
    // stale Thumb state or stray pointer moves from triggering seeks.
    private bool _isSeekDragging;
    private bool _isVolumeDragging;
    private const double VolumeThumbSize = 12;
    private const double VolumeSliderVisualWidth = 84;
    private readonly TranslateTransform _volumeThumbTransform = new();

    // Island edge-resize drag state (persistent bar only; the lyrics-page copy is fixed).
    // The reference visual is the TopLevel: the bar itself is Center-aligned and
    // shrink-wraps the island, so its own origin shifts as the width changes.
    private bool _isResizeDragging;
    private bool _resizeFromLeftGrip;
    private bool _resizeWidthChanged;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private Visual? _resizeReference;
    private Control? _resizeHost;
    private bool _isWidthCompact;

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

        // Shape follows the ARRANGED width, so a window squeeze (MaxWidth clamping the
        // island) morphs the layout exactly like a user drag does.
        IslandBorder.SizeChanged += OnIslandBorderSizeChanged;
        UpdateResizeGripVisibility();

        PropertyChanged += OnPlaybackBarPropertyChanged;
        TrackTitleTextBlock.PropertyChanged += OnTrackTitleTextBlockPropertyChanged;
        TrackTitleViewport.PropertyChanged += OnTrackTitleViewportPropertyChanged;
        ArtistNameTextBlock.PropertyChanged += OnArtistNameTextBlockPropertyChanged;
        ArtistNameViewport.PropertyChanged += OnArtistNameViewportPropertyChanged;
        AttachedToVisualTree += OnPlaybackBarAttachedToVisualTree;
        DetachedFromVisualTree += OnPlaybackBarDetachedFromVisualTree;
        DataContextChanged += OnPlaybackBarDataContextChanged;

    }

    private void OnPlaybackBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == CompactWhenLyricsPageActiveProperty)
        {
            UpdateResizeGripVisibility();
            UpdateIslandWidth();
        }

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

        // The resizable (persistent) bar may never be wider than its host: track the
        // host's size and clamp via MaxWidth, so the stored user width survives a
        // too-narrow window untouched and comes back when there is room again.
        if (!CompactWhenLyricsPageActive && _resizeHost == null
            && this.GetVisualParent() is Control host)
        {
            _resizeHost = host;
            host.SizeChanged += OnResizeHostSizeChanged;
            UpdateIslandMaxWidth();
        }

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

        if (_resizeHost != null)
        {
            _resizeHost.SizeChanged -= OnResizeHostSizeChanged;
            _resizeHost = null;
        }
        _isResizeDragging = false;

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

        if (e.PropertyName == nameof(PlayerViewModel.IsLyricsPageActive) ||
            e.PropertyName == nameof(PlayerViewModel.PlaybackBarIslandWidth) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowSkipButtons) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowPlaybackSpeed) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowSleepTimer) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowShuffle))
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
        // Loop geometry: the badge's trailing pad must clear the edge before the wrap,
        // same reason it's added to the overflow above.
        _trackTitleTextWidth = hasOverflow && ExplicitBadge.IsVisible
            ? textWidth + TrackTitleBadgeTrailingPadding
            : textWidth;
        _trackTitleViewportWidth = viewportWidth;
        var shouldAnimate = vm.TrackTitleMarqueeEnabled && hasOverflow;
        if (!shouldAnimate)
        {
            ApplyTrackTitleStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetTrackTitleWidth(double.NaN);

        // Keep the phase across benign re-measures; reset when asked or out of the
        // loop's valid range (-textWidth, viewportWidth].
        if (resetAnimation || _trackTitleOffset < -_trackTitleTextWidth || _trackTitleOffset > _trackTitleViewportWidth)
        {
            _trackTitlePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
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
        // restart the animation either.
        if (_marqueeRunning || VisualRoot == null || Opacity <= 0)
            return;

        _marqueeRunning = true;
        _marqueeLastTimestamp = Stopwatch.GetTimestamp();
        QueueMarqueeFrame();
    }

    private void StopTrackTitleMarqueeTimer()
    {
        _marqueeRunning = false;
        _marqueeResumeGeneration++; // cancels any pending between-laps resume
    }

    private void QueueMarqueeFrame()
    {
        if (!_marqueeRunning || _marqueeFrameQueued)
            return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            StopTrackTitleMarqueeTimer();
            return;
        }
        _marqueeFrameQueued = true;
        topLevel.RequestAnimationFrame(OnMarqueeFrame);
    }

    private void ResetTrackTitleMarquee()
    {
        StopTrackTitleMarqueeTimer();
        _trackTitleOverflow = 0;
        _trackTitlePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
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

    private void OnMarqueeFrame(TimeSpan frameTime)
    {
        _marqueeFrameQueued = false;
        if (!_marqueeRunning)
            return;

        if (DataContext is not PlayerViewModel { State: PlaybackState.Playing, CurrentTrack: not null } vm)
        {
            StopTrackTitleMarqueeTimer();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        // Real elapsed time, clamped so a stalled UI thread can't produce one giant jump.
        var elapsedMs = Math.Min((now - _marqueeLastTimestamp) * 1000.0 / Stopwatch.Frequency, 100);
        _marqueeLastTimestamp = now;

        var titleActive = vm.TrackTitleMarqueeEnabled && _trackTitleOverflow > TrackTitleOverflowThreshold;
        var artistActive = vm.ArtistMarqueeEnabled && _artistNameOverflow > TrackTitleOverflowThreshold;

        if (!titleActive && !artistActive)
        {
            StopTrackTitleMarqueeTimer();
            if (!titleActive) ResetTrackTitleMarquee();
            if (!artistActive) ResetArtistNameMarquee();
            return;
        }

        if (elapsedMs > 0)
        {
            // Tick title marquee
            if (titleActive)
                TickMarquee(elapsedMs, _trackTitleOffset, ref _trackTitlePauseRemainingMs,
                    _trackTitleTextWidth, _trackTitleViewportWidth, SetTrackTitleOffset);

            // Tick artist marquee (same speed, independent phase)
            if (artistActive)
                TickMarquee(elapsedMs, _artistNameOffset, ref _artistNamePauseRemainingMs,
                    _artistNameTextWidth, _artistNameViewportWidth, SetArtistNameOffset);
        }

        // While anything is mid-lap, ride the frame clock. When every active marquee is
        // resting, sleep until the earliest rest expires instead of forcing continuous
        // renders through a 7-second hold.
        var wait = Math.Min(
            titleActive ? _trackTitlePauseRemainingMs : double.PositiveInfinity,
            artistActive ? _artistNamePauseRemainingMs : double.PositiveInfinity);
        if (wait <= 0)
        {
            QueueMarqueeFrame();
            return;
        }

        _marqueeRunning = false;
        var generation = ++_marqueeResumeGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation != _marqueeResumeGeneration)
                return;
            // Account for the slept time in BOTH rests — they have independent phases,
            // and only the earliest one has necessarily expired.
            _trackTitlePauseRemainingMs = Math.Max(0, _trackTitlePauseRemainingMs - wait);
            _artistNamePauseRemainingMs = Math.Max(0, _artistNamePauseRemainingMs - wait);
            StartTrackTitleMarqueeTimer();
        }, TimeSpan.FromMilliseconds(wait));
    }

    /// <summary>Full-loop marquee step, matching MarqueeTextBlock: the text always travels
    /// left; once its tail clears the viewport's left edge it wraps to just past the right
    /// edge and slides back in; landing on the start position rests for RestPause.</summary>
    private static void TickMarquee(double elapsedMs, double offset, ref double pauseRemainingMs,
        double textWidth, double viewportWidth, Action<double> setOffset)
    {
        if (pauseRemainingMs > 0)
        {
            pauseRemainingMs = Math.Max(0, pauseRemainingMs - elapsedMs);
            return;
        }

        var nextOffset = offset - TrackTitleScrollSpeed * elapsedMs / 1000.0;
        if (nextOffset <= -textWidth)
        {
            nextOffset += textWidth + viewportWidth;
        }
        else if (offset > 0 && nextOffset <= 0)
        {
            // Only a wrapped (incoming-from-the-right) pass crosses zero downward —
            // the outbound pass STARTS at zero, so this never fires on the way out.
            nextOffset = 0;
            pauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
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
        _artistNameTextWidth = textWidth;
        _artistNameViewportWidth = viewportWidth;
        var hasOverflow = _artistNameOverflow > TrackTitleOverflowThreshold;
        var shouldAnimate = vm.ArtistMarqueeEnabled && hasOverflow;
        if (!shouldAnimate)
        {
            ApplyArtistNameStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetArtistNameWidth(double.NaN);

        // Keep the phase across benign re-measures; reset when asked or out of the
        // loop's valid range (-textWidth, viewportWidth].
        if (resetAnimation || _artistNameOffset < -_artistNameTextWidth || _artistNameOffset > _artistNameViewportWidth)
        {
            _artistNamePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
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
        _artistNamePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
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

        // No extra width trim here: the helper already ends the fill at the thumb's
        // centre (the volume slider uses it as-is). Shaving another half-thumb off
        // left a visible dark gap between the fill's rounded end and the thumb, and
        // an unfilled sliver at the track's right end even at 100%.
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

    // Slim bar (3 transport, 5 right icons — the favorite heart joined in 1.4.8) — still
    // narrower than the old 5+5 layout.
    private const double IslandBaseWidth = 626;
    // Lyrics page hides the center track-info, so the pill only holds transport + right icons.
    private const double IslandLyricsPageWidth = 340;

    // ── User resize (persistent bar only) ──
    // Shape thresholds derived from the clusters' natural widths as declared in the
    // XAML: transport 148 + 14 margin = 162; right icons 5 × 34 + 4 × 2 spacing = 178,
    // − 16 margin = 162 (the favorite heart hides with the track info, so the compact
    // pill still holds the proven 4); track info 36 art + 12 + 192 viewport + 8 + 6
    // margins = 254; island chrome 24 padding + 3 border = 27. Full layout therefore
    // needs 605px; with the viewports narrowed to 120 ("bar-mid") it needs 533px;
    // transport + 4 icons alone need 315px — 340 is the compact layout the lyrics page
    // already uses.
    private const double IslandFullShapeMinWidth = 606; // below: viewports narrow to 120
    private const double IslandMidShapeMinWidth = 536;  // below: track info hidden (compact pill)
    private const double IslandMinUserWidth = IslandLyricsPageWidth;
    // Each optional island button (speed / skip back / skip forward / sleep) is a 34px
    // transport button plus the row's 2px spacing.
    private const double ExtraTransportButtonWidth = 36;
    // Breathing room to the host's edges, matching the 8px margins the side panels use.
    private const double IslandEdgeMargin = 8;
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
    /// note on IslandBorder in the XAML for why the width must never animate. The
    /// persistent bar uses the user's stored width (PlayerViewModel, hydrated from
    /// AppSettings.PlaybackBarWidth); the lyrics-page copy keeps its fixed sizes.</summary>
    private void UpdateIslandWidth()
    {
        if (_isResizeDragging) return; // the live drag owns the width until release

        // The podcast/audiobook extras widen the transport cluster; the stock width
        // (and the fixed lyrics-page pill) grow with them so the layout budget holds.
        // A width the user chose themselves is left alone — the shape thresholds
        // below account for the extras instead.
        var extra = ExtraTransportWidth;
        if (CompactWhenLyricsPageActive && _observedPlayerViewModel?.IsLyricsPageActive == true)
            IslandBorder.Width = IslandLyricsPageWidth + extra;
        else if (!CompactWhenLyricsPageActive && _observedPlayerViewModel is { } vm)
        {
            var width = ClampUserIslandWidth(vm.PlaybackBarIslandWidth);
            if (Math.Abs(width - IslandBaseWidth) < 0.5)
                width += extra;
            IslandBorder.Width = width;
        }
        else
            IslandBorder.Width = IslandBaseWidth + extra;

        // SizeChanged only fires when the arranged size actually changes, so re-apply
        // here too: a lyrics-page flip must refresh the track-info visibility even
        // when the width stays put.
        ApplyIslandShape(IslandBorder.Width);
    }

    /// <summary>Width the visible island extras add to the transport cluster.</summary>
    private double ExtraTransportWidth
    {
        get
        {
            if (_observedPlayerViewModel is not { } vm) return 0;
            var buttons = (vm.IslandShowSkipButtons ? 2 : 0)
                        + (vm.IslandShowPlaybackSpeed ? 1 : 0)
                        + (vm.IslandShowSleepTimer ? 1 : 0)
                        + (vm.IslandShowShuffle ? 1 : 0);
            return buttons * ExtraTransportButtonWidth;
        }
    }

    /// <summary>Lower bound + garbage guard for a stored width; the upper bound is the
    /// window, enforced live via MaxWidth (see UpdateIslandMaxWidth).</summary>
    private static double ClampUserIslandWidth(double width) =>
        double.IsFinite(width) ? Math.Max(IslandMinUserWidth, width) : IslandBaseWidth;

    /// <summary>Adapts the persistent bar's layout to its width, reusing the same
    /// hide-the-track-info trick the 340px lyrics state already relies on: full
    /// layout → "bar-mid" (title/artist viewports narrow to 120) → compact pill
    /// (track info hidden entirely). Classes only toggle on threshold crossings.</summary>
    private void ApplyIslandShape(double width)
    {
        if (!CompactWhenLyricsPageActive && width > 0)
        {
            var extra = ExtraTransportWidth;
            _isWidthCompact = width < IslandMidShapeMinWidth + extra;
            var mid = !_isWidthCompact && width < IslandFullShapeMinWidth + extra;
            if (IslandBorder.Classes.Contains("bar-mid") != mid)
            {
                if (mid) IslandBorder.Classes.Add("bar-mid");
                else IslandBorder.Classes.Remove("bar-mid");
            }
        }

        UpdateTrackInfoVisibility();
    }

    /// <summary>Replaces the old IsVisible="{Binding !IsLyricsPageActive}" on
    /// TrackInfoPanel: the compact resize shape must hide it too, and a width-driven
    /// style could never beat that local binding, so both conditions live here.</summary>
    private void UpdateTrackInfoVisibility()
    {
        var visible = _observedPlayerViewModel?.IsLyricsPageActive != true && !_isWidthCompact;
        // The favorite heart is budgeted with the track info: both go when the pill is
        // compact, so the 340px layout keeps its original four right-hand icons.
        FavoriteButton.IsVisible = visible;
        if (TrackInfoPanel.IsVisible == visible)
            return;

        TrackInfoPanel.IsVisible = visible;
        if (!visible)
        {
            // A hidden panel's viewports report zero bounds, which makes the marquee
            // update bail out early — so stop the ticking here or the 16ms timer would
            // keep scrolling a collapsed panel. Re-showing re-schedules automatically
            // via the viewport Bounds-change handlers.
            ResetTrackTitleMarquee();
            ResetArtistNameMarquee();
        }
    }

    private void OnIslandBorderSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ApplyIslandShape(e.NewSize.Width);

    private void OnResizeHostSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateIslandMaxWidth();

    /// <summary>The island may never exceed the host: Width keeps the user's choice,
    /// MaxWidth does the clamping, so a squeezed window shrinks the bar gracefully and
    /// growing it back restores the stored width with no state loss.</summary>
    private void UpdateIslandMaxWidth()
    {
        if (_resizeHost == null) return;
        var available = _resizeHost.Bounds.Width - IslandEdgeMargin * 2;
        if (available <= 0) return;
        IslandBorder.MaxWidth = Math.Max(IslandMinUserWidth, available);
    }

    private void UpdateResizeGripVisibility()
    {
        // Grips exist on the persistent bottom bar only; the lyrics-page copy is fixed.
        var visible = !CompactWhenLyricsPageActive;
        LeftResizeGrip.IsVisible = visible;
        RightResizeGrip.IsVisible = visible;
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            // Double-click a grip: back to the stock width, persisted like a drag.
            _isResizeDragging = false;
            IslandBorder.Width = IslandBaseWidth;
            PersistIslandWidth(IslandBaseWidth);
            e.Handled = true;
            return;
        }

        _resizeReference = TopLevel.GetTopLevel(this);
        if (_resizeReference == null) return;

        _isResizeDragging = true;
        _resizeWidthChanged = false;
        _resizeFromLeftGrip = ReferenceEquals(grip, LeftResizeGrip);
        _resizeStartX = e.GetPosition(_resizeReference).X;
        // Start from the ARRANGED width: if MaxWidth is currently clamping a wider
        // stored value, starting from Width would jump on the first move.
        _resizeStartWidth = IslandBorder.Bounds.Width > 0 ? IslandBorder.Bounds.Width : IslandBorder.Width;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnResizeGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizeDragging || _resizeReference == null) return;

        var dx = e.GetPosition(_resizeReference).X - _resizeStartX;
        // The island stays centred, so both edges move together: pulling one edge out
        // by dx grows the width by 2*dx — the dragged edge then tracks the cursor.
        var target = _resizeFromLeftGrip ? _resizeStartWidth - dx * 2 : _resizeStartWidth + dx * 2;
        var max = double.IsFinite(IslandBorder.MaxWidth)
            ? Math.Max(IslandMinUserWidth, IslandBorder.MaxWidth)
            : double.MaxValue;
        target = Math.Round(Math.Clamp(target, IslandMinUserWidth, max));

        // Whole-pixel writes only: no allocations, no saves, and no layout pass at all
        // unless the width actually changed.
        if (Math.Abs(IslandBorder.Width - target) >= 1)
        {
            IslandBorder.Width = target;
            _resizeWidthChanged = true;
        }
        e.Handled = true;
    }

    private void OnResizeGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizeDragging) return;
        _isResizeDragging = false;
        e.Pointer.Capture(null);
        if (_resizeWidthChanged) PersistIslandWidth(IslandBorder.Width);
        e.Handled = true;
    }

    private void OnResizeGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizeDragging) return;
        _isResizeDragging = false;
        // The width already changed on screen; keep VM + disk consistent with it.
        if (_resizeWidthChanged) PersistIslandWidth(IslandBorder.Width);
    }

    /// <summary>Pushes a finished resize into the view model and, through it, into the
    /// settings save pipeline (drag release, capture loss and double-click reset).</summary>
    private void PersistIslandWidth(double width) =>
        _observedPlayerViewModel?.CommitPlaybackBarWidth(width);

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




