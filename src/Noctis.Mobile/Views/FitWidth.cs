using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// A header's large title beside its glass pill, as iOS sizes one: at its full size when it fits,
/// else scaled down until it does, no smaller than <see cref="MinScale"/>; past that the title
/// trims (its own TextTrimming). A translated title ("Медиатека", "Избранное") slid under the
/// pill at the fixed 34 px and was cut off by it.
/// </summary>
public sealed class FitWidth : Decorator
{
    public static readonly StyledProperty<double> MinScaleProperty =
        AvaloniaProperty.Register<FitWidth, double>(nameof(MinScale), 0.7);

    /// <summary>The smallest scale the title takes (0.7: 34 px → 24 px).</summary>
    public double MinScale
    {
        get => GetValue(MinScaleProperty);
        set => SetValue(MinScaleProperty, value);
    }

    /// <summary>The scale the last layout chose (1 = full size); tests read it.</summary>
    public double Scale { get; private set; } = 1;

    static FitWidth()
    {
        AffectsMeasure<FitWidth>(MinScaleProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = child.DesiredSize;
        var scale = 1.0;
        if (!double.IsInfinity(availableSize.Width) && natural.Width > availableSize.Width && natural.Width > 0)
            scale = Math.Clamp(availableSize.Width / natural.Width, Math.Clamp(MinScale, 0.1, 1), 1);
        Scale = scale;
        if (scale < 1 && !double.IsInfinity(availableSize.Width))
            child.Measure(new Size(availableSize.Width / scale, double.PositiveInfinity));
        var desired = child.DesiredSize;
        var width = desired.Width * scale;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        return new Size(width, desired.Height * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;
        var scale = Scale;
        child.Arrange(new Rect(0, 0, finalSize.Width / scale, finalSize.Height / scale));
        if (scale < 1)
        {
            if (child.RenderTransform is not ScaleTransform st || st.ScaleX != scale)
            {
                child.RenderTransformOrigin = RelativePoint.TopLeft;
                child.RenderTransform = new ScaleTransform(scale, scale);
            }
        }
        else if (child.RenderTransform != null)
        {
            child.RenderTransform = null;
        }
        return finalSize;
    }
}
