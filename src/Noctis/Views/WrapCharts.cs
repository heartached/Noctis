using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Views;

/// <summary>
/// Small drawn charts for the Noctis Wrap report (owner 10-09: "more like the last.fm
/// report"). Drawn rather than built from elements so a 31-bar month or the 24-spoke clock
/// costs one render call, and the bars stay crisp at any width. Colours come from the theme
/// (AccentColorBrush, the pill field tone) and follow a theme switch.
/// </summary>
public abstract class WrapChartBase : Control
{
    protected WrapChartBase()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected IBrush AccentBrush => Resource("AccentColorBrush") ?? Brushes.SteelBlue;
    protected IBrush TrackBrush => Resource("PillFieldBackground") ?? new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    protected IBrush TextBrush => Resource("SystemControlForegroundBaseHighBrush") ?? Brushes.White;

    private IBrush? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    protected static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, solid.Opacity * opacity)
            : brush;

    protected FormattedText Label(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
}

/// <summary>A rounded proportional bar: the track, and the fill at <see cref="Fraction"/> of it.</summary>
public sealed class WrapBar : WrapChartBase
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<WrapBar, double>(nameof(Fraction));

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    static WrapBar() => AffectsRender<WrapBar>(FractionProperty);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var radius = size.Height / 2;
        context.DrawRectangle(TrackBrush, null, new Rect(size), radius, radius);
        var fraction = Math.Clamp(double.IsNaN(Fraction) ? 0 : Fraction, 0, 1);
        if (fraction <= 0) return;
        var width = Math.Max(size.Height, size.Width * fraction);
        context.DrawRectangle(AccentBrush, null, new Rect(0, 0, width, size.Height), radius, radius);
    }
}

/// <summary>Vertical bars with a label under each (plays per month, or per day of a month).
/// The <see cref="Highlight"/> bar is full accent with its value on top; the rest are softer.</summary>
public sealed class WrapColumnChart : WrapChartBase
{
    public static readonly StyledProperty<IReadOnlyList<int>?> ValuesProperty =
        AvaloniaProperty.Register<WrapColumnChart, IReadOnlyList<int>?>(nameof(Values));
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<WrapColumnChart, IReadOnlyList<string>?>(nameof(Labels));
    public static readonly StyledProperty<int> HighlightProperty =
        AvaloniaProperty.Register<WrapColumnChart, int>(nameof(Highlight), -1);

    public IReadOnlyList<int>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<string>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public int Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    static WrapColumnChart() => AffectsRender<WrapColumnChart>(ValuesProperty, LabelsProperty, HighlightProperty);

    private const double LabelBand = 18;
    private const double ValueBand = 16;

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var size = Bounds.Size;
        if (values is not { Count: > 0 } || size.Width <= 0 || size.Height <= LabelBand + ValueBand) return;

        var max = Math.Max(1, values.Max());
        var slot = size.Width / values.Count;
        var barWidth = Math.Clamp(slot * 0.62, 3, 26);
        var plotHeight = size.Height - LabelBand - ValueBand;
        var baseline = ValueBand + plotHeight;
        var accent = AccentBrush;
        var soft = WithOpacity(accent, 0.45);
        var labelBrush = WithOpacity(TextBrush, 0.5);

        for (var i = 0; i < values.Count; i++)
        {
            var cx = slot * i + slot / 2;
            var x = cx - barWidth / 2;
            if (values[i] <= 0)
            {
                context.DrawRectangle(TrackBrush, null, new Rect(x, baseline - 3, barWidth, 3), 1.5, 1.5);
            }
            else
            {
                var height = Math.Max(barWidth > 6 ? 6 : 3, plotHeight * values[i] / max);
                var radius = Math.Min(barWidth / 2, 6);
                context.DrawRectangle(i == Highlight ? accent : soft, null,
                    new Rect(x, baseline - height, barWidth, height), radius, radius);
                if (i == Highlight)
                {
                    var value = Label(values[i].ToString("N0", CultureInfo.CurrentCulture), 10.5, TextBrush, FontWeight.SemiBold);
                    var vx = Math.Clamp(cx - value.Width / 2, 0, Math.Max(0, size.Width - value.Width));
                    context.DrawText(value, new Point(vx, baseline - height - value.Height - 1));
                }
            }

            var labels = Labels;
            if (labels != null && i < labels.Count && !string.IsNullOrEmpty(labels[i]))
            {
                var text = Label(labels[i], 10.5, labelBrush);
                var lx = Math.Clamp(cx - text.Width / 2, 0, Math.Max(0, size.Width - text.Width));
                context.DrawText(text, new Point(lx, baseline + 4));
            }
        }
    }
}

/// <summary>
/// The listening clock: 24 spokes around a ring, one per hour of the day, each as long as
/// that hour's share of the plays. Midnight at the top, running clockwise; the busiest hour
/// is full accent.
/// </summary>
public sealed class WrapClockChart : WrapChartBase
{
    public static readonly StyledProperty<IReadOnlyList<int>?> ValuesProperty =
        AvaloniaProperty.Register<WrapClockChart, IReadOnlyList<int>?>(nameof(Values));

    public IReadOnlyList<int>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    static WrapClockChart() => AffectsRender<WrapClockChart>(ValuesProperty);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var values = Values;
        if (values is not { Count: 24 } || size.Width <= 0 || size.Height <= 0) return;

        const double labelRoom = 16;
        var center = new Point(size.Width / 2, size.Height / 2);
        var outer = Math.Min(size.Width, size.Height) / 2 - labelRoom;
        if (outer <= 8) return;
        var inner = outer * 0.34;
        var max = Math.Max(1, values.Max());
        var peak = values.Max() > 0 ? IndexOfMax(values) : -1;

        var spoke = Math.Max(2, 2 * Math.PI * (inner + outer) / 2 / 24 * 0.5);
        var accent = AccentBrush;
        var soft = WithOpacity(accent, 0.5);
        var guide = new Pen(TrackBrush, 1);
        context.DrawEllipse(null, guide, center, outer, outer);
        context.DrawEllipse(null, guide, center, inner - spoke, inner - spoke);

        for (var hour = 0; hour < 24; hour++)
        {
            var angle = (hour * 15 - 90) * Math.PI / 180;
            var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
            var length = (outer - inner) * values[hour] / max;
            var from = center + dir * inner;
            var to = center + dir * (inner + Math.Max(0.5, length));
            var pen = new Pen(values[hour] <= 0 ? TrackBrush : hour == peak ? accent : soft, spoke, lineCap: PenLineCap.Round);
            context.DrawLine(pen, from, to);
        }

        var labelBrush = WithOpacity(TextBrush, 0.5);
        foreach (var hour in new[] { 0, 6, 12, 18 })
        {
            var angle = (hour * 15 - 90) * Math.PI / 180;
            var text = Label(hour.ToString("00", CultureInfo.InvariantCulture), 10.5, labelBrush);
            var at = center + new Vector(Math.Cos(angle), Math.Sin(angle)) * (outer + labelRoom / 2 + 2);
            context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
        }
    }

    private static int IndexOfMax(IReadOnlyList<int> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }
}
