using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// AMLL-style fluid mesh background. Renders three large, soft-edged radial colour
/// blobs that slowly drift on independent sine paths over a base colour. The result
/// is a slow, organic colour-cloud effect tuned to read like Apple Music's lyric page.
///
/// All four colours are bindable so callers can derive them from the current album art.
/// Animation runs at ~30 FPS only while attached to the visual tree.
/// </summary>
public class FluidMeshBackground : Control
{
    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<FluidMeshBackground, Color>(nameof(BaseColor), Color.FromRgb(0x0D, 0x1B, 0x2A));

    public static readonly StyledProperty<Color> BlobColor1Property =
        AvaloniaProperty.Register<FluidMeshBackground, Color>(nameof(BlobColor1), Color.FromRgb(0x3A, 0x1C, 0x71));

    public static readonly StyledProperty<Color> BlobColor2Property =
        AvaloniaProperty.Register<FluidMeshBackground, Color>(nameof(BlobColor2), Color.FromRgb(0xD7, 0x6D, 0x77));

    public static readonly StyledProperty<Color> BlobColor3Property =
        AvaloniaProperty.Register<FluidMeshBackground, Color>(nameof(BlobColor3), Color.FromRgb(0xFF, 0xAF, 0x7B));

    /// <summary>Strength of each blob, 0..1. Lower = subtler colour wash.</summary>
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<FluidMeshBackground, double>(nameof(Intensity), 0.55);

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color BlobColor1
    {
        get => GetValue(BlobColor1Property);
        set => SetValue(BlobColor1Property, value);
    }

    public Color BlobColor2
    {
        get => GetValue(BlobColor2Property);
        set => SetValue(BlobColor2Property, value);
    }

    public Color BlobColor3
    {
        get => GetValue(BlobColor3Property);
        set => SetValue(BlobColor3Property, value);
    }

    public double Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    private DispatcherTimer? _timer;
    private double _phase;

    static FluidMeshBackground()
    {
        AffectsRender<FluidMeshBackground>(BaseColorProperty, BlobColor1Property,
            BlobColor2Property, BlobColor3Property, IntensityProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Slow drift — full cycle takes ~30 seconds per axis.
        _phase += 0.0035;
        if (_phase > Math.Tau * 100) _phase -= Math.Tau * 100;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Base fill.
        ctx.FillRectangle(new SolidColorBrush(BaseColor), bounds);

        var intensity = Math.Clamp(Intensity, 0.0, 1.0);
        var alpha = (byte)Math.Round(intensity * 0xC8);

        DrawBlob(ctx, bounds, BlobColor1, alpha, _phase, 0.0, 0.62, 0.45);
        DrawBlob(ctx, bounds, BlobColor2, alpha, _phase, 2.1, 0.58, 0.55);
        DrawBlob(ctx, bounds, BlobColor3, alpha, _phase, 4.2, 0.66, 0.40);
    }

    /// <summary>
    /// Draws one soft radial blob whose centre oscillates on independent sin/cos paths.
    /// Edge is fully transparent so blobs blend additively without ring artefacts.
    /// </summary>
    private static void DrawBlob(DrawingContext ctx, Rect bounds, Color color, byte alpha,
        double phase, double phaseOffset, double radiusFraction, double driftAmplitude)
    {
        var t = phase + phaseOffset;

        // Centre drifts on lissajous-like figure; amplitude scaled by control size.
        var cx = bounds.Width * (0.5 + driftAmplitude * Math.Sin(t * 0.71));
        var cy = bounds.Height * (0.5 + driftAmplitude * 0.82 * Math.Cos(t * 0.53));
        var radius = Math.Max(bounds.Width, bounds.Height) * radiusFraction;

        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0.0),
                new GradientStop(Color.FromArgb((byte)(alpha / 3), color.R, color.G, color.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
            },
        };

        ctx.DrawEllipse(brush, null, new Rect(cx - radius, cy - radius, radius * 2, radius * 2));
    }
}
