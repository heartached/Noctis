using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// The plaza: a quiet flat square where the called albums wander. One control draws everything.
/// <para>
/// Frames come from the compositor (<see cref="TopLevel.RequestAnimationFrame"/>), so the plaza
/// steps in time with the screen's refresh and with the real time between frames; a 33 ms
/// timer used to beat against the 60 Hz refresh and stutter. While the control is hidden, off
/// the visual tree or its window is minimized it stops asking for frames and a slow 4 Hz check
/// waits for it to come back.
/// </para>
/// <para>
/// The floor is drawn once into a bitmap (again only on resize, a new pixel density or theme),
/// each creature's tile is a prebuilt bitmap (<see cref="CreaturePainter"/>), and nothing in a
/// frame allocates, so the GC has nothing to pause for.
/// </para>
/// </summary>
public sealed class PlazaCanvas : Control
{
    public const double MinZoom = 0.6;
    public const double MaxZoom = 1.6;
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    private const double GridStep = 64;
    private static readonly Comparison<Creature> ByDepth = (a, b) => a.Position.Y.CompareTo(b.Position.Y);

    private readonly DispatcherTimer _idle;
    private readonly Action<TimeSpan> _onFrame;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Creature> _order = new(64);
    private readonly Cursor _arrow = new(StandardCursorType.Arrow);
    private readonly Cursor _hand = new(StandardCursorType.Hand);
    private readonly Queue<(IDisposable Item, long Frame)> _retired = new();
    private TimeSpan _last;
    private TimeSpan? _lastFrame;
    private bool _framePending;
    private bool _attached;
    private RenderTargetBitmap? _floor;
    private (Size Size, double Scaling, TownTheme Palette) _floorFor;
    private double _zoom = 1;

    public PlazaCanvas(PlazaWorld world, CoverCache covers)
    {
        World = world;
        Painter = new CreaturePainter(covers);
        covers.CoverReady += (_, _) => { if (!IsPaused) InvalidateVisual(); };
        ClipToBounds = true;
        Focusable = false;
        Cursor = _arrow;
        _onFrame = OnFrame;
        _idle = new DispatcherTimer(IdleInterval, DispatcherPriority.Background, (_, _) => CheckIdle());
    }

    public PlazaWorld World { get; }
    public CreaturePainter Painter { get; }
    public bool ShowNames { get; set; }
    /// <summary>Beat source (0..1), polled per frame; null = no beat (the dancer just dances).</summary>
    public Func<double?>? Beat { get; set; }
    public Creature? Hovered { get; private set; }
    /// <summary>True while frames are not being stepped or drawn.</summary>
    public bool IsPaused { get; private set; } = true;
    /// <summary>Frames stepped since attach (tests).</summary>
    public long Frames { get; private set; }

    /// <summary>The colours (the view passes the app's).</summary>
    public TownTheme Palette
    {
        get => Painter.Palette;
        set
        {
            Painter.Palette = value;
            InvalidateVisual();
        }
    }

