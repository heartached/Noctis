using Avalonia;
using Avalonia.Controls;
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
    private ScrollViewer? _metadataTabStripScroller;
    private Button? _metadataTabStripLeft;
    private Button? _metadataTabStripRight;

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
        }

        VolumeAdjustThumb.RenderTransform = _volumeAdjustThumbTransform;
        VolumeAdjustSlider.AddHandler(InputElement.PointerPressedEvent, OnVolumeAdjustPointerPressed, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.AddHandler(InputElement.PointerMovedEvent, OnVolumeAdjustPointerMoved, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.AddHandler(InputElement.PointerReleasedEvent, OnVolumeAdjustPointerReleased, RoutingStrategies.Tunnel);
        VolumeAdjustSlider.PointerCaptureLost += OnVolumeAdjustCaptureLost;
        VolumeAdjustSlider.PropertyChanged += OnVolumeAdjustSliderPropertyChanged;
        VolumeAdjustSlider.SizeChanged += (_, _) => UpdateVolumeAdjustVisual();
        Loaded += (_, _) => HookMetadataTabStripScroller();
        DispatcherTimer.RunOnce(UpdateVolumeAdjustVisual, TimeSpan.FromMilliseconds(10));
        DispatcherTimer.RunOnce(HookMetadataTabStripScroller, TimeSpan.FromMilliseconds(10));
    }

    public MetadataWindow(MetadataViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
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

    private void HookMetadataTabStripScroller()
    {
        if (_metadataTabStripScroller is not null)
            return;

        var tabControl = this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabControl is null)
            return;

        _metadataTabStripScroller = tabControl.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(sv => sv.Name == "PART_TabStripScroller");

        if (_metadataTabStripScroller is null)
            return;

        _metadataTabStripLeft = tabControl.GetVisualDescendants()
            .OfType<Button>().FirstOrDefault(b => b.Name == "PART_TabStripLeft");
        _metadataTabStripRight = tabControl.GetVisualDescendants()
            .OfType<Button>().FirstOrDefault(b => b.Name == "PART_TabStripRight");

        if (_metadataTabStripLeft is not null)
            _metadataTabStripLeft.Click += OnMetadataTabStripLeftClick;
        if (_metadataTabStripRight is not null)
            _metadataTabStripRight.Click += OnMetadataTabStripRightClick;

        _metadataTabStripScroller.ScrollChanged += (_, _) => UpdateMetadataTabStripOverflow();
        _metadataTabStripScroller.LayoutUpdated += (_, _) => UpdateMetadataTabStripOverflow();
        _metadataTabStripScroller.AddHandler(InputElement.PointerWheelChangedEvent, OnMetadataTabStripWheel, RoutingStrategies.Tunnel, handledEventsToo: true);

        UpdateMetadataTabStripOverflow();
    }

    private void UpdateMetadataTabStripOverflow()
    {
        var scroller = _metadataTabStripScroller;
        if (scroller is null) return;

        var maxOffset = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var canScrollLeft = scroller.Offset.X > 1;
        var canScrollRight = scroller.Offset.X < maxOffset - 1;

        if (_metadataTabStripLeft is not null) _metadataTabStripLeft.IsVisible = canScrollLeft;
        if (_metadataTabStripRight is not null) _metadataTabStripRight.IsVisible = canScrollRight;
    }

    private void ScrollMetadataTabStrip(double delta)
    {
        var scroller = _metadataTabStripScroller;
        if (scroller is null) return;
        var maxOffset = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var target = Math.Clamp(scroller.Offset.X + delta, 0, maxOffset);
        scroller.Offset = new Vector(target, scroller.Offset.Y);
    }

    private void OnMetadataTabStripLeftClick(object? sender, RoutedEventArgs e)
    {
        if (_metadataTabStripScroller is null) return;
        ScrollMetadataTabStrip(-_metadataTabStripScroller.Viewport.Width * 0.8);
    }

    private void OnMetadataTabStripRightClick(object? sender, RoutedEventArgs e)
    {
        if (_metadataTabStripScroller is null) return;
        ScrollMetadataTabStrip(_metadataTabStripScroller.Viewport.Width * 0.8);
    }

    private void OnMetadataTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_metadataTabStripScroller is null) return;
        const double WheelStep = 60.0;
        ScrollMetadataTabStrip(-e.Delta.Y * WheelStep);
        e.Handled = true;
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

    private void OnGenreComboWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsDropDownOpen)
            return;

        e.Handled = true;

        var scrollViewer = cb.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null)
            return;

        const double lineHeight = 50.0;
        var newY = scrollViewer.Offset.Y - e.Delta.Y * lineHeight;
        newY = System.Math.Max(0, System.Math.Min(newY, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newY);
    }
}
