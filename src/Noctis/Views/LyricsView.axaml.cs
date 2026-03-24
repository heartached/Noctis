using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
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
    private DispatcherTimer? _autoFollowResumeTimer;
    private LyricsViewModel? _subscribedVm;
    private bool _isNarrowMode;
    private bool _isImmersiveMode;
    private System.ComponentModel.PropertyChangedEventHandler? _mainVmImmersiveHandler;
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();

    private const double NarrowBreakpoint = 900;

    // Seek slider drag state — mirrors PlaybackBarView pattern
    private bool _isSeekDragging;

    public LyricsView()
    {
        InitializeComponent();

        // Detect manual scroll via mouse wheel to pause auto-follow
        if (LyricsScrollViewer != null)
        {
            LyricsScrollViewer.PointerWheelChanged += OnLyricsPointerWheelChanged;
            LyricsScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }

        // Seek slider: Tunnel routing prevents the Slider's Thumb from
        // starting its own drag, avoiding capture conflicts and stuck state.
        SeekSlider.AddHandler(InputElement.PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(InputElement.PointerMovedEvent, OnSeekPointerMoved, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(InputElement.PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Tunnel);
        SeekSlider.PointerCaptureLost += OnSeekCaptureLost;

        // Volume slider percentage badge
        LyricsVolumeSlider.PointerEntered += OnLyricsVolumeEntered;
        LyricsVolumeSlider.PointerExited += OnLyricsVolumeExited;
        LyricsVolumeSlider.PropertyChanged += OnLyricsVolumePropertyChanged;

        // Mouse wheel → horizontal scroll for color swatch pickers
        SolidSwatchScroller.PointerWheelChanged += OnSwatchWheelScroll;
        GradientSwatchScroller.PointerWheelChanged += OnSwatchWheelScroll;
    }

    // Smooth horizontal scroll state for color swatch pickers
    private DispatcherTimer? _swatchScrollTimer;
    private ScrollViewer? _swatchScrollTarget;
    private double _swatchScrollFrom;
    private double _swatchScrollTo;
    private Stopwatch? _swatchScrollSw;
    private const int SwatchScrollDurationMs = 250;

    private void OnSwatchWheelScroll(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        e.Handled = true;

        var maxX = sv.Extent.Width - sv.Viewport.Width;
        if (maxX <= 0) return;

        // If already animating toward a target, extend from that target
        var currentTarget = (_swatchScrollTarget == sv && _swatchScrollTimer != null)
            ? _swatchScrollTo
            : sv.Offset.X;

        var newTarget = Math.Clamp(currentTarget - e.Delta.Y * 80, 0, maxX);

        _swatchScrollTarget = sv;
        _swatchScrollFrom = sv.Offset.X;
        _swatchScrollTo = newTarget;
        _swatchScrollSw = Stopwatch.StartNew();

        if (_swatchScrollTimer != null)
        {
            _swatchScrollTimer.Stop();
            _swatchScrollTimer = null;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _swatchScrollTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_swatchScrollSw == null) { timer.Stop(); return; }
            var t = Math.Min(1.0, _swatchScrollSw.Elapsed.TotalMilliseconds / SwatchScrollDurationMs);
            var eased = 1.0 - Math.Pow(1.0 - t, 3); // ease-out cubic
            sv.Offset = sv.Offset.WithX(_swatchScrollFrom + (_swatchScrollTo - _swatchScrollFrom) * eased);
            if (t >= 1.0)
            {
                sv.Offset = sv.Offset.WithX(_swatchScrollTo);
                timer.Stop();
                _swatchScrollSw.Stop();
                if (_swatchScrollTimer == timer) _swatchScrollTimer = null;
            }
        };
        timer.Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Reset scroll guard so re-entering the page always scrolls to the active line
        _lastScrolledIndex = -1;

        // Re-sync lyrics after the view is fully laid out.
        // EnsureLyricsForCurrentTrack may have already been called by the navigation code
        // before the view was attached, so PropertyChanged on ActiveLineIndex was missed.
        DispatcherTimer.RunOnce(() =>
        {
            if (DataContext is LyricsViewModel vm)
                vm.EnsureLyricsForCurrentTrack();
        }, TimeSpan.FromMilliseconds(50));

        // Subscribe to sidebar hidden state for immersive scaling
        if (this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel mainVm)
        {
            ApplyImmersiveMode(mainVm.IsSidebarHidden);
            _mainVmImmersiveHandler = (s, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsSidebarHidden))
                    Dispatcher.UIThread.Post(() => ApplyImmersiveMode(((MainWindowViewModel)s!).IsSidebarHidden));
            };
            mainVm.PropertyChanged += _mainVmImmersiveHandler;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Safety: ensure seek drag state is fully cleared on detach
        if (_isSeekDragging)
        {
            _isSeekDragging = false;
            if (DataContext is LyricsViewModel { Player: { } player })
                player.EndSeek();
        }

        // Unsubscribe immersive mode listener
        if (_mainVmImmersiveHandler != null &&
            this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.PropertyChanged -= _mainVmImmersiveHandler;
            _mainVmImmersiveHandler = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double width)
    {
        var shouldBeNarrow = width < NarrowBreakpoint;
        if (shouldBeNarrow == _isNarrowMode) return;
        _isNarrowMode = shouldBeNarrow;

        if (_isNarrowMode)
        {
            // Narrow mode: stack vertically
            MainLayoutGrid.ColumnDefinitions.Clear();
            MainLayoutGrid.RowDefinitions.Clear();
            MainLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            MainLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            Grid.SetColumn(LeftPanel, 0);
            Grid.SetRow(LeftPanel, 0);
            LeftPanel.MaxHeight = 320;
            LeftPanel.Padding = new Thickness(30, 20);

            AlbumArtBorder.Width = 200;
            AlbumArtBorder.Height = 200;

            var rightPanel = MainLayoutGrid.Children.Count > 1
                ? MainLayoutGrid.Children[1] as Grid
                : null;
            if (rightPanel != null)
            {
                Grid.SetColumn(rightPanel, 0);
                Grid.SetRow(rightPanel, 1);
            }
        }
        else
        {
            // Wide mode: two equal columns
            MainLayoutGrid.RowDefinitions.Clear();
            MainLayoutGrid.ColumnDefinitions.Clear();
            MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            MainLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            Grid.SetColumn(LeftPanel, 0);
            Grid.SetRow(LeftPanel, 0);
            LeftPanel.MaxHeight = double.PositiveInfinity;
            LeftPanel.Padding = new Thickness(40, 30);

            AlbumArtBorder.Width = _isImmersiveMode ? 620 : 520;
            AlbumArtBorder.Height = _isImmersiveMode ? 620 : 520;
            LeftContentStack.MaxWidth = _isImmersiveMode ? 680 : 560;
            LyricsItemsControl.MaxWidth = _isImmersiveMode ? 620 : 500;
            RightPanel.RenderTransform = _isImmersiveMode
                ? Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)")
                : Avalonia.Media.Transformation.TransformOperations.Parse("scale(1, 1)");

            var rightPanel = MainLayoutGrid.Children.Count > 1
                ? MainLayoutGrid.Children[1] as Grid
                : null;
            if (rightPanel != null)
            {
                Grid.SetColumn(rightPanel, 1);
                Grid.SetRow(rightPanel, 0);
            }
        }
    }

    private void ApplyImmersiveMode(bool immersive)
    {
        if (immersive == _isImmersiveMode) return;
        _isImmersiveMode = immersive;

        // Only scale up in wide mode (narrow mode has its own compact sizes)
        if (_isNarrowMode) return;

        if (_isImmersiveMode)
        {
            AlbumArtBorder.Width = 620;
            AlbumArtBorder.Height = 620;
            LeftContentStack.MaxWidth = 680;
            LyricsItemsControl.MaxWidth = 620;
            RightPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)");
        }
        else
        {
            AlbumArtBorder.Width = 520;
            AlbumArtBorder.Height = 520;
            LeftContentStack.MaxWidth = 560;
            LyricsItemsControl.MaxWidth = 500;
            RightPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1, 1)");
        }
    }

    private void OnTrackFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not LyricsViewModel { Player: { } player }) return;
        if (sender is not MenuFlyout flyout) return;

        if (!_playlistPopulators.TryGetValue(flyout, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in flyout.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[flyout] = populator;
        }

        populator.Populate(player.Playlists as ObservableCollection<Playlist>, player.AddCurrentTrackToExistingPlaylistCommand);
    }

    // ── Seek slider interaction ──

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LyricsViewModel { Player: { } player }) return;
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isSeekDragging = true;
        player.BeginSeek();
        e.Pointer.Capture(slider);
        slider.Value = GetPercentageFromPointer(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void OnSeekPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSeekDragging) return;
        if (sender is not Slider slider) return;

        slider.Value = GetPercentageFromPointer(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSeekDragging) return;
        _isSeekDragging = false;

        e.Pointer.Capture(null);

        if (DataContext is LyricsViewModel { Player: { } player })
            player.EndSeek();

        e.Handled = true;
    }

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isSeekDragging) return;
        _isSeekDragging = false;

        if (DataContext is LyricsViewModel { Player: { } player })
            player.EndSeek();
    }

    private static double GetPercentageFromPointer(Slider slider, Point position)
    {
        if (slider.Bounds.Width <= 0)
            return 0;

        var percentage = position.X / slider.Bounds.Width;
        return Math.Clamp(percentage, 0, 1);
    }

    // ── ViewModel subscription + scroll animation ──

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unsubscribe from previous ViewModel
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }

        // Reset scroll state
        _lastScrolledIndex = -1;
        CancelScrollAnimation();
        CancelAutoFollowResumeTimer();

        if (DataContext is LyricsViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedVm = vm;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            if (sender is LyricsViewModel vm)
            {
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
            case nameof(LyricsViewModel.LyricsSliderThumb):
                SetResourceBrush("LyricsSliderThumbRes", vm.LyricsSliderThumb);
                break;
            case nameof(LyricsViewModel.LyricsSliderFilled):
                SetResourceBrush("LyricsSliderFilledRes", vm.LyricsSliderFilled);
                break;
            case nameof(LyricsViewModel.LyricsSliderUnfilled):
                SetResourceBrush("LyricsSliderUnfilledRes", vm.LyricsSliderUnfilled);
                break;
        }
    }

    private void SetResourceBrush(string key, IBrush brush)
    {
        if (brush is SolidColorBrush scb && Resources[key] is SolidColorBrush existing)
            existing.Color = scb.Color;
    }

    private void CancelScrollAnimation()
    {
        _isProgrammaticScroll = false;
        _activeScrollTimer?.Stop();
        _activeScrollTimer = null;
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
        LyricsItemsControl.Margin = new Thickness(0, topPad, 0, bottomPad);
    }

    // ── Volume percentage badge ──

    private void OnLyricsVolumeEntered(object? sender, PointerEventArgs e)
    {
        UpdateLyricsVolumePosition();
        LyricsVolumePercentage.Opacity = 1.0;
    }

    private void OnLyricsVolumeExited(object? sender, PointerEventArgs e)
    {
        LyricsVolumePercentage.Opacity = 0;
    }

    private void OnLyricsVolumePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty)
            UpdateLyricsVolumePosition();
    }

    private void UpdateLyricsVolumePosition()
    {
        if (LyricsVolumePercentage.RenderTransform is not Avalonia.Media.TranslateTransform transform)
            return;

        // Slider width=120, thumb width=12 → travel = 120 - 12 = 108
        // Default track margin is ~0 for this style, thumb centers at 6..114
        const double thumbHalfWidth = 6.0;
        const double sliderWidth = 120.0;
        const double thumbTravel = sliderWidth - (thumbHalfWidth * 2);

        var fraction = LyricsVolumeSlider.Value / 100.0;
        var thumbCenterX = thumbHalfWidth + (fraction * thumbTravel);
        var textWidth = LyricsVolumePercentage.Bounds.Width;
        if (textWidth <= 0) textWidth = 18;
        transform.X = thumbCenterX - (textWidth / 2);
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
                var durationMs = (int)Math.Min(800, Math.Max(500, distance * 0.7));
                AnimateScroll(LyricsScrollViewer, currentOffset, targetOffset, durationMs);
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    /// <summary>
    /// Time-based scroll animation using Stopwatch for smooth, frame-accurate motion.
    /// Uses ease-out-sine for the most natural, liquid-smooth deceleration.
    /// </summary>
    private void AnimateScroll(ScrollViewer scrollViewer, double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        _isProgrammaticScroll = true;

        var sw = Stopwatch.StartNew();
        var totalMs = (double)durationMs;

        // ~60fps tick rate for smooth rendering
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _activeScrollTimer = timer;

        timer.Tick += (_, _) =>
        {
            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / totalMs);

            // Ease-out-sine: smooth continuous deceleration, no sudden start or stop
            var eased = Math.Sin(t * Math.PI / 2.0);
            var value = from + (to - from) * eased;

            scrollViewer.Offset = new Vector(0, value);

            if (t >= 1.0)
            {
                scrollViewer.Offset = new Vector(0, to);
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
