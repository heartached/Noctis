using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Small "working" ring: a three-quarter arc turning once every 0.9 s, drawn in
/// <see cref="Foreground"/>. Driven by animation frames only while it is shown and
/// <see cref="IsActive"/>, so a hidden spinner costs nothing.
/// </summary>
public sealed class LoadingSpinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LoadingSpinner>();

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<LoadingSpinner, double>(nameof(Thickness), 2);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LoadingSpinner, bool>(nameof(IsActive), true);

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private const double SecondsPerTurn = 0.9;
    private double _angle;
    private bool _queued;
    private TimeSpan? _last;

    static LoadingSpinner()
    {
        AffectsRender<LoadingSpinner>(ForegroundProperty, ThicknessProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Queue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty) Queue();
    }

    private void Queue()
    {
        if (_queued || !IsActive || !IsEffectivelyVisible) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _queued = true;
        _last = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _queued = false;
        if (VisualRoot == null || !IsActive || !IsEffectivelyVisible) return;
        if (_last is { } last)
            _angle = (_angle + (now - last).TotalSeconds * 360 / SecondsPerTurn) % 360;
        _last = now;
        InvalidateVisual();
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _queued = true;
            top.RequestAnimationFrame(OnFrame);
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0 || Foreground is not { } brush) return;
        var t = Thickness;
        var r = size / 2 - t / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var a0 = _angle * Math.PI / 180;
        var a1 = a0 + 1.5 * Math.PI; // three quarters of a turn
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, t, lineCap: PenLineCap.Round), geo);
    }
}
