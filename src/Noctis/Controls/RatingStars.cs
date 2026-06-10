using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Lightweight 5-star rating display/editor. Renders the stars directly (no per-star
/// buttons) so it stays cheap inside track-row item templates. When <see cref="IsInteractive"/>
/// is true, clicking a star sets <see cref="Value"/>; clicking the current value clears to 0.
/// </summary>
public class RatingStars : Control
{
    private const int StarCount = 5;
    private const double GeometrySize = 24;

    private static readonly Geometry StarGeometry = Geometry.Parse(
        "M12 1.8 L14.9 7.9 L21.6 8.8 L16.7 13.4 L17.9 20 L12 16.8 L6.1 20 L7.3 13.4 L2.4 8.8 L9.1 7.9 Z");

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<RatingStars, int>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> StarSizeProperty =
        AvaloniaProperty.Register<RatingStars, double>(nameof(StarSize), 14);

    public static readonly StyledProperty<double> StarSpacingProperty =
        AvaloniaProperty.Register<RatingStars, double>(nameof(StarSpacing), 3);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RatingStars, IBrush?>(nameof(Foreground), Brushes.Gold);

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<RatingStars, bool>(nameof(IsInteractive));

    private int _hoverValue = -1;

    static RatingStars()
    {
        AffectsRender<RatingStars>(ValueProperty, ForegroundProperty, StarSizeProperty, StarSpacingProperty);
        AffectsMeasure<RatingStars>(StarSizeProperty, StarSpacingProperty);
    }

    /// <summary>Rating from 0 (unrated) to 5 stars.</summary>
    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StarSize
    {
        get => GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    public double StarSpacing
    {
        get => GetValue(StarSpacingProperty);
        set => SetValue(StarSpacingProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>When false the control is display-only and ignores pointer input.</summary>
    public bool IsInteractive
    {
        get => GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(StarCount * StarSize + (StarCount - 1) * StarSpacing, StarSize);

    public override void Render(DrawingContext context)
    {
        // Transparent backplate so the whole bounds (including star gaps) is clickable.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var brush = Foreground;
        if (brush == null)
            return;

        var shown = IsInteractive && _hoverValue >= 0 ? _hoverValue : System.Math.Clamp(Value, 0, StarCount);
        var scale = StarSize / GeometrySize;

        for (int i = 0; i < StarCount; i++)
        {
            var transform = Matrix.CreateScale(scale, scale) *
                            Matrix.CreateTranslation(i * (StarSize + StarSpacing), 0);
            using (context.PushTransform(transform))
            {
                if (i < shown)
                {
                    context.DrawGeometry(brush, null, StarGeometry);
                }
                else
                {
                    using (context.PushOpacity(0.25))
                        context.DrawGeometry(brush, null, StarGeometry);
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsInteractive) return;

        var star = StarFromPosition(e.GetPosition(this).X);
        if (star != _hoverValue)
        {
            _hoverValue = star;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverValue != -1)
        {
            _hoverValue = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var star = StarFromPosition(e.GetPosition(this).X);
        // Clicking the current rating clears it (same affordance as Apple Music).
        SetCurrentValue(ValueProperty, star == Value ? 0 : star);
        _hoverValue = -1;
        InvalidateVisual();
        e.Handled = true;
    }

    private int StarFromPosition(double x)
    {
        var slot = StarSize + StarSpacing;
        return System.Math.Clamp((int)(x / slot) + 1, 1, StarCount);
    }
}
