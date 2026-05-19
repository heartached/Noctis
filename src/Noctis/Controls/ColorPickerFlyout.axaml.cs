using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    private double _hue;
    private double _saturation = 1;
    private double _value = 1;
    private bool _suppressHexEcho;

    public ColorPickerFlyout()
    {
        InitializeComponent();
        Spectrum.SizeChanged += (_, _) => UpdateVisuals();
        HueTrack.SizeChanged += (_, _) => UpdateVisuals();
        HexInput.LostFocus += OnHexInputLostFocus;
        HexInput.KeyDown += OnHexInputKeyDown;
        SyncFromHex(Hex);
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HexProperty && !_suppressHexEcho)
        {
            SyncFromHex(change.GetNewValue<string>());
            UpdateVisuals();
        }
    }

    private void OnSpectrumPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateSpectrumFromPointer(e);
        e.Pointer.Capture(Spectrum);
        e.Handled = true;
    }

    private void OnSpectrumPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(Spectrum).Properties.IsLeftButtonPressed)
        {
            UpdateSpectrumFromPointer(e);
            e.Handled = true;
        }
        else
        {
            e.Pointer.Capture(null);
        }
    }

    private void OnHuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateHueFromPointer(e);
        e.Pointer.Capture(HueTrack);
        e.Handled = true;
    }

    private void OnHuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(HueTrack).Properties.IsLeftButtonPressed)
        {
            UpdateHueFromPointer(e);
            e.Handled = true;
        }
        else
        {
            e.Pointer.Capture(null);
        }
    }

    private void OnHexInputLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CommitHexInput();

    private void OnHexInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHexInput();
            e.Handled = true;
        }
    }

    private void CommitHexInput()
    {
        var text = (HexInput.Text ?? string.Empty).Trim();
        if (TryParseHex(text, out var color))
        {
            PushColor(color);
        }
        else
        {
            // Snap the textbox back to the canonical value.
            HexInput.Text = Hex;
        }
    }

    private void UpdateSpectrumFromPointer(PointerEventArgs e)
    {
        var bounds = Spectrum.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var p = e.GetPosition(Spectrum);
        _saturation = Math.Clamp(p.X / bounds.Width, 0, 1);
        _value = 1 - Math.Clamp(p.Y / bounds.Height, 0, 1);
        PushColor(FromHsv(_hue, _saturation, _value));
    }

    private void UpdateHueFromPointer(PointerEventArgs e)
    {
        var width = HueTrack.Bounds.Width;
        if (width <= 0) return;

        var p = e.GetPosition(HueTrack);
        _hue = Math.Clamp(p.X / width, 0, 1) * 360;
        PushColor(FromHsv(_hue, _saturation, _value));
    }

    private void PushColor(Color color)
    {
        var hex = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        _suppressHexEcho = true;
        try { SetCurrentValue(HexProperty, hex); }
        finally { _suppressHexEcho = false; }
        HexInput.Text = hex;
        UpdateVisuals();
    }

    private void SyncFromHex(string? hex)
    {
        if (!TryParseHex(hex, out var color))
            return;

        ToHsv(color, out var hue, out var saturation, out var value);
        if (saturation > 0.001) _hue = hue;
        _saturation = saturation;
        _value = value;
        if (HexInput != null) HexInput.Text = hex;
    }

    private void UpdateVisuals()
    {
        if (Spectrum is null || HueTrack is null || HueWash is null ||
            SpectrumThumb is null || HueThumb is null || DefaultSwatch is null || HexRowSwatch is null)
            return;

        var hueColor = FromHsv(_hue, 1, 1);
        HueWash.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(hueColor, 1),
            }
        };

        var current = FromHsv(_hue, _saturation, _value);
        var currentBrush = new SolidColorBrush(current);
        DefaultSwatch.Fill = currentBrush;
        HexRowSwatch.Background = currentBrush;

        var sw = Spectrum.Bounds.Width;
        var sh = Spectrum.Bounds.Height;
        if (sw > 0 && sh > 0)
        {
            Canvas.SetLeft(SpectrumThumb, (_saturation * sw) - (SpectrumThumb.Width / 2));
            Canvas.SetTop(SpectrumThumb, ((1 - _value) * sh) - (SpectrumThumb.Height / 2));
        }

        var hw = HueTrack.Bounds.Width;
        if (hw > 0)
        {
            Canvas.SetLeft(HueThumb,
                Math.Clamp((_hue / 360) * hw - (HueThumb.Width / 2), 0, hw - HueThumb.Width));
            Canvas.SetTop(HueThumb, (HueTrack.Bounds.Height - HueThumb.Height) / 2);
        }
    }

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = Color.Parse(hex); return true; }
        catch { return false; }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - chroma;

        var (r1, g1, b1) = hue switch
        {
            < 60  => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _     => (chroma, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        hue = delta switch
        {
            0 => 0,
            _ when max == r => 60 * (((g - b) / delta) % 6),
            _ when max == g => 60 * (((b - r) / delta) + 2),
            _ => 60 * (((r - g) / delta) + 4)
        };
        if (hue < 0) hue += 360;

        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }
}
