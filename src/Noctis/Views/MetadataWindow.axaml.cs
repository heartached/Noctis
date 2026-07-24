using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MetadataWindow : Window
{
    private const double VolumeThumbSize = 14;
    private readonly TranslateTransform _volumeAdjustThumbTransform = new();
    private bool _isVolumeAdjustDragging;
    private DateTime _lastVolumeAdjustPressAt = DateTime.MinValue;
    private Point _lastVolumeAdjustPressPosition;
    private ScrollViewer? _genreFormScroller;

    public MetadataWindow()
    {
        InitializeComponent();

        if (this.FindControl<ComboBox>("GenreCombo") is { } genreCombo)
        {
            genreCombo.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnGenreComboWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            genreCombo.DropDownOpened += OnGenreDropDownOpened;
            genreCombo.DropDownClosed += OnGenreDropDownClosed;
        }

        VolumeAdjustThumb.RenderTransform = _volumeAdjustThumbTransform;
        VolumeAdjustSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeAdjustPointerPressed, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.AddHandler(InputElement.PointerMovedEvent, OnVolumeAdjustPointerMoved, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeAdjustPointerReleased, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.PointerCaptureLost += OnVolumeAdjustCaptureLost;
        VolumeAdjustSlider.PropertyChanged += OnVolumeAdjustSliderPropertyChanged;
        VolumeAdjustSlider.SizeChanged += (_, _) => UpdateVolumeAdjustVisual();
        DispatcherTimer.RunOnce(UpdateVolumeAdjustVisual, TimeSpan.FromMilliseconds(10));

        // Animate only user-driven tab switches — the initial SelectedIndex
        // binding settles before the window opens (the dialog has its own
        // entrance animation).
        Opened += (_, _) => _tabSwitchAnimationReady = true;
    }

    // ── Tab-switch entrance animation (mirrors the Settings modal tab glide) ──

    private ContentPresenter? _tabContentHost;
    private bool _tabSwitchAnimationReady;
    private static readonly Avalonia.Media.Transformation.TransformOperations TabPageOffsetTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(10px)");
    private static readonly Avalonia.Media.Transformation.TransformOperations TabPageRestTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
    private readonly Avalonia.Animation.Transitions _tabPageTransitions = new()
    {
        new Avalonia.Animation.DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
        },
        new Avalonia.Animation.TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
        },
    };

    /// <summary>Fades/glides the incoming tab page in. The pin happens with
    /// transitions detached so it's instant; re-attaching them next frame
    /// animates both properties back to rest. Keyframe animations can't drive
    /// RenderTransform (startup crash) — transitions are the supported path.</summary>
    private void OnMetaTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from ListBoxes/ComboBoxes inside tab pages.
        if (e.Source is not TabControl || !_tabSwitchAnimationReady)
            return;

        _tabContentHost ??= MetaTabControl.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_SelectedContentHost");
        if (_tabContentHost is not { } host)
            return;

        host.Transitions = null;
        host.Opacity = 0;
        host.RenderTransform = TabPageOffsetTransform;
        Dispatcher.UIThread.Post(() =>
        {
            host.Transitions = _tabPageTransitions;
            host.Opacity = 1;
            host.RenderTransform = TabPageRestTransform;
        }, DispatcherPriority.Render);
    }

    public MetadataWindow(MetadataViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();

        // Both search flyouts are AttachedFlyouts anchored on the preview borders
        // (so they open centered over the artwork, not at the Search buttons) and
        // AttachedFlyout has no IsOpen binding — drive open/close from the VM here.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MetadataViewModel.IsAnimatedArtworkSearchOpen))
            {
                if (viewModel.IsAnimatedArtworkSearchOpen)
                    ShowAnimatedSearchFlyout();
                else
                    _animatedSearchFlyout?.Hide();
            }
            else if (e.PropertyName == nameof(MetadataViewModel.IsArtworkSearchOpen))
            {
                if (viewModel.IsArtworkSearchOpen)
                    ShowArtworkSearchFlyout();
                else
                    _artworkSearchFlyout?.Hide();
            }
        };
    }

    private Avalonia.Controls.Flyout? _artworkSearchFlyout;
    private Avalonia.Controls.Flyout? _animatedSearchFlyout;

    private void ShowArtworkSearchFlyout()
    {
        if (ArtworkButtonsBar is null || ArtworkPreviewAnchor is null) return;
        // Declared on the button bar, but shown centered over the preview.
        _artworkSearchFlyout ??= FlyoutBase.GetAttachedFlyout(ArtworkButtonsBar) as Avalonia.Controls.Flyout;
        if (_artworkSearchFlyout is null) return;

        // Keep the VM in sync when the flyout is dismissed by clicking outside,
        // so a later search reopens it.
        _artworkSearchFlyout.Closed -= OnArtworkSearchFlyoutClosed;
        _artworkSearchFlyout.Closed += OnArtworkSearchFlyoutClosed;
        _artworkSearchFlyout.ShowAt(ArtworkPreviewAnchor);
    }

    private void OnArtworkSearchFlyoutClosed(object? sender, EventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            vm.IsArtworkSearchOpen = false;
    }

    private void ShowAnimatedSearchFlyout()
    {
        if (AnimatedCoverPreviewAnchor is null) return;
        _animatedSearchFlyout ??= FlyoutBase.GetAttachedFlyout(AnimatedCoverPreviewAnchor) as Avalonia.Controls.Flyout;
        if (_animatedSearchFlyout is null) return;

        _animatedSearchFlyout.Closed -= OnAnimatedSearchFlyoutClosed;
        _animatedSearchFlyout.Closed += OnAnimatedSearchFlyoutClosed;
        _animatedSearchFlyout.ShowAt(AnimatedCoverPreviewAnchor);
    }

    private void OnAnimatedSearchFlyoutClosed(object? sender, EventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            vm.IsAnimatedArtworkSearchOpen = false;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void OnVolumeAdjustSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            vm.VolumeAdjust = 0;
    }

    private void OnVolumeAdjustSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty ||
            e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdateVolumeAdjustVisual();
        }
    }

    private void UpdateVolumeAdjustVisual()
    {
        if (VolumeAdjustSlider == null ||
            VolumeAdjustTrackBackground == null ||
            VolumeAdjustTrackFill == null ||
            VolumeAdjustThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            VolumeAdjustSlider,
            VolumeAdjustTrackBackground,
            VolumeAdjustTrackFill,
            VolumeAdjustThumb,
            _volumeAdjustThumbTransform,
            VolumeThumbSize,
            enabledBackgroundOpacity: 0.4,
            disabledBackgroundOpacity: 0.2);
    }

    private void OnVolumeAdjustPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(slider);
        if (IsVolumeAdjustDoublePress(position))
        {
            _isVolumeAdjustDragging = false;
            _lastVolumeAdjustPressAt = DateTime.MinValue;
            e.Pointer.Capture(null);
            if (DataContext is MetadataViewModel vm)
                vm.VolumeAdjust = 0;
            e.Handled = true;
            return;
        }

        _lastVolumeAdjustPressAt = DateTime.UtcNow;
        _lastVolumeAdjustPressPosition = position;
        _isVolumeAdjustDragging = true;
        e.Pointer.Capture(slider);
        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, position, VolumeThumbSize);
        e.Handled = true;
    }

    private void OnVolumeAdjustPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isVolumeAdjustDragging) return;
        if (sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, e.GetPosition(slider), VolumeThumbSize);
        e.Handled = true;
    }

    private void OnVolumeAdjustPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isVolumeAdjustDragging) return;

        _isVolumeAdjustDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnVolumeAdjustCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isVolumeAdjustDragging = false;
    }

    private bool IsVolumeAdjustDoublePress(Point position)
    {
        var elapsed = DateTime.UtcNow - _lastVolumeAdjustPressAt;
        if (elapsed > TimeSpan.FromMilliseconds(400))
            return false;

        var dx = position.X - _lastVolumeAdjustPressPosition.X;
        var dy = position.Y - _lastVolumeAdjustPressPosition.Y;
        return dx * dx + dy * dy <= 36;
    }

    private void OnGenreDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox cb)
            return;

        // The genre combo lives inside the Details tab's ScrollViewer. That
        // ScrollViewer's momentum-scroll behavior is a Tunnel handler that fires
        // first and consumes wheel events routed through it — including events over
        // the open dropdown, which is hosted within its subtree. Suspend it while
        // the dropdown is open so the wheel reaches the popup's own ScrollViewer.
        _genreFormScroller ??= cb.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (_genreFormScroller is not null)
            MomentumScrollBehavior.SetIsEnabled(_genreFormScroller, false);
    }

    private void OnGenreDropDownClosed(object? sender, EventArgs e)
    {
        if (_genreFormScroller is not null)
            MomentumScrollBehavior.SetIsEnabled(_genreFormScroller, true);
    }

    private void OnGenreComboWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox cb)
            return;

        // Dropdown open: let the popup's own ScrollViewer handle the wheel so the
        // genre list scrolls natively (matching the plain Options-tab combos).
        if (cb.IsDropDownOpen)
            return;

        // Dropdown closed: a ComboBox would otherwise cycle its selected value on
        // wheel. Suppress that and scroll the surrounding form instead, so wheeling
        // over the field inside the scrollable Details tab can't change the genre.
        e.Handled = true;
        var scrollViewer = cb.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
            ScrollByWheel(scrollViewer, e.Delta.Y);
    }

    private static void ScrollByWheel(ScrollViewer scrollViewer, double deltaY)
    {
        const double lineHeight = 50.0;
        var newY = scrollViewer.Offset.Y - deltaY * lineHeight;
        newY = Math.Max(0, Math.Min(newY, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newY);
    }
}