    /// <summary>The user's size choice, <see cref="MinZoom"/> to <see cref="MaxZoom"/> (Ctrl+wheel or the header slider).</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            value = Math.Clamp(double.IsFinite(value) ? value : 1, MinZoom, MaxZoom);
            if (Math.Abs(value - _zoom) < 0.0001) return;
            _zoom = value;
            UpdateSize();
            InvalidateVisual();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A creature was clicked (it waves too).</summary>
    public event EventHandler<Creature>? CreatureClicked;
    /// <summary>Ctrl+wheel (or code) changed <see cref="Zoom"/>.</summary>
    public event EventHandler? ZoomChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _last = _clock.Elapsed;
        _lastFrame = null;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _idle.Stop();
        IsPaused = true;
        _floor?.Dispose();
        _floor = null;
        while (_retired.Count > 0) _retired.Dequeue().Item.Dispose();
        Painter.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Back from the Apartments tab: start right away instead of at the next idle check.
        if (change.Property == IsVisibleProperty && IsVisible && _attached) RequestFrame();
    }

    /// <summary>Hidden, collapsed, zero-sized or in a minimized window: nothing to animate.</summary>
    public bool ShouldPause()
        => !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1
           || TopLevel.GetTopLevel(this) is not { } top
           || top is Window { WindowState: WindowState.Minimized };

    private void RequestFrame()
    {
        if (_framePending || !_attached || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(_onFrame);
    }

    /// <summary>One compositor frame: steps by the real time since the last one and redraws.</summary>
    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        if (!_attached) return;
        if (ShouldPause())
        {
            GoIdle();
            return;
        }
        IsPaused = false;
        var dt = _lastFrame is { } last ? (float)(time - last).TotalSeconds : 1 / 60f;
        _lastFrame = time;
        _last = _clock.Elapsed;
        Step(dt);
        InvalidateVisual();
        RequestFrame();
    }

    private void GoIdle()
    {
        IsPaused = true;
        _lastFrame = null;
        if (_attached) _idle.Start();
    }

    private void CheckIdle()
    {
        if (!_attached) { _idle.Stop(); return; }
        if (ShouldPause()) return;
        _idle.Stop();
        RequestFrame();
    }

    /// <summary>One frame driven by hand (tests): steps by the real time since the last one.</summary>
    public void Tick()
    {
        var now = _clock.Elapsed;
        var dt = (float)(now - _last).TotalSeconds;
        _last = now;
        if (ShouldPause())
        {
            GoIdle();
            return;
        }
        IsPaused = false;
        Step(dt);
        InvalidateVisual();
    }

    /// <summary>Steps the simulation (also used by tests and the preview).</summary>
    public void Step(float dt)
    {
        World.Resize((float)Bounds.Width, (float)Bounds.Height);
        UpdateSize();
        World.Now = DateTimeOffset.Now;
        UpdateHover();
        var pulse = Beat?.Invoke() ?? 0;
        World.Step(dt, (float)Math.Clamp(pulse, 0, 1));
        Frames++;
    }

    /// <summary>
    /// Creature size: about 7% of the plaza's short side in device pixels (a ~96 px cover on a
    /// maximized 2560x1440 window), kept small enough that the crowd covers at most ~28% of the
    /// plaza, then the user's <see cref="Zoom"/>, which may grow them until about half of it.
    /// Snapped to whole device pixels so a resize does not rebuild every tile on every pixel.
    /// </summary>
    public static double SizeFor(double width, double height, double scaling, int crowd, double zoom)
    {
        if (width < 1 || height < 1) return PlazaWorld.BaseSize;
        scaling = scaling > 0 ? scaling : 1;
        var auto = Math.Clamp(Math.Min(width, height) * scaling * 0.071, 44, 132) / scaling;
        var n = Math.Max(12, crowd);
        var area = width * height;
        var roomy = Math.Sqrt(area * 0.28 / n) / 1.45;
        var crowded = Math.Sqrt(area * 0.5 / n) / 1.45;
        var size = Math.Max(20, Math.Min(Math.Min(auto, roomy) * zoom, Math.Max(crowded, 20)));
        return Math.Round(size * scaling / 2) * 2 / scaling;
    }

    private void UpdateSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Painter.Scaling = scaling;
        var size = SizeFor(Bounds.Width, Bounds.Height, scaling, World.Creatures.Count, _zoom);
        Painter.Size = size;
        World.BodySize = (float)size;
    }

    private void UpdateHover()
    {
        // Creatures walk under a still pointer too, so this runs every frame, not only on moves.
        var hit = World.Cursor is { } cur ? World.HitTest(cur, Hovered) : null;
        if (!ReferenceEquals(hit, Hovered))
        {
            Hovered = hit;
            Cursor = hit is null ? _arrow : _hand;
        }
        World.Held = hit;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        World.Cursor = new Vector2((float)p.X, (float)p.Y);
        UpdateHover();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        World.Cursor = null;
        UpdateHover();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (World.HitTest(new Vector2((float)p.X, (float)p.Y), Hovered) is not { } c) return;
        World.Wave(c);
        CreatureClicked?.Invoke(this, c);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || e.Delta.Y == 0) return;
        Zoom = Math.Round((_zoom + Math.Sign(e.Delta.Y) * 0.1) * 10) / 10;
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        Painter.BeginFrame();
        DrawFloor(context, bounds);

        _order.Clear();
        var creatures = World.Creatures;
        for (var i = 0; i < creatures.Count; i++) _order.Add(creatures[i]);
        _order.Sort(ByDepth);

        foreach (var c in _order) Painter.DrawShadow(context, c);
        foreach (var c in _order)
        {
            // Newcomers fade in over their first half second.
            if (c.Age < 0.5f)
                using (context.PushOpacity(Math.Clamp(c.Age * 2, 0, 1)))
                    Painter.DrawBody(context, c);
            else Painter.DrawBody(context, c);
        }
        foreach (var c in _order)
            if (ShowNames || ReferenceEquals(c, Hovered) || c.IsPlaying) Painter.DrawName(context, c);
        foreach (var c in _order) Painter.DrawBubble(context, c, bounds);
    }

    private void DrawFloor(DrawingContext ctx, Rect bounds)
    {
        var scaling = Painter.Scaling;
        if (_floor is null || _floorFor != (bounds.Size, scaling, Palette))
        {
            if (_floor is not null) _retired.Enqueue((_floor, Frames));
            _floor = BuildFloor(bounds.Size, scaling, Palette);
            _floorFor = (bounds.Size, scaling, Palette);
        }
        while (_retired.Count > 0 && Frames - _retired.Peek().Frame > 30) _retired.Dequeue().Item.Dispose();
        if (_floor is null) ctx.FillRectangle(Palette.FloorBrush, bounds);
        else ctx.DrawImage(_floor, new Rect(_floor.Size), bounds);
    }

    /// <summary>The floor: a flat surface, a faint grid and a soft light in the middle.</summary>
    private static RenderTargetBitmap? BuildFloor(Size size, double scaling, TownTheme theme)
    {
        var px = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        if (px.Width < 1 || px.Height < 1) return null;
        var bitmap = new RenderTargetBitmap(px, new Avalonia.Vector(96 * scaling, 96 * scaling));
        using var ctx = bitmap.CreateDrawingContext();
        var rect = new Rect(size);
        ctx.FillRectangle(theme.FloorBrush, rect);
        var glow = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.45, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.45, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(theme.FloorGlow, 0),
                new GradientStop(Color.FromArgb(0, theme.FloorGlow.R, theme.FloorGlow.G, theme.FloorGlow.B), 1),
            },
        };
        ctx.FillRectangle(glow, rect);
        // Hairline grid, centred, one device pixel wide.
        var line = 1 / scaling;
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(theme.FloorLine), line);
        var grid = new StreamGeometry();
        using (var g = grid.Open())
        {
            var ox = (size.Width / 2) % GridStep;
            var oy = (size.Height / 2) % GridStep;
            for (var x = ox; x < size.Width; x += GridStep)
            {
                var sx = Math.Round(x * scaling) / scaling + line / 2;
                g.BeginFigure(new Point(sx, 0), false);
                g.LineTo(new Point(sx, size.Height));
                g.EndFigure(false);
            }
            for (var y = oy; y < size.Height; y += GridStep)
            {
                var sy = Math.Round(y * scaling) / scaling + line / 2;
                g.BeginFigure(new Point(0, sy), false);
                g.LineTo(new Point(size.Width, sy));
                g.EndFigure(false);
            }
        }
        ctx.DrawGeometry(null, pen, grid);
        return bitmap;
    }
}
