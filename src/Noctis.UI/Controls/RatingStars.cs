using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Lightweight 5-star rating display/editor. Renders the stars directly (no per-star
/// buttons) so it stays cheap inside track-row item templates. When <see cref="IsInteractive"/>
/// is true, clicking a star sets <see cref="Value"/>; clicking the current value clears to 0.
/// With Focusable set, the arrow keys step the rating, Home clears it and End gives five stars;
/// UI Automation reads and sets it as a 0–5 range.
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

    /// <summary>The unlit stars' colour (drawn at 25%); null takes <see cref="Foreground"/>.</summary>
    public static readonly StyledProperty<IBrush?> EmptyForegroundProperty =
        AvaloniaProperty.Register<RatingStars, IBrush?>(nameof(EmptyForeground));

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<RatingStars, bool>(nameof(IsInteractive));

    private int _hoverValue = -1;

    /// <summary>
    /// Raised after a click changed <see cref="Value"/> (the new value, 0 = cleared). Bindings
    /// see the change too; this exists so a row can persist the rating for its track — and
    /// for the whole multi-selection — without a two-way binding straight onto the model.
    /// </summary>
    public event EventHandler<int>? UserRated;

    static RatingStars()
    {
        AffectsRender<RatingStars>(ValueProperty, ForegroundProperty, EmptyForegroundProperty, StarSizeProperty, StarSpacingProperty);
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

    public IBrush? EmptyForeground
    {
        get => GetValue(EmptyForegroundProperty);
        set => SetValue(EmptyForegroundProperty, value);
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
                        context.DrawGeometry(EmptyForeground ?? brush, null, StarGeometry);
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
        var newValue = star == Value ? 0 : star;
        SetCurrentValue(ValueProperty, newValue);
        _hoverValue = -1;
        InvalidateVisual();
        e.Handled = true;
        UserRated?.Invoke(this, newValue);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsInteractive || e.Handled || e.KeyModifiers != KeyModifiers.None) return;

        int? newValue = e.Key switch
        {
            Key.Right or Key.Up => Value + 1,
            Key.Left or Key.Down => Value - 1,
            Key.Home => 0,
            Key.End => StarCount,
            _ => null,
        };
        if (newValue is not { } rated) return;
        e.Handled = true;
        Rate(rated);
    }

    private void Rate(int value)
    {
        value = System.Math.Clamp(value, 0, StarCount);
        if (value == Value) return;
        SetCurrentValue(ValueProperty, value);
        UserRated?.Invoke(this, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RatingStarsPeer(this);

    /// <summary>A 0–5 range a screen reader can read and, when interactive, set.</summary>
    private sealed class RatingStarsPeer : ControlAutomationPeer, IRangeValueProvider
    {
        public RatingStarsPeer(RatingStars owner) : base(owner)
        {
            owner.PropertyChanged += (_, e) =>
            {
                if (e.Property == ValueProperty)
                    RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty,
                        (double)e.GetOldValue<int>(), (double)e.GetNewValue<int>());
            };
        }

        private RatingStars Stars => (RatingStars)Owner;

        public bool IsReadOnly => !Stars.IsInteractive;
        public double Minimum => 0;
        public double Maximum => StarCount;
        public double Value => System.Math.Clamp(Stars.Value, 0, StarCount);
        public double SmallChange => 1;
        public double LargeChange => 1;

        public void SetValue(double value)
        {
            if (IsReadOnly) throw new InvalidOperationException("The rating is display-only.");
            Stars.Rate((int)System.Math.Round(value));
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }

    private int StarFromPosition(double x)
    {
        var slot = StarSize + StarSpacing;
        return System.Math.Clamp((int)(x / slot) + 1, 1, StarCount);
    }
}
