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
using Noctis.Controls;
using Noctis.ViewModels;
using Avalonia.LogicalTree;
using Noctis.Helpers;
using Noctis.Localization;
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
    private const double TrackTitleScrollSpeed = 30.0;
    private const double TrackTitleBadgeSpacing = 6.0;
    /// <summary>The hover border round the title / artist text adds 1 + 2 px of padding; the
    /// loop copy sits after that border, so one lap is text + padding + gap.</summary>
    private const double MarqueeTextPadding = 3.0;
    /// <summary>Rest at the start position between laps. A lap is a ticker pass: the text and
    /// its loop copy travel left until the copy stands where the text started, matching
    /// MarqueeTextBlock's behavior app-wide (the viewport is never blank mid-lap).</summary>
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
    /// <summary>Distance from the title's start to its loop copy's start.</summary>
    private double _trackTitleLapDistance;
    private double _trackTitleOffset;
    private double _trackTitlePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
    private bool _trackTitleUpdateScheduled;
    private bool _trackTitleResetPending;
    private const double SeekThumbSize = 12;
    private readonly TranslateTransform _seekThumbTransform = new();

    // Artist name marquee state (syncs with title marquee via same timer)
    private double _artistNameOverflow;
    private double _artistNameLapDistance;
    private double _artistNameOffset;
    private double _artistNamePauseRemainingMs = TrackTitleRestPause.TotalMilliseconds;
    private bool _artistNameUpdateScheduled;
    private bool _artistNameResetPending;

    // Seek slider drag state — only our code sets/clears this, preventing
    // stale Thumb state or stray pointer moves from triggering seeks.
    private bool _isSeekDragging;
    private bool _isVolumeDragging;
    // Matches VolumeThumb's Width/Height in the XAML (compact pill, 10-08).
    private const double VolumeThumbSize = 12;
    private const int VolumeStep = 5;
    private readonly TranslateTransform _volumeThumbTransform = new();
    private readonly VolumeWheelAccumulator _volumeWheel = new();
    // The pop-up was opened / driven from the keyboard while the speaker button holds focus:
    // the pointer is nowhere near it, so the hover-close must not fire (see Reevaluate…).
    private bool _volumeKeyboardHold;

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
        if (OptionsButton.Flyout is MenuFlyout optionsMenu)
        {
            optionsMenu.Opening += OnOptionsMenuOpening;
            // The rows' IsVisible bindings first resolve once the items are in the open
            // presenter: on a first open the lyrics-page group still counted as shown during
            // Opening and two separators met on the main bar. Settle again once open.
            optionsMenu.Opened += (_, _) => MenuV2.RefreshLayout(optionsMenu.Items);
        }

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

        // Volume slider: our own drag (Tunnel, ahead of the Slider's internals), as the seek bar.
        VolumeSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeSliderPressed, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(InputElement.PointerMovedEvent, OnVolumeSliderMoved, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeSliderReleased, RoutingStrategies.Tunnel);
        VolumeSlider.PointerCaptureLost += OnVolumeSliderCaptureLost;
        VolumeThumb.RenderTransform = _volumeThumbTransform;

        // Track volume changes (drag, wheel, keys, and outside writers: shortcuts, the mini
        // player, the Local API, MPRIS) to move the fill/thumb and the % readout.
        VolumeSlider.PropertyChanged += OnVolumeSliderPropertyChanged;
        VolumeSlider.SizeChanged += (_, _) => UpdateVolumeSliderVisual();
        UpdateVolumeReadout();

        // Keyboard: arrows on the focused speaker button step the volume; Esc closes the pop-up.
        // Tunnel so the keys are ours before any directional focus handling sees them.
        VolumeButton.AddHandler(InputElement.KeyDownEvent, OnVolumeButtonKeyDown, RoutingStrategies.Tunnel);
        VolumeButton.LostFocus += OnVolumeButtonLostFocus;

        // Hidden pose first, transitions after, so nothing animates at construction.
        SetVolumeFlyoutRevealed(false);
        var reveal = new CubicBezierEase(0.2, 0.8, 0.2, 1); // quick start, soft settle — both ways
        VolumeFlyoutContent.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = GlassPanel.FadeProperty, Duration = VolumeFlyoutRevealDuration, Easing = reveal },
            new Avalonia.Animation.TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = VolumeFlyoutRevealDuration, Easing = reveal },
        };
        VolumePill.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = VolumeFlyoutRevealDuration, Easing = reveal },
        };

        // GitHub #96: bar tooltips sit above their button (Placement=Top, the #52 fix) —
        // exactly where the slider popup opens, and the tooltip's native window draws over
        // the overlay-hosted slider. Hovering the icon and scrolling left "Volume" covering
        // the pill. The popup already shows what the tooltip names, so mute it while open.
        VolumeFlyout.Opened += (_, _) =>
        {
            ToolTip.SetIsOpen(VolumeButton, false);
            ToolTip.SetServiceEnabled(VolumeButton, false);
        };
        VolumeFlyout.Closed += (_, _) => ToolTip.SetServiceEnabled(VolumeButton, true);

        // Shape follows the ARRANGED width, so a window squeeze (MaxWidth clamping the
        // island) morphs the layout exactly like a user drag does.
        IslandBorder.SizeChanged += OnIslandBorderSizeChanged;
        UpdateResizeGripVisibility();

        PropertyChanged += OnPlaybackBarPropertyChanged;
        TrackTitleTextBlock.PropertyChanged += OnTrackTitleTextBlockPropertyChanged;
        TrackTitleViewport.PropertyChanged += OnTrackTitleViewportPropertyChanged;
        ArtistNameTextBlock.PropertyChanged += OnArtistNameTextBlockPropertyChanged;
        ArtistNameViewport.PropertyChanged += OnArtistNameViewportPropertyChanged;
        // Button's own PointerPressed handler marks the event handled before instance
        // handlers run, hence handledEventsToo; Click fires from inside its PointerReleased,
        // before any PointerReleased subscriber, so the name is resolved on press/move.
        ArtistNameButton.AddHandler(PointerPressedEvent, OnArtistNamePointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        ArtistNameButton.PointerMoved += OnArtistNamePointerMoved;
        ArtistNameButton.PointerExited += OnArtistNamePointerExited;
        ArtistNameButton.Click += OnArtistNameClick;
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
        UpdateVolumeMuteLook();
    }

    private void UpdateVolumeMuteLook() => UpdateVolumeSliderVisual();

    private void OnObservedPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsMuted))
            UpdateVolumeMuteLook();

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
            e.PropertyName == nameof(PlayerViewModel.IslandShowShuffle) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowEqualizer) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowRepeat) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowFavorite) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowMiniPlayer) ||
            e.PropertyName == nameof(PlayerViewModel.IslandShowTime))
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
            TrackTitleViewport.Classes.Set("overflow", false);
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
        // Loop geometry: the copy's panel sits after the hover border (text + 3px padding),
        // the badge (already inside textWidth) and its own 42px margin + 6px spacing = 48.
        _trackTitleLapDistance = textWidth + MarqueeTextPadding + MarqueeTextBlock.LoopGap;
        var shouldAnimate = vm.TrackTitleMarqueeEnabled && hasOverflow;
        // Edge fade only while the marquee owns the title: the static path ellipsizes
        // inside the viewport and never cuts a glyph.
        TrackTitleViewport.Classes.Set("overflow", shouldAnimate);
        TrackTitleLoopCopy.IsVisible = shouldAnimate;
        if (!shouldAnimate)
        {
            ApplyTrackTitleStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetTrackTitleWidth(double.NaN);

        // Keep the phase across benign re-measures; reset when asked or out of the
        // lap's valid range (-lap, 0].
        if (resetAnimation || _trackTitleOffset < -_trackTitleLapDistance || _trackTitleOffset > 0)
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
                    _trackTitleLapDistance, SetTrackTitleOffset);

            // Tick artist marquee (same speed, independent phase)
            if (artistActive)
                TickMarquee(elapsedMs, _artistNameOffset, ref _artistNamePauseRemainingMs,
                    _artistNameLapDistance, SetArtistNameOffset);
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

    /// <summary>Ticker step, matching MarqueeTextBlock: the text and its loop copy travel
    /// left together; when the copy reaches the start position the offset snaps back to
    /// zero (the original now stands exactly where the copy was, so nothing visibly moves)
    /// and the marquee rests for RestPause.</summary>
    internal static void TickMarquee(double elapsedMs, double offset, ref double pauseRemainingMs,
        double lapDistance, Action<double> setOffset)
    {
        if (pauseRemainingMs > 0)
        {
            pauseRemainingMs = Math.Max(0, pauseRemainingMs - elapsedMs);
            return;
        }

        var nextOffset = offset - TrackTitleScrollSpeed * elapsedMs / 1000.0;
        if (nextOffset <= -lapDistance)
        {
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

        // Leading-edge fade once the text has moved under the left edge (Classes.Set is
        // a no-op when unchanged, so this is free per frame).
        TrackTitleViewport.Classes.Set("scrolled", offset < -0.5);
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

    // ── Island artist link: one marquee run, one target per credited name ──
    // The credit is a single TextBlock (the ticker measures and scrolls exactly one run), so
    // the artist under the pointer is resolved from the character the pointer is over rather
    // than from separate link controls. Discord (aaron, 2026-09-23): "Kanye West, GLC,
    // Consequence" showed and behaved as a single artist.
    private string? _artistNameUnderPointer;

    private void OnArtistNamePointerPressed(object? sender, PointerPressedEventArgs e) => UpdateArtistNameUnderPointer(e);

    private void OnArtistNamePointerMoved(object? sender, PointerEventArgs e) => UpdateArtistNameUnderPointer(e);

    private void OnArtistNamePointerExited(object? sender, PointerEventArgs e)
    {
        if (_artistNameUnderPointer == null) return;
        _artistNameUnderPointer = null;
        ToolTip.SetTip(ArtistNameButton, Loc.T("PlaybackBar.ViewArtistTip"));
    }

    private void UpdateArtistNameUnderPointer(PointerEventArgs e)
    {
        var text = ArtistNameTextBlock.Text;
        var name = ArtistCreditSpans.Locate(text).Count > 1 ? ResolveArtistNameAt(text, e) : null;
        if (name == _artistNameUnderPointer) return;
        _artistNameUnderPointer = name;
        ToolTip.SetTip(ArtistNameButton, name != null
            ? Loc.T("PlaybackBar.ViewNamedArtistTip", name)
            : Loc.T("PlaybackBar.ViewArtistTip"));
    }

    /// <summary>The credited name under the pointer, from whichever of the two marquee runs
    /// (text or its loop copy) the pointer is over; null over a separator or outside both.
    /// GetPosition includes the runs' TranslateTransform, so a scrolling ticker resolves too.</summary>
    private string? ResolveArtistNameAt(string? text, PointerEventArgs e)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var run in new[] { ArtistNameTextBlock, ArtistNameLoopCopy })
        {
            if (!run.IsVisible) continue;
            var p = e.GetPosition(run);
            if (p.X < 0 || p.Y < 0 || p.X > run.Bounds.Width || p.Y > run.Bounds.Height) continue;
            var hit = run.TextLayout.HitTestPoint(p);
            return ArtistCreditSpans.NameAt(text, hit.TextPosition);
        }
        return null;
    }

    private void OnArtistNameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        // Keyboard activation and clicks on a separator fall back to the primary artist —
        // the behaviour every other artist link in the app has.
        if (_artistNameUnderPointer is { } name)
            vm.ViewArtistNamedCommand.Execute(name);
        else
            vm.ViewArtistCommand.Execute(vm.CurrentTrack);
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
        // Copy sits after the hover border (text + 3px) with a 45px margin = one 48px gap.
        _artistNameLapDistance = textWidth + MarqueeTextPadding + MarqueeTextBlock.LoopGap;
        var hasOverflow = _artistNameOverflow > TrackTitleOverflowThreshold;
        var shouldAnimate = vm.ArtistMarqueeEnabled && hasOverflow;
        ArtistNameLoopCopy.IsVisible = shouldAnimate;
        if (!shouldAnimate)
        {
            ApplyArtistNameStaticPresentation(hasOverflow, viewportWidth);
            return;
        }

        SetArtistNameWidth(double.NaN);

        // Keep the phase across benign re-measures; reset when asked or out of the
        // lap's valid range (-lap, 0].
        if (resetAnimation || _artistNameOffset < -_artistNameLapDistance || _artistNameOffset > 0)
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
        if (ArtistNameLoopCopy.RenderTransform is TranslateTransform copyTransform)
            copyTransform.X = offset;
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty)
        {
            UpdateVolumeReadout();
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
    // The art toggles the Mini Player on a completed click (press AND release over it),
    // not on press: a press that turned into a drag, or slid off the art, used to open it.
    private bool _albumArtPressed;

    private void OnAlbumArtPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _albumArtPressed = true;
        e.Handled = true;
    }

    private void OnAlbumArtReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_albumArtPressed || e.InitialPressMouseButton != MouseButton.Left) return;
        _albumArtPressed = false;
        if (sender is not Control art) return;
        var p = e.GetPosition(art);
        if (p.X < 0 || p.Y < 0 || p.X > art.Bounds.Width || p.Y > art.Bounds.Height) return;

        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            mainWindow.ToggleMiniPlayer();
            e.Handled = true;
        }
    }

    // The right cluster's mini player button (Settings → Player, on by default): the same toggle.
    private void OnMiniPlayerButtonClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            mainWindow.ToggleMiniPlayer();
    }

    // The pill's live readout. Fed from the slider (TwoWay-bound to PlayerViewModel.Volume),
    // so every writer — drag, wheel, keys, shortcuts, mini player, Local API — shows here.
    private void UpdateVolumeReadout() =>
        VolumeReadout.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";

    private void SetVolumeDragging(bool dragging)
    {
        _isVolumeDragging = dragging;
        VolumePill.Classes.Set("dragging", dragging);
    }

    /// <summary>
    /// Wheel → volume notches. A mouse notch is a delta of 1.0, but precision touchpads and
    /// free-spinning / hi-res wheels report fractions (0.1–0.25) at a high rate, and the old
    /// "any delta = ±5" rule turned one small swipe into 0→100. Fractions now accumulate to a
    /// whole notch, while the first event of a gesture (after a pause or a direction change)
    /// always moves one step, so a single slow notch that reports a fraction is never lost.
    /// Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal sealed class VolumeWheelAccumulator
    {
        internal const ulong GesturePauseMs = 250;
        // Cap per event: an accelerated burst (macOS can report 3+ lines at once) moves at
        // most two steps instead of leaping.
        private const int MaxNotchesPerEvent = 2;
        private double _pending;
        private int _lastSign;
        private ulong _lastTimestamp;

        public int Add(double delta, ulong timestamp)
        {
            if (delta == 0 || double.IsNaN(delta)) return 0;
            var sign = Math.Sign(delta);
            var fresh = _lastSign == 0 || sign != _lastSign ||
                        timestamp < _lastTimestamp || timestamp - _lastTimestamp > GesturePauseMs;
            _lastSign = sign;
            _lastTimestamp = timestamp;
            if (fresh) _pending = 0;

            _pending += delta;
            var notches = (int)Math.Truncate(_pending);
            if (notches == 0)
            {
                if (!fresh) return 0;
                _pending = 0; // the gesture's first step is free; fractions after it accumulate
                return sign;
            }
            _pending -= notches;
            return Math.Clamp(notches, -MaxNotchesPerEvent, MaxNotchesPerEvent);
        }
    }

    private void OnVolumeWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || e.Delta.Y == 0) return;
        // Handled even when the fraction has not reached a notch yet: it is still ours.
        e.Handled = true;
        var notches = _volumeWheel.Add(e.Delta.Y, e.Timestamp);
        if (notches != 0) StepVolume(vm, notches * VolumeStep);
    }

    /// <summary>Wheel and arrow keys: unmute (adjusting while muted means "let me hear it"),
    /// step within 0..100, flush to the player, and show the pop-up. Also reopens while the
    /// exit fade is running (still IsOpen, but the exit timer is about to unmap it).</summary>
    private void StepVolume(PlayerViewModel vm, int delta)
    {
        vm.UnmuteForAdjust();
        vm.Volume = Math.Clamp(vm.Volume + delta, 0, 100);
        vm.CommitVolume();
        if (!VolumeFlyout.IsOpen || _volumeFlyoutExitTimer != null) OpenVolumeFlyout();
    }

    private void OnVolumeButtonKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return; // Ctrl/⌘+↑/↓ are the global shortcuts
        int delta;
        switch (e.Key)
        {
            case Key.Enter:
                // Click follows from the button itself (opens, then mutes); just remember the
                // pop-up is keyboard-driven so it is not hover-closed out from under the user.
                _volumeKeyboardHold = true;
                return;
            case Key.Escape:
                if (!VolumeFlyout.IsOpen || _volumeFlyoutExitTimer != null) return;
                _volumeKeyboardHold = false;
                CloseVolumeFlyout();
                e.Handled = true;
                return;
            case Key.Up:
            case Key.Right:
                delta = VolumeStep;
                break;
            case Key.Down:
            case Key.Left:
                delta = -VolumeStep;
                break;
            default:
                return;
        }
        _volumeKeyboardHold = true;
        if (DataContext is PlayerViewModel vm) StepVolume(vm, delta);
        e.Handled = true;
    }

    // Focus moved on (Tab, a click elsewhere): a keyboard-held pop-up closes like a hovered one.
    private void OnVolumeButtonLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_volumeKeyboardHold) return;
        _volumeKeyboardHold = false;
        ReevaluateVolumeFlyoutHover();
    }

    private bool IsVolumeKeyboardHeld => _volumeKeyboardHold && VolumeButton.IsFocused;

    /// <summary>Test hooks (InternalsVisibleTo Noctis.Tests): a hover-close is pending / the
    /// exit fade is running.</summary>
    internal bool IsVolumeFlyoutCloseArmed => _volumeFlyoutCloseTimer != null;
    internal bool IsVolumeFlyoutExiting => _volumeFlyoutExitTimer != null;

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
            SetVolumeFlyoutRevealed(true);
        }

        SetVolumeDragging(true);
        (DataContext as PlayerViewModel)?.UnmuteForAdjust();
        e.Pointer.Capture(slider);
        slider.Value = GetVolumeFromPointer(slider, e.GetPosition(slider));
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
            SetVolumeDragging(false);
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
            SetVolumeDragging(false);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

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

        SetVolumeDragging(false);
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
            // Rest track = IslandIconFill at this strength: a soft fill like the Settings
            // pill fields (#1C white), visible on every island colour, accent only for the level.
            enabledBackgroundOpacity: 0.18,
            disabledBackgroundOpacity: 0.12);

        if (_observedPlayerViewModel?.IsMuted == true)
            ApplyVolumeMutedLayout();
    }

    /// <summary>
    /// Muted: the level stays visible (unmuting restores it) but dims, in the dimmed layout
    /// PillSliderVisualHelper gives a disabled slider — the fill stops at the thumb and the rest
    /// track starts past it, square ends tucked onto the circle — because a dimmed thumb is
    /// see-through: the overlapping enabled layout showed the fill and track through it (seen
    /// in a real-Skia render, 10-08; Opacity on the Canvas reaches each child separately). The
    /// slider itself stays enabled so dragging still unmutes. The next enabled pass restores
    /// the bars' own corners (the helper keeps them).
    /// </summary>
    private void ApplyVolumeMutedLayout()
    {
        var width = VolumeSlider.Bounds.Width;
        if (width <= 0) return;
        var radius = VolumeThumbSize / 2;
        var trackWidth = Math.Max(0, width - VolumeThumbSize);
        var range = VolumeSlider.Maximum - VolumeSlider.Minimum;
        var fraction = range <= 0 ? 0 : Math.Clamp((VolumeSlider.Value - VolumeSlider.Minimum) / range, 0, 1);
        var fillWidth = trackWidth * fraction;
        var half = Math.Min(VolumeTrackFill.Height / 2, radius);
        var tuck = radius - Math.Sqrt(radius * radius - half * half) + 0.15;

        VolumeTrackFill.Width = Math.Max(0, fillWidth - radius + tuck);
        Canvas.SetLeft(VolumeTrackFill, radius);
        VolumeTrackBackground.Width = Math.Max(0, trackWidth - fillWidth - radius + tuck);
        Canvas.SetLeft(VolumeTrackBackground, fillWidth + VolumeThumbSize - tuck);
        var r = VolumeTrackFill.Height / 2;
        VolumeTrackFill.CornerRadius = new CornerRadius(r, 0, 0, r);
        VolumeTrackBackground.CornerRadius = new CornerRadius(0, r, r, 0);

        VolumeTrackFill.Opacity = VolumeMutedStrength;
        VolumeThumb.Opacity = VolumeMutedStrength;
        VolumeTrackBackground.Opacity = 0.12;
    }

    private const double VolumeMutedStrength = 0.45;

    private static double GetVolumeFromPointer(Slider slider, Point position)
    {
        return PillSliderVisualHelper.GetValueFromPointer(slider, position, VolumeThumbSize);
    }

    // Stock width: 3 transport + the track box + 3 right icons. Repeat and the favorite
    // heart became opt-in extras when the box arrived; the box itself is kept short
    // (160px title/artist viewports) so it reads as the reference LCD, not a long bar.
    // Was 626 (192px viewports + heart + dots) — AppSettings migrates that stored value.
    private const double IslandBaseWidth = 536;
    // Lyrics page hides the center track-info, so the pill only holds transport + right icons.
    private const double IslandLyricsPageWidth = 340;

    // ── User resize (persistent bar only) ──
    // Shape thresholds derived from the clusters' natural widths as declared in the
    // XAML: transport 34 + 2 + 40 + 2 + 34 = 112, + 14 margin = 126; right icons
    // 3 × 34 + 2 × 2 spacing = 106 (the options fallback only shows in the compact
    // shape, where the box is gone); the track box 3 + 34 art + 8 + 160 viewport + 8 +
    // 22 eq + 28 dots + 3 = 266, + 8 margin = 274; island chrome 24 padding + 3 border
    // = 27. Full layout therefore needs 533px; with the viewports narrowed to 110
    // ("bar-mid") it needs 483px; transport + 4 icons alone need 295px — 340 is the
    // compact layout the lyrics page already uses. Repeat / favorite / mini player / the
    // podcast extras add ExtraTransportButtonWidth each on top (see ExtraTransportWidth).
    private const double IslandFullShapeMinWidth = 534; // below: viewports narrow to 110
    private const double IslandMidShapeMinWidth = 484;  // below: track info hidden (compact pill)
    private const double IslandMinUserWidth = IslandLyricsPageWidth;
    // Each optional island button (repeat / favorite / mini player / speed / skip back /
    // skip forward / sleep / shuffle) is a 34px button plus its row's 2px spacing.
    private const double ExtraTransportButtonWidth = 36;

    /// <summary>Width the stacked elapsed / remaining labels add to the track box when
    /// Settings → Player → Playback time is on ("12:34" at 10px + its 8px gap).</summary>
    private const double IslandTimeWidth = 44;
    // Breathing room to the host's edges, matching the 8px margins the side panels use.
    private const double IslandEdgeMargin = 8;
    private static readonly TimeSpan VolumeFlyoutCloseDelay = TimeSpan.FromMilliseconds(140);
    // One duration and one curve for show AND hide: a reversal mid-flight (close while still
    // opening, reopen during the exit) simply retargets the running transitions from wherever
    // they are, with no curve swap that could restart them from an end value.
    private static readonly TimeSpan VolumeFlyoutRevealDuration = TimeSpan.FromMilliseconds(180);
    // The exit transition plus a frame or two of slack, so the popup unmaps only after the
    // fade + drop has finished (a DispatcherTimer lands on the ~15.6 ms timer grid).
    private static readonly TimeSpan VolumeFlyoutExitDuration = TimeSpan.FromMilliseconds(200);

    // Constant literals, so no culture can format them (GitHub #79 was a culture-formatted
    // translateX). Same operation list in both, so the transition interpolates each part.
    private static readonly Avalonia.Media.Transformation.TransformOperations VolumeFlyoutShownTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px) scale(1)");
    private static readonly Avalonia.Media.Transformation.TransformOperations VolumeFlyoutHiddenTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(6px) scale(0.92)");

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
        // Reopened during the exit fade: the pop-up is still mapped and part-way down, so
        // turn it around from where it is. (This used to re-set the hidden pose first and
        // reveal a frame later, so the half-faded pill dipped further before coming back.)
        var reviving = VolumeFlyout.IsOpen;
        CancelVolumeFlyoutExit();
        if (reviving)
        {
            SetVolumeFlyoutRevealed(true);
        }
        else
        {
            // Start in the hidden pose (it already is, from construction or the last close),
            // map, and reveal on the next render pass so the transitions have a first frame.
            SetVolumeFlyoutRevealed(false);
            VolumeFlyout.IsOpen = true;
            UpdateVolumeSliderVisual();
            Dispatcher.UIThread.Post(() =>
            {
                // A close that landed before this pass owns the pose now: revealing here
                // flashed the pill back to full strength while its exit timer unmapped it.
                if (VolumeFlyout.IsOpen && _volumeFlyoutExitTimer == null)
                    SetVolumeFlyoutRevealed(true);
            }, DispatcherPriority.Render);
        }
        // The popup only ever closes off pointer-exit of the icon/popup (or the
        // post-drag reevaluation). If it was opened without the cursor anywhere near
        // — keyboard activation of the icon button fires Click too — no exit will
        // ever come, so re-check once the open settles and arm the hover-close then
        // (unless the keyboard is driving it: see IsVolumeKeyboardHeld).
        Dispatcher.UIThread.Post(() => ReevaluateVolumeFlyoutHover(), DispatcherPriority.Input);
    }

    /// <summary>Shown: full strength, resting on its anchor. Hidden: faded out, 6px lower and
    /// at 92 %, scaled about its bottom edge (toward the speaker button). The frost fades on
    /// GlassPanel.Fade, not Opacity — the GPU backend does not apply an opacity layer to the
    /// custom blur — and the content fades on its own Opacity.</summary>
    private void SetVolumeFlyoutRevealed(bool shown)
    {
        VolumeFlyoutContent.Fade = shown ? 1 : 0;
        VolumePill.Opacity = shown ? 1 : 0;
        VolumeFlyoutContent.RenderTransform = shown ? VolumeFlyoutShownTransform : VolumeFlyoutHiddenTransform;
    }

    private void CloseVolumeFlyout()
    {
        CancelVolumeFlyoutClose();
        if (!VolumeFlyout.IsOpen) return;

        // Reverse of the open animation: drop + shrink + fade out, then unmap the popup
        // once the transition has finished (setting IsOpen=false immediately would snap it shut).
        SetVolumeFlyoutRevealed(false);

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

    // Pointer leaves the icon or the popup → schedule a close.
    // A brief grace period lets the cursor cross the small gap between the icon and the popup
    // without dismissing — if it re-enters either, we cancel the pending close.
    // The pointer arriving means the mouse has taken over from the keyboard.
    private void OnVolumeButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (VolumeFlyout.IsOpen)
            ScheduleVolumeFlyoutClose();
    }

    private void OnVolumeButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        _volumeKeyboardHold = false;
        CancelVolumeFlyoutClose();
    }

    private void OnVolumeFlyoutPointerExited(object? sender, PointerEventArgs e) =>
        ScheduleVolumeFlyoutClose();

    private void OnVolumeFlyoutPointerEntered(object? sender, PointerEventArgs e)
    {
        _volumeKeyboardHold = false;
        CancelVolumeFlyoutClose();
    }

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
            // Keyboard-driven (Enter / arrows on the focused speaker button): the pointer is
            // elsewhere by definition. This used to arm the hover-close right after a keyboard
            // open, so the pop-up vanished ~140 ms after Enter. It now lasts while the button
            // keeps focus; Esc, Tab away or the mouse taking over closes it as usual.
            if (VolumeButton.IsPointerOver || VolumeFlyoutContent.IsPointerOver || IsVolumeKeyboardHeld) return;
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
        {
            IslandBorder.Width = IslandLyricsPageWidth + extra;
            // The lyrics page hosts this copy in its info column, whose width follows the
            // cover (height − 370, floored at 300) — on a short window (a 720p/768p TV,
            // or any window under ~710px tall) that column is narrower than the pill.
            // This control clips to its bounds, so being arranged at the column's width
            // chopped the pill's rounded ends straight (Discord report, 2026-09-10).
            // Carrying the pill's width as the bar's minimum lets it overflow the column
            // symmetrically (Stretch centres an oversized child) with its ends intact.
            MinWidth = IslandBorder.Width;
        }
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
                        + (vm.IslandShowShuffle ? 1 : 0)
                        + (vm.IslandShowEqualizer ? 1 : 0)
                        + (vm.IslandShowRepeat ? 1 : 0)
                        + (vm.IslandShowFavorite ? 1 : 0)
                        + (vm.IslandShowMiniPlayer ? 1 : 0);
            return buttons * ExtraTransportButtonWidth + (vm.IslandShowTime ? IslandTimeWidth : 0);
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
        // The "…" normally sits inside the track box; while the box is hidden the right
        // cluster's OptionsButton (which owns the MenuFlyout) stands in for it.
        OptionsButton.IsVisible = !visible;
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

        OptionsButton.Flyout?.ShowAt(BoxOptionsButton);
        e.Handled = true;
    }

    /// <summary>The track box's "…" opens the options MenuFlyout declared on the right
    /// cluster's OptionsButton (the compact/lyrics fallback), anchored to itself. One
    /// flyout, two anchors — the menu's bindings resolve through the shared DataContext.</summary>
    private void OnBoxOptionsClick(object? sender, RoutedEventArgs e) =>
        OptionsButton.Flyout?.ShowAt(BoxOptionsButton);

    // ── Options menu: the track menus' v2 layout (owner 10-10, "make it the same as Home") ──

    private Button? _quickPlayTile, _quickShuffleTile, _quickPlayNextTile, _quickQueueTile;

    /// <summary>
    /// Per open: puts the Play / Shuffle / Play Next / Add to Queue tiles on top (once), points
    /// them at the current track, fills View Artist with the credited names and settles the
    /// separators, as <see cref="TrackContextMenuBuilder"/> does for the other track menus.
    /// The menu stays a MenuFlyout so Liquid Glass can frost it (GlassMenuFlyout).
    /// </summary>
    private void OnOptionsMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || DataContext is not PlayerViewModel vm) return;

        if (_quickPlayTile == null)
        {
            _quickPlayTile = MenuV2.Tile(this, menu.Hide, "MenuLinePlay", Loc.T("PlaybackBar.Play"));
            _quickShuffleTile = MenuV2.Tile(this, menu.Hide, "MenuLineShuffle", Loc.T("LibraryAlbums.Shuffle"));
            _quickPlayNextTile = MenuV2.Tile(this, menu.Hide, "MenuLinePlayNext", Loc.T("LibraryAlbums.PlayNext"));
            _quickQueueTile = MenuV2.Tile(this, menu.Hide, "MenuLineQueue", Loc.T("LibraryAlbums.AddQueue"));
            menu.Items.Insert(0, MenuV2.TileRow(_quickPlayTile, _quickShuffleTile, _quickPlayNextTile, _quickQueueTile));
        }

        // Play pauses the current song while it plays: say so on the tile.
        if (vm.IsPlaying)
            MenuV2.SetTile(this, _quickPlayTile, "MenuLinePause", Loc.T("PlaybackBar.Pause"));
        else
            MenuV2.SetTile(this, _quickPlayTile, "MenuLinePlay", Loc.T("PlaybackBar.Play"));
        _quickPlayTile.Command = vm.PlayPauseCommand;
        _quickShuffleTile!.Command = vm.ShuffleCurrentAlbumCommand;
        _quickPlayNextTile!.Command = vm.PlayNextCurrentTrackCommand;
        _quickQueueTile!.Command = vm.AddCurrentTrackToQueueCommand;

        var track = vm.CurrentTrack;
        BindViewArtist(vm, track);

        LyricsBackgroundClearMenuItem.IsVisible = track != null &&
            LyricsBackgroundOverrides.HasOverride(LyricsBackgroundOverrides.KeyForTrack(track));

        OpenWithMenuItem.Header = ExternalOpenApp.MenuHeader;
        OpenWithMenuItem.IsVisible = ExternalOpenApp.IsAvailable;
        OpenWithMenuItem.Command ??= new CommunityToolkit.Mvvm.Input.RelayCommand<Noctis.Models.Track>(ExternalOpenApp.Open);

        MenuV2.RefreshLayout(menu.Items);
    }

    private void BindViewArtist(PlayerViewModel vm, Noctis.Models.Track? track)
    {
        var item = ViewArtistMenuItem;
        item.Items.Clear();
        var names = track != null ? TrackContextMenuBuilder.CreditedArtists(track) : Array.Empty<string>();
        item.IsVisible = names.Count > 0;
        var single = names.Count == 1;
        item.Command = single ? vm.ViewArtistNamedCommand : null;
        item.CommandParameter = single ? names[0] : null;
        var generation = ++_artistAvatarGeneration;
        if (names.Count < 2) return;
        // Each artist's round picture, as in the other track menus (owner 10-10).
        var photos = new CachedImage[names.Count];
        for (var i = 0; i < names.Count; i++)
            item.Items.Add(new MenuItem
            {
                Header = names[i], Command = vm.ViewArtistNamedCommand, CommandParameter = names[i],
                Icon = MenuV2.ArtistAvatar(out photos[i]),
            });
        if (TrackContextMenuBuilder.ArtistPhotoSource is { } source)
            ArtistAvatarsLoaded = LoadArtistAvatarsAsync(generation, names, photos, source);
    }

    private int _artistAvatarGeneration;

    /// <summary>The current open's artist-picture lookup (tests await it).</summary>
    internal Task ArtistAvatarsLoaded { get; private set; } = Task.CompletedTask;

    // Cache files only, off the UI thread; a later open drops this one's result.
    private async Task LoadArtistAvatarsAsync(int generation, IReadOnlyList<string> names,
        CachedImage[] photos, Func<string, string?> source)
    {
        string?[] paths;
        try
        {
            paths = await Task.Run(() => names.Select(name =>
            {
                try { return source(name); }
                catch { return null; }
            }).ToArray());
        }
        catch { return; }
        if (generation != _artistAvatarGeneration) return;
        for (var i = 0; i < photos.Length; i++)
        {
            photos[i].SourcePath = paths[i];
            photos[i].IsVisible = !string.IsNullOrEmpty(paths[i]);
        }
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




