using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsView : UserControl
{
    private int _lastScrolledIndex = -1;
    // Bumped to invalidate any in-flight frame-clock scroll animation (replaces stopping a timer).
    private int _scrollAnimationGeneration;
    private bool _isProgrammaticScroll;

    // Dev-mode event trace of the lyrics flow (Settings ▸ About ▸ Developer Mode ▸
    // Copy Logs): every line change, every scroll start with its route, every glide
    // end, every cancellation and every offset write that did not come from this
    // view. Timestamps are ms since process start, so the lines can be laid on one
    // timeline against the view model's "active a→b" entries.
    private static readonly Stopwatch TraceClock = Stopwatch.StartNew();
    internal static void Trace(string message)
    {
        if (Services.DebugLog.VlcBridgeEnabled)
            Services.DebugLog.Write("Lyrics", $"+{TraceClock.ElapsedMilliseconds}ms {message}");
    }
    // Lines currently carrying a transient cascade translate (Apple Music-style
    // staggered glide); cleared whenever the scroll animation ends or is cancelled.
    private List<(Control Control, double DelayMs)>? _cascadeLines;
    private DispatcherTimer? _autoFollowResumeTimer;
    private LyricsViewModel? _subscribedVm;

    /// <summary>True while this view is counted in the VM's visible-surface tally.</summary>
    private bool _countedAsVisible;
    private bool _swatchScrollersWired;
    private DispatcherTimer? _colorPickerDismissTimer;
    private const double ColorPickerAutoDismissSeconds = 3;
    private bool _isNarrowMode;
    private DispatcherTimer? _resizeRecenterTimer;
    private Window? _hostWindow;
    private bool _recenterOnNextLayout;
    private bool _isTimelineSeekDragging;
    private bool _isJumpingOnAttach;
    private readonly TranslateTransform _lyricsTimelineThumbTransform = new();

    private const double NarrowBreakpoint = 900;
    private const double LyricsTimelineThumbSize = 16;

    // ── Flowing-artwork background (issue #22) ──
    // One shared frame-clock animator (same math on the page, the side panel and the
    // mini player). Transforms are driven from code: a XAML KeyFrame animation on
    // RenderTransform crashes Avalonia at startup.
    private readonly FlowingArtworkAnimator _flow;

    public LyricsView()
    {
        InitializeComponent();

        // Detect manual scroll via mouse wheel to pause auto-follow
        if (LyricsScrollViewer != null)
        {
            LyricsScrollViewer.PointerWheelChanged += OnLyricsPointerWheelChanged;
            LyricsScrollViewer.PropertyChanged += OnLyricsScrollViewerPropertyChanged;
            LyricsScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }

        LyricsTimelineThumb.RenderTransform = _lyricsTimelineThumbTransform;

        _flow = new FlowingArtworkAnimator(this, FlowBackdrop, FlowLayer1, FlowLayer2, BeatGlow, GetBeatContext);
        AttachedToVisualTree += (_, _) => HookPluginLayers();
        DetachedFromVisualTree += (_, _) => UnhookPluginLayers();
        LyricsTimelineSlider.AddHandler(InputElement.PointerPressedEvent, OnTimelineSeekStart, RoutingStrategies.Tunnel);
        LyricsTimelineSlider.AddHandler(InputElement.PointerMovedEvent, OnTimelineSeekMove, RoutingStrategies.Tunnel);
        LyricsTimelineSlider.AddHandler(InputElement.PointerReleasedEvent, OnTimelineSeekEnd, RoutingStrategies.Tunnel);
        LyricsTimelineSlider.PointerCaptureLost += OnTimelineSeekCaptureLost;
        LyricsTimelineSlider.PropertyChanged += OnTimelineSliderPropertyChanged;
        LyricsTimelineSlider.SizeChanged += (_, _) => UpdateTimelineSliderVisual();
        DispatcherTimer.RunOnce(UpdateTimelineSliderVisual, TimeSpan.FromMilliseconds(10));

        // Mouse wheel → horizontal scroll for color swatch pickers.
        // The scrollers live inside a Flyout and are not realized until first open,
        // so wire them lazily on Flyout.Opened instead of at construction time.
        if (LyricsColorPickerHost?.Flyout is Avalonia.Controls.Flyout colorPickerFlyout)
        {
            colorPickerFlyout.Opened += OnColorPickerFlyoutOpened;
            colorPickerFlyout.Closed += OnColorPickerFlyoutClosed;
        }

        // After a min/maximize/restore the lyrics rewrap; re-anchor the active line on the
        // very next layout pass (guarded by the flag) so it snaps into place instead of
        // sitting at a stale offset for the ~200ms the resize-settle timer would take.
        LayoutUpdated += OnLyricsLayoutUpdated;
    }

    private void OnLyricsLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_recenterOnNextLayout) return;
        _recenterOnNextLayout = false;

        if (DataContext is not LyricsViewModel vm) return;
        if (!vm.IsSyncTabSelected || vm.IsAutoFollowPaused || vm.ActiveLineIndex < 0) return;

        _lastScrolledIndex = -1; // force the jump even if the index didn't change
        JumpToActiveLineWhenReady(vm.ActiveLineIndex);
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        // Defer to the next layout pass, when the rewrapped line heights are final.
        if (DataContext is LyricsViewModel { IsSyncTabSelected: true, IsAutoFollowPaused: false, ActiveLineIndex: >= 0 })
            _recenterOnNextLayout = true;
    }

    // Re-anchor when the user returns to the app (alt-tab back). While the window is
    // backgrounded the auto-scroll can silently go stale (the scroll work reads live
    // visual-tree geometry and is marked done before it runs), leaving the viewport on
    // a region where every line has opacity 0 until the next line change. Jump directly
    // instead of arming _recenterOnNextLayout: plain activation may not trigger a
    // layout pass, so LayoutUpdated might never fire.
    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is not LyricsViewModel vm) return;
        if (!vm.IsSyncTabSelected || vm.IsAutoFollowPaused || vm.ActiveLineIndex < 0) return;

        JumpToActiveLineWhenReady(vm.ActiveLineIndex);
    }

    private void OnColorPickerFlyoutOpened(object? sender, EventArgs e)
    {
        if (!_swatchScrollersWired)
        {
            if (SolidSwatchScroller != null)
                SolidSwatchScroller.PointerWheelChanged += OnSwatchWheelScroll;
            if (GradientSwatchScroller != null)
                GradientSwatchScroller.PointerWheelChanged += OnSwatchWheelScroll;

            // Any interaction inside the picker resets the auto-dismiss countdown so it only
            // closes once the user has stopped fiddling with it (handledEventsToo so swatch/mode
            // button clicks still count).
            if (ColorPickerContent != null)
            {
                ColorPickerContent.AddHandler(PointerPressedEvent, OnColorPickerInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
                ColorPickerContent.AddHandler(PointerMovedEvent, OnColorPickerInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
                ColorPickerContent.AddHandler(PointerWheelChangedEvent, OnColorPickerInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
            }

            _swatchScrollersWired = true;
        }

        RestartColorPickerDismissTimer();
    }

    private void OnColorPickerFlyoutClosed(object? sender, EventArgs e) => StopColorPickerDismissTimer();

    private void OnColorPickerInteraction(object? sender, RoutedEventArgs e) => RestartColorPickerDismissTimer();

    private void RestartColorPickerDismissTimer()
    {
        StopColorPickerDismissTimer();
        _colorPickerDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ColorPickerAutoDismissSeconds) };
        _colorPickerDismissTimer.Tick += (_, _) =>
        {
            StopColorPickerDismissTimer();
            LyricsColorPickerHost?.Flyout?.Hide();
        };
        _colorPickerDismissTimer.Start();
    }

    private void StopColorPickerDismissTimer()
    {
        _colorPickerDismissTimer?.Stop();
        _colorPickerDismissTimer = null;
    }

    private void OnSwatchWheelScroll(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        e.Handled = true;
        var maxX = sv.Extent.Width - sv.Viewport.Width;
        if (maxX <= 0) return;
        sv.Offset = sv.Offset.WithX(Math.Clamp(sv.Offset.X - e.Delta.Y * 60, 0, maxX));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Reset scroll guard so re-entering the page always scrolls to the active line
        _lastScrolledIndex = -1;

        // Tell the VM a lyrics surface is on screen, so the 100ms sync timer and the
        // per-frame word clock only run while something can actually display them.
        if (DataContext is LyricsViewModel attachVm)
        {
            attachVm.SetLyricsSurfaceVisible(true);
            _countedAsVisible = true;
        }

        // Watch window min/maximize/restore so we can re-anchor the active line cleanly.
        if (e.Root is Window window)
        {
            _hostWindow = window;
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
            _hostWindow.Activated += OnHostWindowActivated;
        }

        if (DataContext is LyricsViewModel vm)
        {
            // Re-subscribe on re-attach (detach unsubscribed; DataContextChanged
            // won't fire again when the DataContext is unchanged).
            SubscribeVm(vm);

            vm.IsAutoFollowPaused = false;
            _isJumpingOnAttach = true;
            try
            {
                vm.EnsureLyricsForCurrentTrack();
            }
            finally
            {
                _isJumpingOnAttach = false;
            }
            JumpToActiveLineWhenReady(vm.ActiveLineIndex);

            // Start the artwork flow if the artwork background mode is active.
            UpdateFlowAnimationState(vm);
        }

    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isTimelineSeekDragging)
        {
            _isTimelineSeekDragging = false;
            if (DataContext is LyricsViewModel vm)
                vm.Player.EndSeek();
        }

        // Full VM unsubscribe: the VM is a process singleton, so leaving handlers
        // attached would keep this cached view doing scroll/swap work for every
        // later line and track change while it sits off screen.
        UnsubscribeVm();

        // The swap events are unhooked while detached, so a swap that had faded
        // the host out could never fade back in — snap it visible for the next
        // visit (the attach path re-anchors the active line itself).
        if (_lyricsSwapInProgress)
        {
            _lyricsSwapInProgress = false;
            if (LyricsContentHost is { } swapHost)
            {
                swapHost.Transitions = null;
                swapHost.Opacity = 1.0;
            }
        }

        if (_hostWindow != null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow.Activated -= OnHostWindowActivated;
            _hostWindow = null;
        }

        if (_countedAsVisible)
        {
            _countedAsVisible = false;
            (DataContext as LyricsViewModel)?.SetLyricsSurfaceVisible(false);
        }

        // The artwork flow only makes sense while this page can be seen.
        _flow.Enabled = false;

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateResponsiveLayout(e.NewSize);

        // Re-anchor the active lyric line once the resize settles — layout
        // rewrapping changes every line's height, so the saved scroll offset
        // points somewhere else entirely after a fullscreen/restore switch.
        ScheduleActiveLineRecenter();
    }

    private void UpdateResponsiveLayout(Size size)
    {
        var width = size.Width;
        var height = size.Height;
        if (width <= 0 || height <= 0) return;

        // The grid's row/column definitions are static ("*,*" / "Auto,*").
        // Mode switches only move the panels via attached properties: mutating
        // the definition collections mid-layout-pass crashed Grid.MeasureCell
        // (children briefly referenced a column that no longer existed) when
        // the window was resized across the breakpoint.
        var shouldBeNarrow = width < NarrowBreakpoint;
        if (shouldBeNarrow != _isNarrowMode)
        {
            _isNarrowMode = shouldBeNarrow;

            if (_isNarrowMode)
            {
                // Narrow mode: header row on top, lyrics below. The full stacked
                // header (630px cover centered above the text) ate most of a
                // short window and its 320px cap used to overflow onto the
                // lyrics, so the header flips to a compact row instead: small
                // cover beside left-aligned track info, ~330px total, lyrics
                // keep the rest.
                Grid.SetColumnSpan(LeftPanel, 2);
                Grid.SetRow(LeftPanel, 0);
                Grid.SetRowSpan(LeftPanel, 1);
                LeftPanel.Padding = new Thickness(30, 20);

                Grid.SetColumn(RightPanel, 0);
                Grid.SetColumnSpan(RightPanel, 2);
                Grid.SetRow(RightPanel, 1);
                Grid.SetRowSpan(RightPanel, 1);

                Grid.SetColumnSpan(AlbumArtBorder, 1);
                AlbumArtBorder.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                AlbumArtBorder.Margin = new Thickness(0, 0, 14, 0);

                Grid.SetRow(TrackInfoStack, 0);
                Grid.SetColumn(TrackInfoStack, 1);
                Grid.SetColumnSpan(TrackInfoStack, 1);
                TrackInfoStack.Margin = default;
                TrackInfoStack.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                SetHeaderAlignment(Avalonia.Layout.HorizontalAlignment.Left);
            }
            else
            {
                // Wide mode: two equal columns spanning both rows
                Grid.SetColumnSpan(LeftPanel, 1);
                Grid.SetRow(LeftPanel, 0);
                Grid.SetRowSpan(LeftPanel, 2);
                LeftPanel.Padding = new Thickness(48, 42);

                Grid.SetColumn(RightPanel, 1);
                Grid.SetColumnSpan(RightPanel, 1);
                Grid.SetRow(RightPanel, 0);
                Grid.SetRowSpan(RightPanel, 2);

                Grid.SetColumnSpan(AlbumArtBorder, 2);
                AlbumArtBorder.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                AlbumArtBorder.Margin = default;

                Grid.SetRow(TrackInfoStack, 1);
                Grid.SetColumn(TrackInfoStack, 0);
                Grid.SetColumnSpan(TrackInfoStack, 2);
                TrackInfoStack.Margin = new Thickness(0, 24, 0, 0);
                TrackInfoStack.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                SetHeaderAlignment(Avalonia.Layout.HorizontalAlignment.Center);
            }
        }

        // Continuous sizing: derive the cover and lyric sizes from the actual
        // window dimensions instead of assuming a 1080p-class maximized window.
        // The previous fixed 780px cover + 1.1× lyric scale overflowed smaller
        // displays (MacBook-sized windows) and broke fullscreen/resize.
        double stackWidth;
        if (_isNarrowMode)
        {
            // The cover rides beside the text, so it stays small; header height
            // is dominated by the fixed rows (info/timeline/controls) either way.
            var cover = Math.Clamp(height * 0.15, 84, 128);
            AlbumArtBorder.Width = cover;
            AlbumArtBorder.Height = cover;
            // Header text needs the window's width, not the cover's: clamping
            // the stack to the cover clipped the genre · year · badge line.
            stackWidth = Math.Clamp(width - 160, 280, 520);
            LyricsItemsControl.MaxWidth = Math.Max(240, width - 80);
            RightPanel.RenderTransform = null;
        }
        else
        {
            // Left column is half the window minus panel padding; vertically
            // reserve room for track info, timeline, and playback controls,
            // plus enough slack that the block never presses against the
            // screen edges in fullscreen.
            var maxByWidth = width / 2 - 90;
            var maxByHeight = height - 370;
            var cover = Math.Clamp(Math.Min(maxByWidth, maxByHeight), 220, 780);
            AlbumArtBorder.Width = cover;
            AlbumArtBorder.Height = cover;
            stackWidth = Math.Max(cover, 300);
            LyricsItemsControl.MaxWidth = Math.Clamp(width / 2 - 120, 280, 620);
            RightPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)");
        }

        LeftContentStack.Width = stackWidth;

        // Track title/artist/album marquees must not run wider than the stack
        // (in narrow mode, than the text column beside the cover).
        var marqueeMax = _isNarrowMode
            ? Math.Max(160, stackWidth - AlbumArtBorder.Width - 54)
            : Math.Min(520, Math.Max(180, stackWidth - 40));
        TitleMarquee.MaxDisplayWidth = marqueeMax;
        ArtistMarquee.MaxDisplayWidth = marqueeMax;
        AlbumMarquee.MaxDisplayWidth = marqueeMax;

        // Lyric text: 46px suits a ~1000px-tall window; scale down with the
        // window so lines don't wrap into a wall of text on small displays.
        // Inherited by the line/karaoke TextBlocks in the item template.
        var fontScale = Math.Clamp(Math.Min(height / 1000.0, width / 1700.0), 0.55, 1.0);
        LyricsItemsControl.FontSize = Math.Round(46 * fontScale);
    }

    /// <summary>Aligns the header text block (title/artist/album/metadata) for the
    /// active mode: centered under the cover in wide, left beside it in narrow.</summary>
    private void SetHeaderAlignment(Avalonia.Layout.HorizontalAlignment alignment)
    {
        TrackInfoStack.HorizontalAlignment = alignment;
        TitleMarquee.HorizontalAlignment = alignment;
        ArtistLinkButton.HorizontalAlignment = alignment;
        AlbumLinkButton.HorizontalAlignment = alignment;
        MetadataRow.HorizontalAlignment = alignment;
    }

    /// <summary>Debounced jump back to the active lyric line after a resize settles.</summary>
    private void ScheduleActiveLineRecenter()
    {
        _resizeRecenterTimer?.Stop();
        _resizeRecenterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeRecenterTimer.Tick += (_, _) =>
        {
            _resizeRecenterTimer?.Stop();
            _resizeRecenterTimer = null;

            if (DataContext is not LyricsViewModel vm) return;
            if (!vm.IsSyncTabSelected || vm.IsAutoFollowPaused) return;
            if (vm.ActiveLineIndex < 0) return;

            _lastScrolledIndex = -1; // force the jump even if the index didn't change
            JumpToActiveLineWhenReady(vm.ActiveLineIndex);
        };
        _resizeRecenterTimer.Start();
    }

    // ── ViewModel subscription + scroll animation ──

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unsubscribe from previous ViewModel
        UnsubscribeVm();

        // Reset scroll state
        _lastScrolledIndex = -1;
        CancelScrollAnimation();
        CancelAutoFollowResumeTimer();

        if (DataContext is LyricsViewModel vm)
            SubscribeVm(vm);
    }

    /// <summary>
    /// The ONE canonical subscription set for this view, used by all three lifecycle
    /// hooks (DataContextChanged, attach, detach). Keeping a single set is what
    /// guarantees Player.PropertyChanged is live on the first visit and that the
    /// swap events don't keep firing into a detached (cached) view.
    /// </summary>
    private void SubscribeVm(LyricsViewModel vm)
    {
        if (_subscribedVm != null) return;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.OpenBackgroundColorRequested += OnOpenBackgroundColorRequested;
        vm.LyricsSwapPending += OnLyricsSwapPending;
        vm.LyricsSwapped += OnLyricsSwapped;
        // The Settings toggle for the flowing light lives on the Player VM
        // (same live channel as the marquee flags) — watch it so flipping the
        // switch while this page is open starts/stops the drift immediately.
        vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        vm.Player.Seeked += OnPlayerSeeked;
        _subscribedVm = vm;
    }

    private void UnsubscribeVm()
    {
        if (_subscribedVm == null) return;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
        _subscribedVm.LyricsSwapPending -= OnLyricsSwapPending;
        _subscribedVm.LyricsSwapped -= OnLyricsSwapped;
        _subscribedVm.Player.PropertyChanged -= OnPlayerPropertyChanged;
        _subscribedVm.Player.Seeked -= OnPlayerSeeked;
        _subscribedVm = null;
    }

    // ── Track-change lyric swap: fade out, swap while hidden, fade back ──
    // The swap rebuilds every line control in one frame; doing it behind the
    // fade is what makes a track change read as a transition, not a flicker.

    private bool _lyricsSwapInProgress;

    private void OnLyricsSwapPending(object? sender, EventArgs e)
    {
        // Subscribed-but-not-yet-attached window (first creation): don't animate
        // a tree that isn't on screen.
        if (this.GetVisualRoot() == null) return;
        _lyricsSwapInProgress = true;
        FadeLyricsHost(0.0, LyricsViewModel.LyricsSwapFadeOutMs);
    }

    private void OnLyricsSwapped(object? sender, EventArgs e)
    {
        if (this.GetVisualRoot() == null) return;
        // Re-anchor from scratch while still invisible: a glide from the old
        // track's offset is meaningless on new content, so jump.
        if (DataContext is LyricsViewModel vm)
        {
            _lastScrolledIndex = -1;
            CancelScrollAnimation();
            CancelAutoFollowResumeTimer();
            vm.IsAutoFollowPaused = false;

            var index = vm.IsSyncTabSelected ? vm.ActiveLineIndex : -1;
            if (index >= 0)
                JumpToActiveLineWhenReady(index);
            else if (LyricsScrollViewer != null)
                ApplyScrollOffset(LyricsScrollViewer, 0);
        }
        // Cleared only after the block above: the IsAutoFollowPaused reset re-enters
        // OnViewModelPropertyChanged, whose resume re-anchor must see the swap still
        // in progress and stand down — the jump owns anchoring here.
        _lyricsSwapInProgress = false;
        FadeLyricsHost(1.0, 240);
    }

    private void FadeLyricsHost(double to, int durationMs)
    {
        if (LyricsContentHost is not { } host) return;
        host.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Easing = new Avalonia.Animation.Easings.CubicEaseInOut(),
            },
        };
        host.Opacity = to;
    }

    private void OnOpenBackgroundColorRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LyricsColorPickerHost?.Flyout?.ShowAt(LyricsColorPickerHost);
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            if (sender is LyricsViewModel vm)
            {
                if (_isJumpingOnAttach)
                    return;

                // Mid-swap index churn must not start an animated glide — the
                // OnLyricsSwapped jump anchors the new track while hidden.
                if (_lyricsSwapInProgress)
                    return;

                // Plain mode: no active-line tracking — keep the list still.
                if (!vm.IsSyncTabSelected)
                {
                    _lastScrolledIndex = -1;
                    CancelScrollAnimation();
                    CancelAutoFollowResumeTimer();
                    return;
                }

                if (vm.ActiveLineIndex >= 0)
                    ScrollToActiveLine(vm.ActiveLineIndex);
                else
                {
                    _lastScrolledIndex = -1;
                    CancelScrollAnimation();
                    CancelAutoFollowResumeTimer();
                }
            }
        }
        else if (e.PropertyName == nameof(LyricsViewModel.IsSyncTabSelected))
        {
            // Mid-swap tab flips (AutoSelectTab inside the apply) are anchored by
            // the OnLyricsSwapped jump — don't start an animated glide here.
            if (_lyricsSwapInProgress)
                return;

            // Switching modes: keep the visible position aligned with the current playback
            // line on both directions, so Plain doesn't restart at the top and Sync doesn't
            // show a blank window (LyricLines outside ±9 of active have opacity 0).
            if (sender is LyricsViewModel vm2)
            {
                _lastScrolledIndex = -1;
                CancelScrollAnimation();
                CancelAutoFollowResumeTimer();
                vm2.IsAutoFollowPaused = false;

                var targetIndex = vm2.IsSyncTabSelected
                    ? vm2.ActiveLineIndex
                    : MapSyncedToUnsyncedIndex(vm2);

                if (targetIndex >= 0)
                    ScrollToActiveLine(targetIndex);
                else if (LyricsScrollViewer != null)
                    ApplyScrollOffset(LyricsScrollViewer, 0);
            }
        }
        else if (e.PropertyName == nameof(LyricsViewModel.IsLyricsFocusActive))
        {
            // Focus mode moves the anchor (22% ↔ 45%): re-pad now, then re-anchor on
            // the layout pass the margin change triggers — same guarded path as a
            // min/maximize/restore. If auto-follow is paused, only the padding moves.
            UpdateLyricsCenterPadding();
            if (DataContext is LyricsViewModel { IsSyncTabSelected: true, IsAutoFollowPaused: false, ActiveLineIndex: >= 0 })
                _recenterOnNextLayout = true;
        }
        else if (e.PropertyName == nameof(LyricsViewModel.IsAutoFollowPaused))
        {
            // Resume (Follow button, the 5s auto-resume, or a committed seek): re-anchor
            // now. Waiting for the next line change left the page parked — after a far
            // seek every visible line sits outside the ±9 dim window at opacity 0, so
            // the lyrics looked gone until the next line boundary.
            if (sender is LyricsViewModel resumed && !resumed.IsAutoFollowPaused
                && !_lyricsSwapInProgress && !_isJumpingOnAttach)
            {
                CancelAutoFollowResumeTimer();
                if (resumed.IsSyncTabSelected && resumed.ActiveLineIndex >= 0)
                {
                    _lastScrolledIndex = -1;
                    ScrollToActiveLine(resumed.ActiveLineIndex);
                }
            }
        }
        else if (sender is LyricsViewModel v)
        {
            SyncAdaptiveResource(e.PropertyName, v);
        }
    }

    private void SyncAdaptiveResource(string? prop, LyricsViewModel vm)
    {
        switch (prop)
        {
            case nameof(LyricsViewModel.LyricsBtnBg):
                SetResourceBrush("LyricsBtnBgRes", vm.LyricsBtnBg);
                SetResourceBrush("LyricsSecBtnBgRes", vm.LyricsBtnBg);
                break;
            case nameof(LyricsViewModel.LyricsBtnBgHover):
                SetResourceBrush("LyricsBtnBgHoverRes", vm.LyricsBtnBgHover);
                SetResourceBrush("LyricsSecBtnBgHoverRes", vm.LyricsBtnBgHover);
                break;
            case nameof(LyricsViewModel.IsColorModeArtwork):
                UpdateFlowAnimationState(vm);
                break;
        }
    }

    // ── Flowing-artwork background ──
    // See FlowingArtworkAnimator / FlowingArtworkMotion: the blurred cover drifts and
    // breathes on the beat, only while this page is attached, the Artwork background
    // mode is on and the Settings toggle is on.

    // A video/GIF background covers the artwork stack entirely, so its drift loop is
    // parked too — no point animating layers nobody can see.
    private void UpdateFlowAnimationState(LyricsViewModel vm)
        => _flow.Enabled = vm.IsColorModeArtwork
                           && vm.Player.LyricsFlowingLightEnabled
                           && string.IsNullOrEmpty(vm.Player.LyricsBackgroundMediaPath);

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The layers' visibility is bound in XAML; this only parks/resumes the loop.
        if (e.PropertyName is nameof(PlayerViewModel.LyricsFlowingLightEnabled)
                or nameof(PlayerViewModel.LyricsBackgroundMediaPath) &&
            DataContext is LyricsViewModel vm)
            UpdateFlowAnimationState(vm);
    }

    // ── Plugin visual layers ──
    // Each running plugin's IVisualLayerProvider gets one control in PluginLayerHost while
    // this page is attached; the host's VisualLayersChanged (enable/disable/reload) rebuilds
    // them. A misbehaving plugin layer is skipped, never allowed to break the page.
    private Services.Plugins.PluginHost? _pluginHost;

    private void HookPluginLayers()
    {
        _pluginHost = App.Services?.GetService<MainWindowViewModel>()?.Plugins;
        if (_pluginHost is null) return;
        _pluginHost.VisualLayersChanged += OnPluginLayersChanged;
        RebuildPluginLayers();
    }

    private void UnhookPluginLayers()
    {
        if (_pluginHost is not null) _pluginHost.VisualLayersChanged -= OnPluginLayersChanged;
        _pluginHost = null;
        PluginLayerHost.Children.Clear();
    }

    private void OnPluginLayersChanged(object? sender, EventArgs e) => RebuildPluginLayers();

    private void RebuildPluginLayers()
    {
        PluginLayerHost.Children.Clear();
        if (_pluginHost is null) return;
        foreach (var provider in _pluginHost.VisualLayers)
        {
            try
            {
                var layer = provider.CreateLayer();
                if (layer is not null) PluginLayerHost.Children.Add(layer);
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"layer '{provider.Name}' failed: {ex.Message}");
            }
        }
    }

    private BeatContext GetBeatContext()
    {
        if (DataContext is not LyricsViewModel { Player: { } player }) return default;
        return new BeatContext(player.CurrentTrack?.Bpm ?? 0, player.Position.TotalMilliseconds, player.IsPlaying);
    }

    private void SetResourceBrush(string key, IBrush brush)
    {
        if (brush is SolidColorBrush scb && Resources[key] is SolidColorBrush existing)
            existing.Color = scb.Color;
    }

    private void OnTimelineSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty ||
            e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdateTimelineSliderVisual();
        }
    }

    private void UpdateTimelineSliderVisual()
    {
        if (LyricsTimelineSlider == null ||
            LyricsTimelineTrackBackground == null ||
            LyricsTimelineTrackFill == null ||
            LyricsTimelineThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            LyricsTimelineSlider,
            LyricsTimelineTrackBackground,
            LyricsTimelineTrackFill,
            LyricsTimelineThumb,
            _lyricsTimelineThumbTransform,
            LyricsTimelineThumbSize,
            enabledBackgroundOpacity: 0.55,
            disabledBackgroundOpacity: 0.25);
    }

    private void OnTimelineSeekStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LyricsViewModel vm || sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isTimelineSeekDragging = true;
        vm.Player.BeginSeek();
        e.Pointer.Capture(slider);
        slider.Value = GetTimelineValueFromPointer(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void OnTimelineSeekMove(object? sender, PointerEventArgs e)
    {
        if (!_isTimelineSeekDragging || sender is not Slider slider) return;

        slider.Value = GetTimelineValueFromPointer(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void OnTimelineSeekEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isTimelineSeekDragging) return;

        _isTimelineSeekDragging = false;
        e.Pointer.Capture(null);

        if (DataContext is LyricsViewModel vm)
            vm.Player.EndSeek();

        e.Handled = true;
    }

    private void OnTimelineSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isTimelineSeekDragging) return;

        _isTimelineSeekDragging = false;
        if (DataContext is LyricsViewModel vm)
            vm.Player.EndSeek();
    }

    // Committed seeks from any surface (this page's timeline, the player bar, a
    // lyric-line click) mark the moment; ScrollToActiveLine routes the resulting
    // line change to the chase instead of the glide.
    private void OnPlayerSeeked(object? sender, TimeSpan target)
    {
        _lastSeekCommitTicks = Stopwatch.GetTimestamp();
    }

    private static double GetTimelineValueFromPointer(Slider slider, Point position)
    {
        return PillSliderVisualHelper.GetValueFromPointer(slider, position, LyricsTimelineThumbSize);
    }

    private void CancelScrollAnimation()
    {
        if (_isProgrammaticScroll || _chaseRunning)
            Trace($"cancel {(_chaseRunning ? "chase" : "glide")} in flight at offset {LyricsScrollViewer?.Offset.Y:0}");
        _scrollAnimationGeneration++;
        _isProgrammaticScroll = false;
        _chaseRunning = false;
        ClearCascadeTransforms();
    }

    // ── Scrub chase ──
    // Dragging the timeline delivers a new active line every frame or two. The eased
    // glide below cannot follow that: each update cancelled the previous animation about
    // two milliseconds in, and smootherstep opens at essentially zero velocity, so the
    // list did not move at all until the drag stopped — in either direction. Restarting
    // faster cannot fix it; the target has to be retargeted without restarting the curve.
    // This is the same exponential chase SmoothScrollBehavior uses for the wheel: a new
    // target only moves the goalpost, so the existing velocity carries straight over.
    private const double ScrubWindowMs = 250;   // updates closer together than this = a drag
    private const double ScrubSettleMs = 380;   // time to ~99% of a stationary target
    // A committed seek re-anchors with the chase, never the eased glide: smootherstep
    // opens at near-zero velocity, so after an explicit jump the page sat visibly still
    // for ~250-300ms before moving — and whether a click even got the chase used to
    // depend on the request race above, timing the user cannot see. The window covers
    // the commit debounce (60ms, posted) plus a 100ms sync tick, with margin.
    private const double SeekFollowWindowMs = 400;
    private long _lastSeekCommitTicks;
    private long _lastScrollRequestTicks;
    private double _chaseTargetY;
    private double _chaseY;
    private bool _chaseRunning;
    private long _chaseLastFrameTicks;

    private void ChaseTo(ScrollViewer scrollViewer, double targetY)
    {
        _chaseTargetY = targetY;

        if (!_chaseRunning)
        {
            // Pick the chase up from wherever the offset actually is, so a glide that was
            // in flight hands over without a jump.
            _chaseY = scrollViewer.Offset.Y;
            Trace($"chase start {_chaseY:0}→{targetY:0}");
            _chaseLastFrameTicks = Stopwatch.GetTimestamp();
            _chaseRunning = true;
            _isProgrammaticScroll = true;
            RequestChaseFrame();
        }
    }

    private void RequestChaseFrame()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            // No frame clock (detached mid-drag) — land on the target rather than strand
            // the offset part-way.
            if (LyricsScrollViewer != null)
                ApplyScrollOffset(LyricsScrollViewer, _chaseTargetY);
            _chaseRunning = false;
            _isProgrammaticScroll = false;
            return;
        }
        topLevel.RequestAnimationFrame(ChaseFrame);
    }

    private void ChaseFrame(TimeSpan _)
    {
        if (!_chaseRunning) return;

        var scrollViewer = LyricsScrollViewer;
        if (scrollViewer == null)
        {
            _chaseRunning = false;
            _isProgrammaticScroll = false;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        // Clamped so a stalled UI thread cannot produce one huge jump.
        var dt = Math.Min((now - _chaseLastFrameTicks) / (double)Stopwatch.Frequency, 0.1);
        _chaseLastFrameTicks = now;

        // Extent grows as lines re-wrap, so re-clamp every frame.
        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var target = Math.Clamp(_chaseTargetY, 0, maxY);

        _chaseY += (target - _chaseY) * (1 - Math.Exp(-dt / (ScrubSettleMs / 4600.0)));

        if (Math.Abs(target - _chaseY) < 0.5)
        {
            _chaseY = target;
            ApplyScrollOffset(scrollViewer, target);
            _chaseRunning = false;
            _isProgrammaticScroll = false;
            return;
        }

        ApplyScrollOffset(scrollViewer, _chaseY);
        RequestChaseFrame();
    }

    // The page's anchor math runs in the scroll viewer's extent space, which includes
    // LyricsItemsControl's top margin (UpdateLyricsCenterPadding) — so it cannot reuse
    // LyricsScrollAnchor.AnchorRatio (0.22, the panel's margin-exclusive space) directly:
    // with the normal 10% top margin, 0.32 in extent space IS that shipped geometry
    // (0.10 + 0.22), offsets and clamps identical.
    private const double PageAnchorRatio = 0.32;

    // Fullscreen focus dims everything but the active ±2 lines, so the active line sits
    // at the optical center: geometric 50% reads slightly low to the eye, so the anchor
    // rides a touch above it (user-tuned). The focus margins (ratio-coupled below) keep
    // the first and last lines scrollable all the way to that anchor.
    private const double FocusAnchorRatio = 0.47;

    /// <summary>Anchor ratio in effect: optically centered while fullscreen focus
    /// dimming is on, the page default otherwise. Every scroll target (glide, scrub
    /// chase, instant jump) and the center padding follow this same ratio.</summary>
    private double ActiveAnchorRatio =>
        DataContext is LyricsViewModel { IsLyricsFocusActive: true }
            ? FocusAnchorRatio
            : PageAnchorRatio;

    /// <summary>Anchor offset for a line, or null if the item containers are not laid out
    /// yet. Shared by the eased glide and the scrub chase.</summary>
    private double? TryComputeAnchorOffset(int index)
    {
        if (LyricsItemsControl == null || LyricsScrollViewer == null) return null;
        if (index < 0 || index >= LyricsItemsControl.ItemCount) return null;

        var presenter = LyricsItemsControl.GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault();
        if (presenter?.GetVisualChildren().FirstOrDefault() is not Panel panel) return null;
        if (index >= panel.Children.Count) return null;

        var targetChild = panel.Children[index];
        var childBounds = targetChild.TransformToVisual(panel);
        if (childBounds == null) return null;

        // Panel coordinates exclude LyricsItemsControl's margin, but the anchor math
        // runs in the scroll viewer's offset/extent space, which includes it.
        var childTop = childBounds.Value.Transform(new Point(0, 0)).Y
                       + LyricsItemsControl.Margin.Top;
        return Helpers.LyricsScrollAnchor.ComputeAnchorOffset(
            childTop, targetChild.Bounds.Height,
            LyricsScrollViewer.Viewport.Height,
            LyricsScrollViewer.Extent.Height,
            ActiveAnchorRatio);
    }

    private void ClearCascadeTransforms()
    {
        if (_cascadeLines == null) return;
        foreach (var (control, _) in _cascadeLines)
        {
            if (control.RenderTransform is TranslateTransform tt)
                tt.Y = 0;
        }
        _cascadeLines = null;
    }

    private void CancelAutoFollowResumeTimer()
    {
        _autoFollowResumeTimer?.Stop();
        _autoFollowResumeTimer = null;
    }

    // Every scroll this view performs itself goes through ApplyScrollOffset, which records
    // the value it wrote. Anything that lands in Offset without matching it came from the
    // user — a scrollbar drag, a touch pan, PageDown — and pauses auto-follow. The pause
    // used to hang off PointerWheelChanged alone, so those three did nothing and the next
    // line change simply yanked the view back with no "Follow" button to explain it.
    private double _lastAppliedScrollY = double.NaN;

    private void ApplyScrollOffset(ScrollViewer scrollViewer, double y)
    {
        _lastAppliedScrollY = y;
        scrollViewer.Offset = new Vector(0, y);
    }

    private void OnLyricsScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty) return;
        if (_isProgrammaticScroll) return;
        if (e.NewValue is not Vector v) return;

        // Nothing has been scrolled by us yet — this is the first layout pass, not a user
        // gesture. Adopt it as the baseline.
        if (double.IsNaN(_lastAppliedScrollY))
        {
            _lastAppliedScrollY = v.Y;
            return;
        }

        // 1px tolerance: layout can round the offset we asked for.
        if (Math.Abs(v.Y - _lastAppliedScrollY) > 1.0)
        {
            Trace($"offset moved externally {_lastAppliedScrollY:0}→{v.Y:0} — pausing auto-follow");
            PauseAutoFollow();
        }
    }

    private void OnLyricsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_isProgrammaticScroll)
            PauseAutoFollow();
    }

    private void PauseAutoFollow()
    {
        if (DataContext is not LyricsViewModel vm) return;

        vm.IsAutoFollowPaused = true;

        // Auto-resume after 5 seconds
        CancelAutoFollowResumeTimer();
        _autoFollowResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoFollowResumeTimer.Tick += (_, _) =>
        {
            CancelAutoFollowResumeTimer();
            if (DataContext is LyricsViewModel v)
                v.IsAutoFollowPaused = false;
        };
        _autoFollowResumeTimer.Start();
    }

    // ── Center-anchored lyrics padding ──

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty || e.Property == ScrollViewer.BoundsProperty)
            UpdateLyricsCenterPadding();
    }

    private void UpdateLyricsCenterPadding()
    {
        if (LyricsScrollViewer == null || LyricsItemsControl == null || LyricsScrollContent == null) return;

        // Normal: top margin = 10% so lyrics start near the top of the center zone,
        // bottom = 78% so the last lyric can still be scrolled to the page anchor.
        // Fullscreen focus: everything outside the active ±2 window is invisible, so
        // both margins follow the focus anchor instead — an anchor-sized top margin
        // lets the first line sit AT the anchor and the complementary bottom margin
        // lets the last line scroll up to it.
        var viewportHeight = LyricsScrollViewer.Viewport.Height;
        if (viewportHeight <= 0) return;

        double topPad, bottomPad;
        if (DataContext is LyricsViewModel { IsLyricsFocusActive: true })
        {
            topPad = viewportHeight * FocusAnchorRatio;
            bottomPad = viewportHeight * (1 - FocusAnchorRatio);
        }
        else
        {
            topPad = viewportHeight * 0.10;
            bottomPad = viewportHeight * 0.78;
        }
        // Right margin reserves an overflow zone for the active line's 1.07× scale
        // transform. Without it, scaled glyphs on long lines get clipped by the
        // ScrollViewer's internal viewport ("…GOA" instead of "…GOAT").
        const double activeLineScaleHeadroom = 64;
        // Top/right pads stay on the ItemsControl — the anchor math reads Margin.Top —
        // but the bottom run-out moved to the scroll-content wrapper so the Written-By
        // footer (the wrapper's second child) sits just under the last line instead of
        // a viewport below it. Total extent is unchanged while the footer is collapsed.
        LyricsItemsControl.Margin = new Thickness(0, topPad, activeLineScaleHeadroom, 0);
        LyricsScrollContent.Margin = new Thickness(0, 0, 0, bottomPad);
    }

    // Maps the current synced ActiveLineIndex to the corresponding row in UnsyncedLines.
    // UnsyncedLines mirrors LyricLines minus the optional "..." intro placeholder at index 0.
    private static int MapSyncedToUnsyncedIndex(LyricsViewModel vm)
    {
        if (vm.ActiveLineIndex < 0 || vm.LyricLines.Count == 0 || vm.UnsyncedLines.Count == 0)
            return -1;
        var hasIntro = vm.LyricLines[0].Text == "...";
        var idx = vm.ActiveLineIndex - (hasIntro ? 1 : 0);
        if (idx < 0) idx = 0;
        if (idx >= vm.UnsyncedLines.Count) idx = vm.UnsyncedLines.Count - 1;
        return idx;
    }

    private void ScrollToActiveLine(int index)
    {
        if (index == _lastScrolledIndex) return;
        _lastScrolledIndex = index;

        if (DataContext is LyricsViewModel vm && vm.IsAutoFollowPaused)
        {
            Trace($"scroll→{index} skipped: auto-follow paused");
            return;
        }

        // Updates arriving on top of each other mean the position is being dragged, not
        // advancing with playback. Those go to the chase, which retargets instead of
        // restarting; a normal line advance is seconds apart and keeps the eased glide.
        // A line change landing right after a committed seek is that seek resolving:
        // chase as well, so a discrete click always answers with the same fast settle.
        var now = Stopwatch.GetTimestamp();
        var sinceLastMs = _lastScrollRequestTicks == 0
            ? double.MaxValue
            : (now - _lastScrollRequestTicks) * 1000.0 / Stopwatch.Frequency;
        _lastScrollRequestTicks = now;

        var sinceSeekMs = _lastSeekCommitTicks == 0
            ? double.MaxValue
            : (now - _lastSeekCommitTicks) * 1000.0 / Stopwatch.Frequency;

        if ((sinceLastMs < ScrubWindowMs || sinceSeekMs < SeekFollowWindowMs) && LyricsScrollViewer != null)
        {
            // No settle delay here: a scrub only changes WHICH line is active, so the
            // item layout the offset is measured against has not moved.
            if (TryComputeAnchorOffset(index) is { } scrubTarget)
            {
                Trace($"scroll→{index} chase to {scrubTarget:0} (sinceLast {sinceLastMs:0}ms, sinceSeek {sinceSeekMs:0}ms)");
                _scrollAnimationGeneration++;   // supersede the eased glide, keep the chase
                ClearCascadeTransforms();
                ChaseTo(LyricsScrollViewer, scrubTarget);
                return;
            }
        }

        CancelScrollAnimation();

        // Start the glide NOW, on the same frame the line's scale/opacity transitions
        // start. It used to wait on a 10ms DispatcherTimer "for layout to settle" —
        // a relic of the era when activation swapped in a differently-wrapped
        // TextBlock. Lines are always word-laid-out today, so activation changes no
        // layout; and on Windows a 10ms timer fires on the ~15.6ms timer tick, so the
        // list held still for a frame or two after the line had brightened and begun
        // to grow, then set off on the curve's acceleration — a hesitation at the
        // start of every flow. (Mounts and track swaps, where layout IS pending, go
        // through JumpToActiveLineWhenReady, not here.)
        try
        {
            if (LyricsItemsControl == null || index >= LyricsItemsControl.ItemCount) return;

            var presenter = LyricsItemsControl.GetVisualDescendants()
                .OfType<ItemsPresenter>()
                .FirstOrDefault();
            if (presenter == null) return;

            var panel = presenter.GetVisualChildren().FirstOrDefault() as Panel;
            if (panel == null || index >= panel.Children.Count) return;

            var targetChild = panel.Children[index];
            if (LyricsScrollViewer == null) return;

            var childBounds = targetChild.TransformToVisual(panel);
            if (childBounds == null) return;

            // Extent space: panel coordinates + the ItemsControl top margin
            // (see TryComputeAnchorOffset).
            var childTop = childBounds.Value.Transform(new Point(0, 0)).Y
                           + LyricsItemsControl.Margin.Top;
            var childHeight = targetChild.Bounds.Height;

            var targetOffset = Helpers.LyricsScrollAnchor.ComputeAnchorOffset(
                childTop, childHeight,
                LyricsScrollViewer.Viewport.Height,
                LyricsScrollViewer.Extent.Height,
                ActiveAnchorRatio);

            var currentOffset = LyricsScrollViewer.Offset.Y;
            var diff = targetOffset - currentOffset;

            if (Math.Abs(diff) < 2)
            {
                Trace($"scroll→{index} snap {currentOffset:0}→{targetOffset:0}");
                ApplyScrollOffset(LyricsScrollViewer, targetOffset);
                return;
            }

            // A line-to-line advance runs exactly LineMotion.DurationMs so it lands
            // together with the line's own scale transition; only seek-sized jumps
            // stretch beyond it.
            var distance = Math.Abs(diff);
            var durationMs = (int)Math.Min(1050, Math.Max(LineMotion.DurationMs, distance * 0.85));
            Trace($"scroll→{index} glide {currentOffset:0}→{targetOffset:0} ({diff:+0;-0}px) {durationMs}ms");
            AnimateScroll(LyricsScrollViewer, currentOffset, targetOffset, durationMs, panel, index);
        }
        catch { }
    }

    private void JumpToActiveLineWhenReady(int index)
    {
        if (index < 0)
            return;

        Trace($"jump→{index}");
        _lastScrolledIndex = index;
        CancelScrollAnimation();

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (LyricsItemsControl == null || index >= LyricsItemsControl.ItemCount) return;

                var presenter = LyricsItemsControl.GetVisualDescendants()
                    .OfType<ItemsPresenter>()
                    .FirstOrDefault();
                if (presenter == null) return;

                var panel = presenter.GetVisualChildren().FirstOrDefault() as Panel;
                if (panel == null || index >= panel.Children.Count) return;

                var targetChild = panel.Children[index];
                if (LyricsScrollViewer == null) return;

                var childBounds = targetChild.TransformToVisual(panel);
                if (childBounds == null) return;

                // Extent space: panel coordinates + the ItemsControl top margin
                // (see TryComputeAnchorOffset).
                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y
                               + LyricsItemsControl.Margin.Top;
                var childHeight = targetChild.Bounds.Height;

                var targetOffset = Helpers.LyricsScrollAnchor.ComputeAnchorOffset(
                    childTop, childHeight,
                    LyricsScrollViewer.Viewport.Height,
                    LyricsScrollViewer.Extent.Height,
                    ActiveAnchorRatio);
                ApplyScrollOffset(LyricsScrollViewer, targetOffset);
            }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    // Cascade tuning: each line below the active one starts its glide this much later,
    // with the stagger depth capped — the Apple Music "settle top-down" feel.
    private const double CascadeDelayPerLineMs = 35;
    private const int CascadeMaxLines = 8;
    // A line's lag may exceed the line above's by at most this much — see the lyrics
    // panel's constant of the same name (uncapped stagger made lines overlap mid-glide).
    private const double MaxCascadeLagStepPx = 16;

    /// <summary>
    /// Frame-clock scroll animation: each step is scheduled via TopLevel.RequestAnimationFrame,
    /// so movement is vsync-locked instead of a 16ms DispatcherTimer beating against the
    /// compositor's ~16.7ms frame interval (which dropped/doubled a frame about once a second).
    /// Uses smootherstep easing so lyric movement glides in and out instead of jumping.
    /// When the lines panel and active index are supplied, lines below the active line
    /// lag the base glide with a per-line stagger (transient translate that relaxes to
    /// zero), so the stack settles top-down instead of moving as one rigid slab.
    /// </summary>
    private void AnimateScroll(ScrollViewer scrollViewer, double from, double to, int durationMs,
        Panel? linesPanel = null, int activeIndex = -1)
    {
        CancelScrollAnimation();
        _isProgrammaticScroll = true;

        var delta = to - from;

        // The stagger is a SETTLING effect for line-to-line advance. Over a seek-sized
        // jump it is not readable at all, and it is the one place the two scroll
        // directions cost different amounts: membership is "every line below the active
        // one", so a jump UP lands on a low index and drags several times as many lines —
        // each carrying a blur effect that re-renders every frame — through the viewport
        // as an equal jump DOWN. That is why seeking backwards ran visibly heavier than
        // seeking forwards. Past a viewport and a half, glide cleanly with no cascade.
        var seekSized = Math.Abs(delta) > scrollViewer.Viewport.Height * 1.5;

        var cascade = new List<(Control Control, double DelayMs)>();
        if (linesPanel != null && activeIndex >= 0 && Math.Abs(delta) > 8 && !seekSized)
        {
            // Every line below the active one takes part; only the DELAY is capped.
            // Cutting the list at CascadeMaxLines left the first excluded line static
            // while the last included one lagged a full line height onto it — lyrics
            // overlapped mid-glide wherever that boundary was on screen (worst in the
            // side panel; see LyricsPanelView.AnimateScroll).
            for (int i = activeIndex + 1; i < linesPanel.Children.Count; i++)
            {
                cascade.Add((linesPanel.Children[i],
                    Math.Min(i - activeIndex, CascadeMaxLines) * CascadeDelayPerLineMs));
            }
        }
        _cascadeLines = cascade.Count > 0 ? cascade : null;

        var generation = _scrollAnimationGeneration;
        var sw = Stopwatch.StartNew();
        var totalMs = (double)durationMs;
        var maxDelayMs = cascade.Count > 0 ? cascade[^1].DelayMs : 0;

        // Frame-pacing diagnostic: a dropped frame during the glide is a visible skip
        // (positions are time-based, so the next frame catches up in one step). One
        // dev-log line per glide that dropped any, with where in the glide it happened.
        var lastFrameMs = double.NaN;
        var frames = 0;
        var maxGapMs = 0.0;
        var maxGapAtMs = 0.0;
        var droppedFrames = 0;

        void Frame(TimeSpan _)
        {
            // Superseded or cancelled: the canceller already reset flags/transforms.
            if (generation != _scrollAnimationGeneration) return;

            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / totalMs);

            frames++;
            if (!double.IsNaN(lastFrameMs))
            {
                var gap = elapsed - lastFrameMs;
                if (gap > maxGapMs) { maxGapMs = gap; maxGapAtMs = elapsed; }
                if (gap > 30) droppedFrames++;
            }
            lastFrameMs = elapsed;

            // Scroll easing: the shared line-motion curve (ζ=0.85 spring: sub-pixel
            // overshoot — the old wobbly SpringEase read as "jumped past, then snapped
            // back"; a critically damped one crept for 3px after the eye saw it stop).
            // Same curve as the line's scale transition, so size and position arrive
            // together; smootherstep sat nearly still for its first quarter while the
            // scale had already popped.
            var eased = LineMotion.Ease(t);
            ApplyScrollOffset(scrollViewer, from + delta * eased);

            // Stagger: each cascade line is displaced by the gap between the base ease
            // and its own delayed ease — positive while catching up, zero when settled.
            // Chained clamp: on large scroll deltas the raw stagger between neighbours
            // exceeds the inter-line gap, so bound each line's lag to its predecessor's
            // (the list is in top-to-bottom order) — lines can never cross.
            // The lag chain must be walked in full — each line's clamp depends on the one
            // above it — but only lines near the viewport need the transform WRITTEN.
            // A write costs a visual invalidation plus a re-render of that line's blur
            // effect, and "every line below the active one" means a backward seek (which
            // lands on a low index) enrolled most of the song while an equally long
            // forward seek enrolled a handful — several times the per-frame cost for the
            // same travel, which is why scrolling up felt sluggish and scrolling down
            // did not. Off-screen lines render identically at any lag, so skipping them
            // changes nothing visible.
            var viewportHeight = scrollViewer.Viewport.Height;
            var scrollY = from + delta * eased;
            var bandTop = scrollY - viewportHeight;
            var bandBottom = scrollY + viewportHeight * 2;

            var prevLag = 0.0;
            foreach (var (control, delayMs) in cascade)
            {
                var tLine = Math.Clamp((elapsed - delayMs) / totalMs, 0.0, 1.0);
                var lag = LineMotion.CascadeStep(
                    delta * (eased - LineMotion.Ease(tLine)), prevLag, MaxCascadeLagStepPx);
                prevLag = lag;

                // Bounds is layout-only, so it is not disturbed by the transforms written
                // here. The band is a viewport of slack either side of the visible area,
                // so a line is already being driven well before it can be seen.
                var top = control.Bounds.Top + lag;
                var target = top + control.Bounds.Height >= bandTop && top <= bandBottom ? lag : 0.0;

                if (control.RenderTransform is TranslateTransform tt)
                {
                    // ReSharper disable once CompareOfFloatsByEqualityOperator
                    if (tt.Y != target) tt.Y = target;
                }
                else if (target != 0.0)
                {
                    control.RenderTransform = new TranslateTransform(0, target);
                }
            }

            if (t >= 1.0 && elapsed >= totalMs + maxDelayMs)
            {
                ApplyScrollOffset(scrollViewer, to);
                ClearCascadeTransforms();
                _isProgrammaticScroll = false;
                Trace($"glide end at {elapsed:0}ms (planned {durationMs}+{maxDelayMs:0}): {frames} frames, " +
                      $"{droppedFrames} gap(s) >30ms, worst {maxGapMs:0}ms at {maxGapAtMs:0}ms, {cascade.Count} cascade lines, offset {to:0}");
                return;
            }

            RequestScrollFrame(Frame, scrollViewer, to);
        }

        RequestScrollFrame(Frame, scrollViewer, to);
    }

    /// <summary>Schedules the next animation frame. If the view left the visual tree
    /// mid-animation (no TopLevel → no frame callbacks), snaps to the target instead
    /// so the offset never strands mid-glide.</summary>
    private void RequestScrollFrame(Action<TimeSpan> frame, ScrollViewer scrollViewer, double to)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(frame);
        }
        else
        {
            ApplyScrollOffset(scrollViewer, to);
            CancelScrollAnimation();
        }
    }

}

