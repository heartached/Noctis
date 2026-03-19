using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Noctis.Controls;

public partial class LottieToggle : UserControl
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<LottieToggle, bool>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public LottieToggle()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateVisualState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty)
            UpdateVisualState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }

    private void UpdateVisualState()
    {
        if (Track is null || Knob is null) return;

        if (IsChecked)
        {
            Track.Background = new SolidColorBrush(Color.Parse("#E74856"));
            Knob.Margin = new Thickness(29, 0, 0, 0);
        }
        else
        {
            Track.Background = new SolidColorBrush(Color.Parse("#484C54"));
            Knob.Margin = new Thickness(3, 0, 0, 0);
        }
    }
}
