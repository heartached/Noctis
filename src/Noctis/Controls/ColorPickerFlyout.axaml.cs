using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Reusable swatch + HSV/hex picker flyout. Exposes a single two-way bindable
/// <see cref="Hex"/> property so the same control can drive any "pick a color"
/// row (Settings accent picker, Theme Editor color fields, etc.).
/// </summary>
public partial class ColorPickerFlyout : UserControl
{
    public static readonly StyledProperty<string> HexProperty =
        AvaloniaProperty.Register<ColorPickerFlyout, string>(
            nameof(Hex),
            defaultValue: "#000000",
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> SwatchSizeProperty =
        AvaloniaProperty.Register<ColorPickerFlyout, double>(
            nameof(SwatchSize),
            defaultValue: 28d);

    public static readonly StyledProperty<object?> SwatchContentProperty =
        AvaloniaProperty.Register<ColorPickerFlyout, object?>(
            nameof(SwatchContent));

    public string Hex
    {
        get => GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public double SwatchSize
    {
        get => GetValue(SwatchSizeProperty);
        set => SetValue(SwatchSizeProperty, value);
    }

    public object? SwatchContent
    {
        get => GetValue(SwatchContentProperty);
        set => SetValue(SwatchContentProperty, value);
    }

    public ColorPickerFlyout()
    {
        InitializeComponent();
    }
}
