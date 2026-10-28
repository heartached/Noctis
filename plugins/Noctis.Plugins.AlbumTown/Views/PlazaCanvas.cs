using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// The plaza: a top-down tiled square (after the Mii plaza) where the called albums wander. One
/// control draws everything; a 30 fps timer steps <see cref="PlazaWorld"/> and redraws, and goes
/// quiet (no stepping, no drawing, a slow 4 Hz check) while the control is hidden, off the
/// visual tree or its window is minimized.
/// </summary>
public sealed class PlazaCanvas : Control
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    private const double Tile = 46;

    private static readonly IBrush PlazaLight = new ImmutableSolidColorBrush(Color.FromRgb(0xEE, 0xEB, 0xF2));
    private static readonly IBrush PlazaDark = new ImmutableSolidColorBrush(Color.FromRgb(0xE1, 0xDD, 0xE8));
    private static readonly IBrush RingBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xD6, 0xD0, 0xE0));
    private static readonly IBrush RingInner = new ImmutableSolidColorBrush(Color.FromRgb(0xF6, 0xF3, 0xF8));
    private static readonly IPen RingPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xCB, 0xC4, 0xD6)), 2);
    private static readonly IPen GroutPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0xC8, 0xC1, 0xD3)), 1);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last;
    private StreamGeometry? _tiles;
    private Size _tilesFor;
    private readonly List<Creature> _order = new(64);

    public PlazaCanvas(PlazaWorld world, CoverCache covers)
    {
        World = world;
        Painter = new CreaturePainter(covers);
        covers.CoverReady += (_, _) => { if (!IsPaused) InvalidateVisual(); };
        ClipToBounds = true;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Normal, (_, _) => Tick());
    }

    public PlazaWorld World { get; }
    public CreaturePainter Painter { get; }
    public bool ShowNames { get; set; }
    /// <summary>Beat source (0..1), polled per frame; null = no beat (the dancer just dances).</summary>
    public Func<double?>? Beat { get; set; }
    public Creature? Hovered { get; private set; }
    /// <summary>True while frames are not being stepped or drawn.</summary>
    public bool IsPaused { get; private set; } = true;
    /// <summary>Frames stepped and drawn since attach (tests).</summary>
    public long Frames { get; private set; }

    /// <summary>A creature was clicked (it waves too).</summary>
    public event EventHandler<Creature>? CreatureClicked;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _last = _clock.Elapsed;
        _timer.Interval = FrameInterval;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        IsPaused = true;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Hidden, collapsed, zero-sized or in a minimized window: nothing to animate.</summary>
    public bool ShouldPause()
        => !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1
           || TopLevel.GetTopLevel(this) is not { } top
           || top is Window { WindowState: WindowState.Minimized };

    /// <summary>One timer tick: steps the world by the real time since the last tick and redraws.</summary>
    public void Tick()
    {
        var now = _clock.Elapsed;
        var dt = (float)(now - _last).TotalSeconds;
        _last = now;
        var pause = ShouldPause();
        if (pause != IsPaused)
        {
            IsPaused = pause;
            _timer.Interval = pause ? IdleInterval : FrameInterval;
        }
        if (pause) return;
        Step(dt);
        InvalidateVisual();
    }

    /// <summary>Steps the simulation (also used by tests and the preview).</summary>
    public void Step(float dt)
    {
        World.Resize((float)Bounds.Width, (float)Bounds.Height);
        // Creatures grow a little on big windows, never past what 60 of them can share.
        Painter.Size = Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) / 22, 26, 38);
        World.Now = DateTimeOffset.Now;
        var pulse = Beat?.Invoke() ?? 0;
        World.Step(dt, (float)Math.Clamp(pulse, 0, 1));
        Frames++;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        World.Cursor = new Vector2((float)p.X, (float)p.Y);
        var hit = World.HitTest(World.Cursor.Value);
        if (!ReferenceEquals(hit, Hovered))
        {
            Hovered = hit;
            Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        World.Cursor = null;
        Hovered = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (World.HitTest(new Vector2((float)p.X, (float)p.Y)) is not { } c) return;
        World.Wave(c);
        CreatureClicked?.Invoke(this, c);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        DrawPlaza(context, bounds);

        _order.Clear();
        _order.AddRange(World.Creatures);
        _order.Sort((a, b) => a.Position.Y.CompareTo(b.Position.Y));

        foreach (var c in _order) Painter.DrawShadow(context, c);
        foreach (var c in _order)
        {
            // Newcomers fade in over their first half second.
            if (c.Age < 0.5f)
                using (context.PushOpacity(Math.Clamp(c.Age * 2, 0, 1)))
                {
                    Painter.DrawGlow(context, c);
                    Painter.DrawBody(context, c);
                }
            else
            {
                Painter.DrawGlow(context, c);
                Painter.DrawBody(context, c);
            }
        }
        foreach (var c in _order)
            if (ShowNames || ReferenceEquals(c, Hovered) || c.IsPlaying) Painter.DrawName(context, c);
        foreach (var c in _order) Painter.DrawBubble(context, c, bounds);
    }

    private void DrawPlaza(DrawingContext ctx, Rect bounds)
    {
        ctx.FillRectangle(PlazaLight, bounds);
        if (_tiles is null || _tilesFor != bounds.Size)
        {
            _tiles = BuildTiles(bounds.Size);
            _tilesFor = bounds.Size;
        }
        ctx.DrawGeometry(PlazaDark, GroutPen, _tiles);
        // A paved ring in the middle, like a town square.
        var c = bounds.Center;
        var r = Math.Min(bounds.Width, bounds.Height) * 0.2;
        ctx.DrawEllipse(RingBrush, RingPen, c, r * 1.35, r);
        ctx.DrawEllipse(RingInner, RingPen, c, r * 1.35 - 14, r - 10);
    }

    /// <summary>Checkerboard tiles as one geometry (one draw call), rebuilt only on resize.</summary>
    private static StreamGeometry BuildTiles(Size size)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var cols = (int)Math.Ceiling(size.Width / Tile) + 1;
        var rows = (int)Math.Ceiling(size.Height / Tile) + 1;
        for (var y = 0; y < rows; y++)
            for (var x = y % 2; x < cols; x += 2)
            {
                var rx = x * Tile;
                var ry = y * Tile;
                g.BeginFigure(new Point(rx, ry), true);
                g.LineTo(new Point(rx + Tile, ry));
                g.LineTo(new Point(rx + Tile, ry + Tile));
                g.LineTo(new Point(rx, ry + Tile));
                g.EndFigure(true);
            }
        return geo;
    }
}
