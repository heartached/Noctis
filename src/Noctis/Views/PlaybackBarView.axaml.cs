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
    private const double TrackTitleOverflowThreshold = 1.0;
    private const double TrackTitleScrollSpeed = 26.0;
    private static readonly TimeSpan TrackTitleEdgePause = TimeSpan.FromMilliseconds(850);
    private PlaylistMenuPopulator? _playlistPopulator;
    private readonly DispatcherTimer _trackTitleMarqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _trackTitleMarqueeClock = new();
    private PlayerViewModel? _observedPlayerViewModel;
    private double _trackTitleOverflow;
    private double _trackTitleOffset;
    private int _trackTitleDirection = -1;
    private double _trackTitlePauseRemainingMs = TrackTitleEdgePause.TotalMilliseconds;
    private bool _trackTitleUpdateScheduled;
    private bool _trackTitleResetPending;

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

        // Handle volume slider interaction to show/hide percentage badge
        // MUST use AddHandler with handledEventsToo:true because Slider's Thumb handles these events
        VolumeSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeSliderPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeSliderReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeSlider.PointerCaptureLost += OnVolumeSliderCaptureLost;

        // Track volume changes to update percentage badge position
        VolumeSlider.PropertyChanged += OnVolumeSliderPropertyChanged;

        TrackTitleTextBlock.PropertyChanged += OnTrackTitleTextBlockPropertyChanged;
        TrackTitleViewport.PropertyChanged += OnTrackTitleViewportPropertyChanged;
        ArtistNameTextBlock.PropertyChanged += OnArtistNameTextBlockPropertyChanged;
        ArtistNameViewport.PropertyChanged += OnArtistNameViewportPropertyChanged;
        _trackTitleMarqueeTimer.Tick += OnTrackTitleMarqueeTick;
        AttachedToVisualTree += OnPlaybackBarAttachedToVisualTree;
        DetachedFromVisualTree += OnPlaybackBarDetachedFromVisualTree;
        DataContextChanged += OnPlaybackBarDataContextChanged;

        _playlistPopulator = new PlaylistMenuPopulator(AddToPlaylistMenuItem, PlaylistSubmenuSeparator);
        if (OptionsButton.Flyout is MenuFlyout optionsFlyout)
            optionsFlyout.Opened += OnOptionsFlyoutOpened;
    }

    private void OnPlaybackBarAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
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

        ScheduleTrackTitleMarqueeUpdate(resetAnimation: true);
        ScheduleArtistNameMarqueeUpdate(resetAnimation: true);
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

        _trackTitleOverflow = Math.Max(0, textWidth - viewportWidth);
        var hasOverflow = _trackTitleOverflow > TrackTitleOverflowThreshold;
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
        if (_trackTitleMarqueeTimer.IsEnabled || VisualRoot == null)
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
        if (ExplicitBadge.IsVisible && ExplicitBadge.Bounds.Width > 0)
            width += 6 + ExplicitBadge.Bounds.Width; // 6 = StackPanel Spacing

        return width;
    }

    private void SetTrackTitleWidth(double width)
    {
        // When constraining for static truncation, reserve space for the badge
        if (!double.IsNaN(width) && ExplicitBadge.IsVisible && ExplicitBadge.Bounds.Width > 0)
            width = Math.Max(0, width - 6 - ExplicitBadge.Bounds.Width);

        var currentWidth = TrackTitleTextBlock.Width;
        var widthsMatch = (double.IsNaN(currentWidth) && double.IsNaN(width)) ||
                          (!double.IsNaN(currentWidth) && !double.IsNaN(width) && Math.Abs(currentWidth - width) < 0.5);
        if (!widthsMatch)
            TrackTitleTextBlock.Width = width;
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
            UpdateVolumePercentagePosition();
        }
    }

    private void UpdateVolumePercentagePosition()
    {
        if (VolumePercentageBadge.RenderTransform is not TranslateTransform transform)
            return;

        // Track has Margin="7,0" inside 90px slider, so track area = 76px
        // Thumb is 14px wide, travel range = 76 - 14 = 62px
        // At 0%: thumb center at 7 (track margin) + 7 (half thumb) = 14px
        // At 100%: thumb center at 14 + 62 = 76px
        const double trackMargin = 7.0;
        const double thumbHalfWidth = 7.0;
        const double thumbTravelRange = 62.0;

        var volume = VolumeSlider.Value;
        var fraction = volume / 100.0;

        // Calculate thumb center position, then center the text over it
        var thumbCenterX = trackMargin + thumbHalfWidth + (fraction * thumbTravelRange);
        var textWidth = VolumePercentageBadge.Bounds.Width;
        if (textWidth <= 0) textWidth = 18; // Estimate if not yet measured
        var xPos = thumbCenterX - (textWidth / 2);

        transform.X = xPos;
    }

    private void OnSeekStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isSeekDragging = true;
        vm.BeginSeek();
        e.Pointer.Capture(slider);
        slider.Value = GetPercentageFromPointer(slider, e.GetPosition(slider));
        e.Handled = true; // Prevent Slider's Thumb from starting its own drag
    }

    private void OnSeekMove(object? sender, PointerEventArgs e)
    {
        if (!_isSeekDragging) return; // Only process moves during OUR drag
        if (sender is not Slider slider) return;

        slider.Value = GetPercentageFromPointer(slider, e.GetPosition(slider));
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

    private static double GetPercentageFromPointer(Slider slider, Point position)
    {
        if (slider.Bounds.Width <= 0)
            return 0;

        var percentage = position.X / slider.Bounds.Width;
        return Math.Clamp(percentage, 0, 1);
    }

    private void OnVolumeSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateVolumePercentagePosition();
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        (DataContext as ViewModels.PlayerViewModel)?.CommitVolume();
    }

    private void OnVolumeSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        (DataContext as ViewModels.PlayerViewModel)?.CommitVolume();
    }

    private const double IslandBaseWidth = 620;
    private const double VolumeSliderExpandedWidth = 99;

    private void OnVolumeContainerEntered(object? sender, PointerEventArgs e)
    {
        // Expand the island and volume slider together so the timeline doesn't shrink
        IslandBorder.Width = IslandBaseWidth + VolumeSliderExpandedWidth;
        VolumeSliderContainer.Width = VolumeSliderExpandedWidth;
        VolumePercentageBadge.IsVisible = true;
        UpdateVolumePercentagePosition();
        VolumePercentageBadge.Opacity = 1.0;
    }

    private void OnVolumeContainerExited(object? sender, PointerEventArgs e)
    {
        // Collapse the island and volume slider together
        IslandBorder.Width = IslandBaseWidth;
        VolumeSliderContainer.Width = 0;
        VolumePercentageBadge.Opacity = 0;
        VolumePercentageBadge.IsVisible = false;
    }

    private void OnTrackInfoRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not PlayerViewModel { CurrentTrack: not null }) return;

        OptionsButton.Flyout?.ShowAt(OptionsButton);
        e.Handled = true;
    }

    private void OnLyricsPanelButtonClick(object? sender, RoutedEventArgs e)
    {
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
            mainVm.ToggleLyricsPanelCommand.Execute(null);
    }

    private void OnLyricsButtonClick(object? sender, RoutedEventArgs e)
    {
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
            mainVm.ToggleLyricsCommand.Execute(null);
    }

    private void OnOptionsFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        _playlistPopulator?.Populate(vm.Playlists, vm.AddCurrentTrackToExistingPlaylistCommand);
    }
}




