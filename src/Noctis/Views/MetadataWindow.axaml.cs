using Avalonia;
using Avalonia.Controls;
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
    }

    public MetadataWindow(MetadataViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();

        // Avalonia's Flyout.IsOpen binding receives state from the flyout but
        // does NOT invoke Hide() when the source property flips to false. We
        // bridge it manually so the VM can dismiss the flyout after the user
        // picks a variant (download then runs in the background).
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MetadataViewModel.IsAnimatedArtworkSearchOpen) &&
                !viewModel.IsAnimatedArtworkSearchOpen)
            {
                (SearchAnimatedArtworkButton?.Flyout as Avalonia.Controls.Flyout)?.Hide();
            }
            else if (e.PropertyName == nameof(MetadataViewModel.IsArtworkSearchOpen))
            {
                // The artwork-search flyout is attached to the centered button bar
                // (not the off-center Search button), so it opens centered in the
                // dialog. Drive it from code since AttachedFlyout has no IsOpen binding.
                if (viewModel.IsArtworkSearchOpen)
                    ShowArtworkSearchFlyout();
                else
                    _artworkSearchFlyout?.Hide();
            }
        };
    }

    private Avalonia.Controls.Flyout? _artworkSearchFlyout;

    private void ShowArtworkSearchFlyout()
    {
        if (ArtworkButtonsBar is null) return;
        _artworkSearchFlyout ??= FlyoutBase.GetAttachedFlyout(ArtworkButtonsBar) as Avalonia.Controls.Flyout;
        if (_artworkSearchFlyout is null) return;

        // Keep the VM in sync when the flyout is dismissed by clicking outside,
        // so a later search reopens it.
        _artworkSearchFlyout.Closed -= OnArtworkSearchFlyoutClosed;
        _artworkSearchFlyout.Closed += OnArtworkSearchFlyoutClosed;
        _artworkSearchFlyout.ShowAt(ArtworkButtonsBar);
    }

    private void OnArtworkSearchFlyoutClosed(object? sender, EventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            vm.IsArtworkSearchOpen = false;
    }

    private async void OnEditSyncedLyricsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MetadataViewModel vm)
            await SyncedLyricsEditorWindow.ShowAsync(vm, this);
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
