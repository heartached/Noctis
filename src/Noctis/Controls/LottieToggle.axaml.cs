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

    private static IBrush ResolveAccentBrush()
    {
        if (Application.Current?.Resources.TryGetResource("AccentColorBrush", null, out var b) == true && b is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse("#E74856"));
    }

    private EventHandler? _accentHandler;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateVisualState();

        _accentHandler = (_, _) => UpdateVisualState();
        App.AccentApplied += _accentHandler;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_accentHandler != null)
        {
            App.AccentApplied -= _accentHandler;
            _accentHandler = null;
        }
        base.OnUnloaded(e);
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
            Track.Background = ResolveAccentBrush();
            Knob.Margin = new Thickness(29, 0, 0, 0);
        }
        else
        {
            Track.Background = new SolidColorBrush(Color.Parse("#484C54"));
            Knob.Margin = new Thickness(3, 0, 0, 0);
        }
    }
}
