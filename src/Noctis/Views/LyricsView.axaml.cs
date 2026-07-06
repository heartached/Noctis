using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsView : UserControl
{
    private int _lastScrolledIndex = -1;
    private DispatcherTimer? _activeScrollTimer;
    private bool _isProgrammaticScroll;
    // Lines currently carrying a transient cascade translate (Apple Music-style
    // staggered glide); cleared whenever the scroll animation ends or is cancelled.
    private List<(Control Control, double DelayMs)>? _cascadeLines;
    private DispatcherTimer? _autoFollowResumeTimer;
    private LyricsViewModel? _subscribedVm;
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

    public LyricsView()
    {
        InitializeComponent();

        // Detect manual scroll via mouse wheel to pause auto-follow
        if (LyricsScrollViewer != null)
        {
            LyricsScrollViewer.PointerWheelChanged += OnLyricsPointerWheelChanged;
            LyricsScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }

        LyricsTimelineThumb.RenderTransform = _lyricsTimelineThumbTransform;
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

        // Watch window min/maximize/restore so we can re-anchor the active line cleanly.
        if (e.Root is Window window)
        {
            _hostWindow = window;
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }

        if (DataContext is LyricsViewModel vm)
        {
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

        if (_subscribedVm != null)
        {
            _subscribedVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
        }

        if (_hostWindow != null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }

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
                // Narrow mode: stack vertically (cover row on top, lyrics below)
                Grid.SetColumnSpan(LeftPanel, 2);
                Grid.SetRow(LeftPanel, 0);
                Grid.SetRowSpan(LeftPanel, 1);
                LeftPanel.MaxHeight = 320;
                LeftPanel.Padding = new Thickness(30, 20);

                Grid.SetColumn(RightPanel, 0);
                Grid.SetColumnSpan(RightPanel, 2);
                Grid.SetRow(RightPanel, 1);
                Grid.SetRowSpan(RightPanel, 1);
            }
            else
            {
                // Wide mode: two equal columns spanning both rows
                Grid.SetColumnSpan(LeftPanel, 1);
                Grid.SetRow(LeftPanel, 0);
                Grid.SetRowSpan(LeftPanel, 2);
                LeftPanel.MaxHeight = double.PositiveInfinity;
                LeftPanel.Padding = new Thickness(40, 30);

                Grid.SetColumn(RightPanel, 1);
                Grid.SetColumnSpan(RightPanel, 1);
                Grid.SetRow(RightPanel, 0);
                Grid.SetRowSpan(RightPanel, 2);
            }
        }

        // Continuous sizing: derive the cover and lyric sizes from the actual
        // window dimensions instead of assuming a 1080p-class maximized window.
        // The previous fixed 780px cover + 1.1× lyric scale overflowed smaller
        // displays (MacBook-sized windows) and broke fullscreen/resize.
        double stackWidth;
        if (_isNarrowMode)
        {
            var cover = Math.Clamp(height * 0.25, 120, 200);
            AlbumArtBorder.Width = cover;
            AlbumArtBorder.Height = cover;
            stackWidth = Math.Max(cover, 200);
            LyricsItemsControl.MaxWidth = Math.Max(240, width - 80);
            RightPanel.RenderTransform = null;
        }
        else
        {
            // Left column is half the window minus panel padding; vertically
            // reserve room for track info, timeline, and playback controls.
            var maxByWidth = width / 2 - 90;
            var maxByHeight = height - 330;
            var cover = Math.Clamp(Math.Min(maxByWidth, maxByHeight), 220, 780);
            AlbumArtBorder.Width = cover;
            AlbumArtBorder.Height = cover;
            stackWidth = Math.Max(cover, 300);
            LyricsItemsControl.MaxWidth = Math.Clamp(width / 2 - 120, 280, 620);
            RightPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)");
        }

        LeftContentStack.Width = stackWidth;

        // Track title/artist/album marquees must not run wider than the stack.
        var marqueeMax = Math.Min(520, Math.Max(180, stackWidth - 40));
        TitleMarquee.MaxDisplayWidth = marqueeMax;
        ArtistMarquee.MaxDisplayWidth = marqueeMax;
        AlbumMarquee.MaxDisplayWidth = marqueeMax;

        // Lyric text: 46px suits a ~1000px-tall window; scale down with the
        // window so lines don't wrap into a wall of text on small displays.
        // Inherited by the line/karaoke TextBlocks in the item template.
        var fontScale = Math.Clamp(Math.Min(height / 1000.0, width / 1700.0), 0.55, 1.0);
        LyricsItemsControl.FontSize = Math.Round(46 * fontScale);
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
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
            _subscribedVm = null;
        }

        // Reset scroll state
        _lastScrolledIndex = -1;
        CancelScrollAnimation();
        CancelAutoFollowResumeTimer();

        if (DataContext is LyricsViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.OpenBackgroundColorRequested += OnOpenBackgroundColorRequested;
            _subscribedVm = vm;
        }
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
                    LyricsScrollViewer.Offset = new Avalonia.Vector(0, 0);
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
        }
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

    private static double GetTimelineValueFromPointer(Slider slider, Point position)
    {
        return PillSliderVisualHelper.GetValueFromPointer(slider, position, LyricsTimelineThumbSize);
    }

    private void CancelScrollAnimation()
    {
        _isProgrammaticScroll = false;
        _activeScrollTimer?.Stop();
        _activeScrollTimer = null;
        ClearCascadeTransforms();
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
        if (LyricsScrollViewer == null || LyricsItemsControl == null) return;

        // Top margin = 10% so lyrics start near the top of the center zone
        // Bottom margin = 78% so the last lyric can still be scrolled to the 22% target
        var viewportHeight = LyricsScrollViewer.Viewport.Height;
        if (viewportHeight <= 0) return;

        var topPad = viewportHeight * 0.10;
        var bottomPad = viewportHeight * 0.78;
        // Right margin reserves an overflow zone for the active line's 1.07× scale
        // transform. Without it, scaled glyphs on long lines get clipped by the
        // ScrollViewer's internal viewport ("…GOA" instead of "…GOAT").
        const double activeLineScaleHeadroom = 64;
        LyricsItemsControl.Margin = new Thickness(0, topPad, activeLineScaleHeadroom, bottomPad);
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
            return;

        CancelScrollAnimation();

        // Minimal delay — just enough for layout to settle after active line change
        DispatcherTimer.RunOnce(() =>
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

                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
                var childHeight = targetChild.Bounds.Height;
                var viewportHeight = LyricsScrollViewer.Viewport.Height;

                var targetOffset = childTop - (viewportHeight * 0.22) + (childHeight / 2.0);
                targetOffset = Math.Max(0, targetOffset);

                var currentOffset = LyricsScrollViewer.Offset.Y;
                var diff = targetOffset - currentOffset;

                if (Math.Abs(diff) < 2)
                {
                    LyricsScrollViewer.Offset = new Vector(0, targetOffset);
                    return;
                }

                var distance = Math.Abs(diff);
                var durationMs = (int)Math.Min(1050, Math.Max(650, distance * 0.85));
                AnimateScroll(LyricsScrollViewer, currentOffset, targetOffset, durationMs, panel, index);
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    private void JumpToActiveLineWhenReady(int index)
    {
        if (index < 0)
            return;

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

                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
                var childHeight = targetChild.Bounds.Height;
                var viewportHeight = LyricsScrollViewer.Viewport.Height;

                var targetOffset = childTop - (viewportHeight * 0.22) + (childHeight / 2.0);
                targetOffset = Math.Max(0, targetOffset);
                LyricsScrollViewer.Offset = new Vector(0, targetOffset);
            }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    // Cascade tuning: each line below the active one starts its glide this much later,
    // up to this many lines deep — the Apple Music "settle top-down" feel.
    private const double CascadeDelayPerLineMs = 35;
    private const int CascadeMaxLines = 8;

    /// <summary>
    /// Time-based scroll animation using Stopwatch for smooth, frame-accurate motion.
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
        var cascade = new List<(Control Control, double DelayMs)>();
        if (linesPanel != null && activeIndex >= 0 && Math.Abs(delta) > 8)
        {
            for (int i = activeIndex + 1;
                 i < linesPanel.Children.Count && i - activeIndex <= CascadeMaxLines;
                 i++)
            {
                cascade.Add((linesPanel.Children[i], (i - activeIndex) * CascadeDelayPerLineMs));
            }
        }
        _cascadeLines = cascade.Count > 0 ? cascade : null;

        var sw = Stopwatch.StartNew();
        var totalMs = (double)durationMs;
        var maxDelayMs = cascade.Count > 0 ? cascade[^1].DelayMs : 0;

        // ~60fps tick rate for smooth rendering
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _activeScrollTimer = timer;

        timer.Tick += (_, _) =>
        {
            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / totalMs);

            // Scroll easing: smootherstep glides without overshoot. Spring overshoot here
            // reads as "the lyrics jumped past, then snapped back" — opposite of smooth.
            var eased = Easing.SmootherStep(t);
            var value = from + delta * eased;

            scrollViewer.Offset = new Vector(0, value);

            // Stagger: each cascade line is displaced by the gap between the base ease
            // and its own delayed ease — positive while catching up, zero when settled.
            foreach (var (control, delayMs) in cascade)
            {
                var tLine = Math.Clamp((elapsed - delayMs) / totalMs, 0.0, 1.0);
                var lag = delta * (eased - Easing.SmootherStep(tLine));
                if (control.RenderTransform is TranslateTransform tt)
                    tt.Y = lag;
                else
                    control.RenderTransform = new TranslateTransform(0, lag);
            }

            if (t >= 1.0 && elapsed >= totalMs + maxDelayMs)
            {
                scrollViewer.Offset = new Vector(0, to);
                ClearCascadeTransforms();
                timer.Stop();
                sw.Stop();
                _isProgrammaticScroll = false;
                if (_activeScrollTimer == timer)
                    _activeScrollTimer = null;
            }
        };
        timer.Start();
    }

}

