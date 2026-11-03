using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Noctis.Plugins.ABLoop.Views;

/// <summary>
/// The thin progress strip: the song as a line, the looped stretch shaded in the accent, the
/// playhead, and the A and B markers, which can be dragged. A click elsewhere seeks there.
/// </summary>
public sealed class LoopStrip : Control
{
    public const double Pad = 12;
    private const double MarkerRadius = 9;
    private const double GrabDistance = 14;

    private LoopTheme _palette = LoopTheme.Dark;
    private FormattedText? _aText;
    private FormattedText? _bText;
    private bool? _dragging; // true = A, false = B, null = none
    private TimeSpan _position;

    public LoopStrip()
    {
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public TimeSpan Duration { get; set; }
    public TimeSpan? A { get; set; }
    public TimeSpan? B { get; set; }

    /// <summary>The playhead. Setting it redraws only when it moved at least half a pixel.</summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            var moved = Math.Abs(X(value) - X(_position)) >= 0.5;
            _position = value;
            if (moved) InvalidateVisual();
        }
    }

    public LoopTheme Palette
    {
        get => _palette;
        set
        {
            _palette = value;
            _aText = _bText = null;
            InvalidateVisual();
        }
    }

    /// <summary>A marker was dragged (true = A) to a time; raised while dragging and on release.</summary>
    public event EventHandler<(bool IsA, TimeSpan At)>? MarkerMoved;
    /// <summary>The strip was clicked away from the markers: seek there.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    public bool IsDragging => _dragging is not null;

    /// <summary>X of <paramref name="t"/> on the strip.</summary>
    public double X(TimeSpan t)
    {
        var w = Math.Max(1, Bounds.Width - 2 * Pad);
        var d = Duration.TotalSeconds;
        return Pad + (d > 0 ? Math.Clamp(t.TotalSeconds / d, 0, 1) : 0) * w;
    }

    /// <summary>The time at strip x.</summary>
    public TimeSpan At(double x)
    {
        var w = Math.Max(1, Bounds.Width - 2 * Pad);
        return TimeSpan.FromSeconds(Math.Clamp((x - Pad) / w, 0, 1) * Math.Max(0, Duration.TotalSeconds));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= TimeSpan.Zero || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var x = e.GetPosition(this).X;
        var da = A is { } a ? Math.Abs(X(a) - x) : double.MaxValue;
        var db = B is { } b ? Math.Abs(X(b) - x) : double.MaxValue;
        if (Math.Min(da, db) <= GrabDistance)
        {
            _dragging = da <= db;
            e.Pointer.Capture(this);
        }
        else SeekRequested?.Invoke(this, At(x));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is not { } isA) return;
        MarkerMoved?.Invoke(this, (isA, At(e.GetPosition(this).X)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is not { } isA) return;
        _dragging = null;
        e.Pointer.Capture(null);
        MarkerMoved?.Invoke(this, (isA, At(e.GetPosition(this).X)));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = null;
        base.OnPointerCaptureLost(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 * Pad + 1) return;
        var mid = h * 0.62;
        var p = _palette;
        ctx.DrawRectangle(p.Track, null, new RoundedRect(new Rect(Pad, mid - 2, w - 2 * Pad, 4), 2));
        if (Duration > TimeSpan.Zero)
            ctx.DrawRectangle(p.Played, null, new RoundedRect(new Rect(Pad, mid - 2, Math.Max(0, X(_position) - Pad), 4), 2));
        if (A is { } a && B is { } b && b > a)
        {
            var xa = X(a);
            var xb = X(b);
            ctx.DrawRectangle(p.Shade, null, new RoundedRect(new Rect(xa, mid - 7, Math.Max(1, xb - xa), 14), 3));
            ctx.DrawRectangle(p.AccentBrush, null, new RoundedRect(new Rect(xa, mid - 2, Math.Max(1, xb - xa), 4), 2));
        }
        if (Duration > TimeSpan.Zero)
        {
            var xp = X(_position);
            ctx.DrawRectangle(p.Playhead, null, new RoundedRect(new Rect(xp - 1, mid - 9, 2, 18), 1));
        }
        if (A is { } ma) Marker(ctx, X(ma), mid, _aText ??= Letter("A"));
        if (B is { } mb) Marker(ctx, X(mb), mid, _bText ??= Letter("B"));
    }

    private void Marker(DrawingContext ctx, double x, double mid, FormattedText letter)
    {
        var p = _palette;
        var cy = MarkerRadius + 1;
        ctx.DrawRectangle(p.AccentBrush, null, new Rect(x - 1, cy, 2, mid - cy));
        ctx.DrawEllipse(p.AccentBrush, p.MarkerPen, new Point(x, cy), MarkerRadius, MarkerRadius);
        ctx.DrawText(letter, new Point(x - letter.Width / 2, cy - letter.Height / 2));
    }

    private FormattedText Letter(string s)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10.5, _palette.OnAccent);
}
