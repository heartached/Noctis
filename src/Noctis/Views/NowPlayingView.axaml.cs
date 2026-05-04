using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class NowPlayingView : UserControl
{
    private const double SeekThumbSize = 14;
    private readonly TranslateTransform _seekThumbTransform = new();
    private bool _isSeekDragging;

    public NowPlayingView()
    {
        InitializeComponent();

        NowPlayingSeekThumb.RenderTransform = _seekThumbTransform;
        NowPlayingSeekSlider.AddHandler(InputElement.PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
        NowPlayingSeekSlider.AddHandler(InputElement.PointerMovedEvent, OnSeekPointerMoved, RoutingStrategies.Tunnel);
        NowPlayingSeekSlider.AddHandler(InputElement.PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Tunnel);
        NowPlayingSeekSlider.PointerCaptureLost += OnSeekCaptureLost;
        NowPlayingSeekSlider.PropertyChanged += OnSeekSliderPropertyChanged;
        NowPlayingSeekSlider.SizeChanged += (_, _) => UpdateSeekSliderVisual();
        DispatcherTimer.RunOnce(UpdateSeekSliderVisual, TimeSpan.FromMilliseconds(10));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateSeekSliderVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isSeekDragging)
        {
            _isSeekDragging = false;
            if (DataContext is NowPlayingViewModel { Player: { } player })
                player.EndSeek();
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not NowPlayingViewModel { Player: { } player }) return;
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _isSeekDragging = true;
        player.BeginSeek();
        e.Pointer.Capture(slider);
        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, e.GetPosition(slider), SeekThumbSize);
        e.Handled = true;
    }

    private void OnSeekPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSeekDragging) return;
        if (sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, e.GetPosition(slider), SeekThumbSize);
        e.Handled = true;
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSeekDragging) return;

        _isSeekDragging = false;
        e.Pointer.Capture(null);

        if (DataContext is NowPlayingViewModel { Player: { } player })
            player.EndSeek();

        e.Handled = true;
    }

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isSeekDragging) return;

        _isSeekDragging = false;
        if (DataContext is NowPlayingViewModel { Player: { } player })
            player.EndSeek();
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
        if (NowPlayingSeekSlider == null ||
            NowPlayingSeekTrackBackground == null ||
            NowPlayingSeekTrackFill == null ||
            NowPlayingSeekThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            NowPlayingSeekSlider,
            NowPlayingSeekTrackBackground,
            NowPlayingSeekTrackFill,
            NowPlayingSeekThumb,
            _seekThumbTransform,
            SeekThumbSize);
    }
}
