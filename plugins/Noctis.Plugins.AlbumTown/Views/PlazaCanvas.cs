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
/// The plaza: a quiet flat square where the called albums wander.
/// <para>
/// Drawn so a frame only repaints what changed. The floor is one bitmap drawn by this control
/// and never redrawn while the plaza lives; every creature, name tag and speech bubble is a small
/// child visual of its own that moves by a translation. The compositor then repaints only the old
/// and new rectangles of what moved (and of creatures whose pose changed: a step, a blink),
/// instead of the whole 2560x1440 plaza 60 times a second (1.1 used ~60% of a core for that).
/// </para>
/// <para>
/// Frames come from <see cref="TopLevel.RequestAnimationFrame"/> and are let through at the
/// "Motion" setting's rate (60, 30 or 15 per second; 15 while the window is not in front). The
/// simulation is timed by the clock, so creatures walk at the same speed at every rate. When
/// nobody moves or speaks the plaza sleeps (no frames at all) until the first one sets off or the
/// pointer, the music or the crowd changes. Hidden, off the tree or minimized it stops, and a slow
/// 4 Hz check waits for it to come back.
/// </para>
/// <para>
/// Name tags and bubbles keep out of each other's way: a tag that would sit on a neighbour moves
/// down or (if it is nobody's focus) hides; a bubble that would cover another flips under its
/// creature's feet, then nudges up, and is dropped when there is no room. Nothing in a frame allocates.
/// </para>
/// </summary>
public sealed class PlazaCanvas : Control
{
    public const double MinZoom = 0.6;
    public const double MaxZoom = 1.6;
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    private const double GridStep = 64;
    /// <summary>The longest sleep before the plaza checks in anyway (s).</summary>
    private const float MaxSleep = 30;

    private readonly DispatcherTimer _idle;
    private readonly DispatcherTimer _wake;
    private readonly Action<TimeSpan> _onFrame;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FrameGate _gate = new();
    private readonly Cursor _arrow = new(StandardCursorType.Arrow);
    private readonly Cursor _hand = new(StandardCursorType.Hand);
    private readonly Queue<(IDisposable Item, long Frame)> _retired = new();
    private readonly List<BodyVisual> _bodies = new(64);
    private readonly List<LabelVisual> _names = new(64);
    private readonly List<LabelVisual> _bubbles = new(64);
    private Rect[] _bodyRects = new Rect[64];
    private Rect[] _placed = new Rect[128];
    private TimeSpan _last;
    private TimeSpan _sleptAt;
    private bool _framePending;
    private bool _attached;
    private RenderTargetBitmap? _floor;
    private (Size Size, double Scaling, TownTheme Palette) _floorFor;
    private double _zoom = 1;
    private int _worldVersion = -1;
    private (double Size, double Scaling, TownTheme? Theme) _bodyFor;
    // A creature's visual: compact (tile, hands, feet, shadow, a waving hand); the dancer's is
    // taller and wider for its notes. Small visuals keep the repainted area small.
    private Size _bodySize;
    private Point _feet;
    private Size _danceSize;
    private Point _danceFeet;
    private bool _showNames;

    public PlazaCanvas(PlazaWorld world, CoverCache covers)
    {
        World = world;
        Painter = new CreaturePainter(covers);
        covers.CoverReady += (_, _) =>
        {
            // A cover arrived: tiles drawn without it get redrawn once theirs is built.
            foreach (var b in _bodies) b.TileOk = false;
            if (!IsPaused || IsAsleep) Wake();
        };
        ClipToBounds = true;
        Focusable = false;
        Cursor = _arrow;
        _onFrame = OnFrame;
        _idle = new DispatcherTimer(IdleInterval, DispatcherPriority.Background, (_, _) => CheckIdle());
        _wake = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Wake());
    }

    public PlazaWorld World { get; }
    public CreaturePainter Painter { get; }

    public bool ShowNames
    {
        get => _showNames;
        set
        {
            if (_showNames == value) return;
            _showNames = value;
            Wake();
        }
    }

    /// <summary>Beat source (0..1), polled per frame; null = no beat (the dancer just dances).</summary>
    public Func<double?>? Beat { get; set; }
    public Creature? Hovered { get; private set; }
    /// <summary>True while frames are not being stepped or drawn (hidden, minimized).</summary>
    public bool IsPaused { get; private set; } = true;
    /// <summary>True while nothing moves and no frames are asked for (see <see cref="World"/>.IsQuiet).</summary>
    public bool IsAsleep { get; private set; }
    /// <summary>Frames stepped since attach (tests).</summary>
    public long Frames { get; private set; }
    /// <summary>Frames the screen offered (stepped or let pass by the Motion rate; tests).</summary>
    public long FramesOffered { get; private set; }

    /// <summary>The "Motion" setting: how many frames a second are drawn.</summary>
    public MotionMode Motion { get; set; } = MotionMode.Balanced;

    /// <summary>Tests: treat the window as in front (headless windows may not say so).</summary>
    public bool? FocusOverride { get; set; }

    /// <summary>Tests: the 1.1 way, every frame repaints the whole plaza at 60 fps (the baseline the
    /// benchmark compares against).</summary>
    public bool ForceFullRedraw { get; set; }

    /// <summary>Is the window in front (it gets the full frame rate)?</summary>
    public bool IsWindowFocused => FocusOverride ?? (TopLevel.GetTopLevel(this) is not Window w || w.IsActive);

    /// <summary>Time between drawn frames right now.</summary>
    public TimeSpan FrameInterval => ForceFullRedraw ? TimeSpan.FromSeconds(1.0 / 60) : Town.Motion.Interval(Motion, IsWindowFocused);

    /// <summary>The colours (the view passes the app's).</summary>
    public TownTheme Palette
    {
        get => Painter.Palette;
        set
        {
            if (ReferenceEquals(value, Painter.Palette)) return;
            Painter.Palette = value;
            InvalidateVisual();
            foreach (var l in _names) l.InvalidateVisual();
            foreach (var l in _bubbles) l.InvalidateVisual();
            Wake();
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
            Wake();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A creature was clicked (it waves too).</summary>
    public event EventHandler<Creature>? CreatureClicked;
    /// <summary>Ctrl+wheel (or code) changed <see cref="Zoom"/>.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>The creatures' and labels' visuals (tests).</summary>
    public IReadOnlyList<Control> CreatureVisuals => _bodies;
    public IReadOnlyList<Control> NameVisuals => _names;
    public IReadOnlyList<Control> BubbleVisuals => _bubbles;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _last = _clock.Elapsed;
        _gate.Reset();
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _idle.Stop();
        _wake.Stop();
        IsPaused = true;
        IsAsleep = false;
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
        if (change.Property == IsVisibleProperty && IsVisible && _attached) Wake();
    }

    /// <summary>Hidden, collapsed, zero-sized or in a minimized window: nothing to animate.</summary>
    public bool ShouldPause()
        => !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1
           || TopLevel.GetTopLevel(this) is not { } top
           || top is Window { WindowState: WindowState.Minimized };

    /// <summary>Something changed (the crowd, the music, a setting, the pointer): draw again now.</summary>
    public void Wake()
    {
        if (IsAsleep)
        {
            _wake.Stop();
            IsAsleep = false;
            World.Advance((float)(_clock.Elapsed - _sleptAt).TotalSeconds);
            _gate.Reset();
        }
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_framePending || !_attached || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(_onFrame);
    }

    /// <summary>Tests: frames come only from <see cref="OfferFrame"/> (headless animation frames stop
    /// arriving after a few), the compositor's own work still runs on its timer.</summary>
    public bool ManualFrames { get; set; }

    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        if (ManualFrames) return;
        OfferFrame();
    }

    /// <summary>One screen frame: let through at the Motion rate (the canvas's own clock), steps by
    /// the real time since the last one let through, moves the visuals; sleeps when nothing moves.</summary>
    public void OfferFrame()
    {
        FramesOffered++;
        if (!_attached || IsAsleep) return;
        if (ShouldPause())
        {
            GoIdle();
            return;
        }
        IsPaused = false;
        if (!_gate.Pass(_clock.Elapsed, FrameInterval, out var dt))
        {
            RequestFrame();
            return;
        }
        _last = _clock.Elapsed;
        Step(dt);
        Sync();
        if (ForceFullRedraw) RepaintAll();
        if (!ForceFullRedraw && Hovered is null && World.IsQuiet()) Sleep();
        else RequestFrame();
    }

    private void Sleep()
    {
        IsAsleep = true;
        _sleptAt = _clock.Elapsed;
        var wait = Math.Clamp(World.NextChangeIn(), 0.05f, MaxSleep);
        _wake.Interval = TimeSpan.FromSeconds(wait);
        _wake.Start();
    }

    private void GoIdle()
    {
        IsPaused = true;
        _gate.Reset();
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
        Sync();
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
        Wake();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        World.Cursor = null;
        UpdateHover();
        Wake();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (World.HitTest(new Vector2((float)p.X, (float)p.Y), Hovered) is not { } c) return;
        World.Wave(c);
        Wake();
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

    // ── The visuals ──

    /// <summary>
    /// Brings the visuals in line with the world: one per creature (and its tag and bubble),
    /// moved by translation; a creature's visual is repainted only when its pose changed.
    /// </summary>
    public void Sync()
    {
        Painter.BeginFrame();
        DirtyCount = 0;
        RepaintCount = 0;
        var creatures = World.Creatures;
        if (_worldVersion != World.Version || _bodies.Count != creatures.Count) RebuildVisuals();
        var scaling = Painter.Scaling;
        if (_bodyFor != (Painter.Size, scaling, Painter.Palette))
        {
            // New size, density or theme: every creature visual is resized and redrawn.
            _bodyFor = (Painter.Size, scaling, Painter.Palette);
            var s = Painter.Size;
            _bodySize = new Size(Math.Ceiling(s * 1.75 * scaling) / scaling, Math.Ceiling(s * 1.9 * scaling) / scaling);
            _feet = new Point(Math.Round(_bodySize.Width / 2 * scaling) / scaling, _bodySize.Height - Math.Ceiling(s * 0.16 * scaling) / scaling);
            _danceSize = new Size(Math.Ceiling(s * 2.4 * scaling) / scaling, Math.Ceiling(s * 2.75 * scaling) / scaling);
            _danceFeet = new Point(Math.Round(_danceSize.Width / 2 * scaling) / scaling, _danceSize.Height - Math.Ceiling(s * 0.16 * scaling) / scaling);
            foreach (var b in _bodies)
            {
                b.InvalidateMeasure();
                b.TileOk = false;
            }
        }

        var size = Painter.Size;
        for (var i = 0; i < creatures.Count; i++)
        {
            var c = creatures[i];
            var body = _bodies[i];
            Painter.Touch(c);
            var t = body.Translation;
            var was = new Rect(new Point(t.X, t.Y), body.Big ? _danceSize : _bodySize);
            var changed = false;
            if (body.Big != c.IsPlaying)
            {
                body.Big = c.IsPlaying;
                body.InvalidateMeasure();
                changed = true;
            }
            var feet = body.Big ? _danceFeet : _feet;
            MoveTo(t, Snap(c.Position.X, scaling) - feet.X, Snap(c.Position.Y, scaling) - feet.Y);
            changed |= t.X != was.X || t.Y != was.Y;
            var opacity = c.Age < 0.5f ? Math.Clamp(c.Age * 2, 0, 1) : 1;
            if (Math.Abs(body.Opacity - opacity) > 0.001) { body.Opacity = opacity; changed = true; }
            // Front to back by the feet; in coarse steps, so walking does not reorder every frame.
            var z = (int)(c.Position.Y / Math.Max(4, size * 0.4));
            if (body.ZIndex != z) { body.ZIndex = z; changed = true; }
            var pose = Pose(c);
            if (pose != body.Pose || !body.TileOk)
            {
                body.Pose = pose;
                body.InvalidateVisual();
                Repainted(body);
                changed = true;
            }
            if (changed) Dirty(was, new Rect(new Point(t.X, t.Y), body.Big ? _danceSize : _bodySize));
        }
        LayoutLabels();
    }

    /// <summary>Everything about how a creature looks right now except where it stands.</summary>
    private static int Pose(Creature c)
    {
        var moving = c.Stride > 0.001f || c.State is CreatureState.Dance or CreatureState.Wave || (c.State == CreatureState.Talk && c.Bubble is not null);
        var phase = moving ? (int)(c.WalkPhase * 8) : 0;
        var talk = c.State == CreatureState.Talk && c.Bubble is not null ? (int)(c.Age * 14) : 0;
        var notes = c.IsPlaying ? (int)(c.Age * 30) : 0;
        return HashCode.Combine(
            HashCode.Combine((int)c.State, phase, (int)(c.Stride * 16), c.BlinkTimer < 0, (int)(c.Facing)),
            (int)MathF.Round(c.Gaze.X * 10), (int)MathF.Round(c.Gaze.Y * 10),
            c.IsPlaying, (int)(c.Pulse * 12), talk, notes);
    }

    private void RebuildVisuals()
    {
        _worldVersion = World.Version;
        foreach (var v in _bodies) VisualChildren.Remove(v);
        foreach (var v in _names) VisualChildren.Remove(v);
        foreach (var v in _bubbles) VisualChildren.Remove(v);
        _bodies.Clear();
        _names.Clear();
        _bubbles.Clear();
        foreach (var c in World.Creatures)
        {
            var body = new BodyVisual(this, c);
            var name = new LabelVisual(this, c, bubble: false);
            var bubble = new LabelVisual(this, c, bubble: true);
            _bodies.Add(body);
            _names.Add(name);
            _bubbles.Add(bubble);
            VisualChildren.Add(body);
        }
        // Labels above every creature: tags, then bubbles on top.
        foreach (var n in _names) VisualChildren.Add(n);
        foreach (var b in _bubbles) VisualChildren.Add(b);
        if (_bodyRects.Length < World.Creatures.Count) _bodyRects = new Rect[World.Creatures.Count * 2];
        if (_placed.Length < World.Creatures.Count * 2) _placed = new Rect[World.Creatures.Count * 4];
        InvalidateMeasure();
    }

    /// <summary>
    /// Places the name tags and bubbles so they do not sit on each other or (tags) on a
    /// neighbour: hovered and playing first, a tag that collides moves down a little or hides, a
    /// bubble that collides flips under the feet, then nudges up, else waits for room.
    /// </summary>
    private void LayoutLabels()
    {
        var bounds = new Rect(Bounds.Size);
        Painter.AreaWidth = bounds.Width;
        var creatures = World.Creatures;
        var s = Painter.Size;
        for (var i = 0; i < creatures.Count; i++)
        {
            var p = creatures[i].Position;
            _bodyRects[i] = new Rect(p.X - s * 0.72, p.Y - s * 1.42, s * 1.44, s * 1.5);
        }
        var placed = 0;

        // Name tags: the focused ones (hovered, playing) first, they always show.
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < creatures.Count; i++)
            {
                var c = creatures[i];
                var focus = ReferenceEquals(c, Hovered) || c.IsPlaying;
                if (pass == 0 != focus) continue;
                var label = _names[i];
                if (!(ShowNames || focus) || Painter.NameBounds(c, bounds) is not { } rect)
                {
                    Hide(label);
                    continue;
                }
                if (!focus && Collides(rect, i, placed))
                {
                    // A little lower, clear of the neighbour; else it only shows on hover.
                    var lower = rect.WithY(Math.Min(bounds.Bottom - rect.Height - 4, rect.Y + s * 0.35));
                    if (Collides(lower, i, placed)) { Hide(label); continue; }
                    rect = lower;
                }
                _placed[placed++] = rect;
                Show(label, rect, null);
            }

        // Bubbles: the hovered one first, then the openers (they speak first), then the rest.
        for (var pass = 0; pass < 3; pass++)
            for (var i = 0; i < creatures.Count; i++)
            {
                var c = creatures[i];
                var rank = ReferenceEquals(c, Hovered) ? 0 : c.Opened ? 1 : 2;
                if (rank != pass) continue;
                var label = _bubbles[i];
                if (c.Bubble is not { } line || Painter.BubbleBounds(c, bounds) is not { } rect)
                {
                    Hide(label);
                    continue;
                }
                if (Overlaps(rect, placed))
                {
                    var under = rect.WithY(Math.Clamp(Painter.UnderFeet(c), bounds.Y + 4, Math.Max(bounds.Y + 4, bounds.Bottom - rect.Height - 4)));
                    if (!Overlaps(under, placed)) rect = under;
                    else
                    {
                        var up = rect.WithY(Above(rect, placed) - rect.Height - 2);
                        if (up.Y < bounds.Y + 4 || Overlaps(up, placed)) { Hide(label); continue; } // no room: it waits
                        rect = up;
                    }
                }
                _placed[placed++] = rect;
                Show(label, rect, line);
            }
    }

    /// <summary>True when <paramref name="r"/> overlaps another creature's body (not <paramref name="self"/>) or a placed label.</summary>
    private bool Collides(Rect r, int self, int placed)
    {
        var creatures = World.Creatures.Count;
        for (var j = 0; j < creatures; j++)
            if (j != self && _bodyRects[j].Intersects(r)) return true;
        return Overlaps(r, placed);
    }

    private bool Overlaps(Rect r, int placed)
    {
        for (var j = 0; j < placed; j++)
            if (_placed[j].Intersects(r)) return true;
        return false;
    }

    /// <summary>The top of the highest placed label that <paramref name="r"/> overlaps.</summary>
    private double Above(Rect r, int placed)
    {
        var top = r.Y;
        for (var j = 0; j < placed; j++)
            if (_placed[j].Intersects(r)) top = Math.Min(top, _placed[j].Y);
        return top;
    }

    private void Show(LabelVisual label, Rect rect, string? line)
    {
        var scaling = Painter.Scaling;
        var t = label.Translation;
        var was = label.IsVisible ? new Rect(t.X, t.Y, label.Built.Width, label.Built.Height) : default;
        var changed = !label.IsVisible;
        if (!ReferenceEquals(label.Line, line) || label.Built != rect.Size)
        {
            if (label.Built != rect.Size) label.InvalidateMeasure();
            label.Line = line;
            label.Built = rect.Size;
            label.InvalidateVisual();
            Repainted(label);
            changed = true;
        }
        var x = Snap(rect.X, scaling);
        var y = Snap(rect.Y, scaling);
        changed |= t.X != x || t.Y != y;
        MoveTo(t, x, y);
        if (!label.IsVisible) label.IsVisible = true;
        label.Rect = rect;
        if (changed) Dirty(was, new Rect(x, y, rect.Width, rect.Height));
    }

    private void Hide(LabelVisual label)
    {
        if (!label.IsVisible) return;
        label.IsVisible = false;
        var t = label.Translation;
        Dirty(new Rect(t.X, t.Y, label.Built.Width, label.Built.Height), default);
    }

    // ── What a frame changes (the benchmark repaints exactly this, like the compositor) ──

    private Rect[] _dirty = new Rect[256];
    private Control[] _repainted = new Control[192];

    /// <summary>Rectangles the last <see cref="Sync"/> changed: the old and new places of every
    /// visual that moved, appeared, vanished or was repainted. Everything else on screen stayed.</summary>
    public int DirtyCount { get; private set; }
    public Rect DirtyAt(int i) => _dirty[i];
    /// <summary>Visuals the last <see cref="Sync"/> asked to repaint (their content changed).</summary>
    public int RepaintCount { get; private set; }
    public Control RepaintedAt(int i) => _repainted[i];

    private void Dirty(Rect a, Rect b)
    {
        if (DirtyCount + 2 > _dirty.Length) Array.Resize(ref _dirty, _dirty.Length * 2);
        if (a.Width > 0 && a.Height > 0) _dirty[DirtyCount++] = a;
        if (b.Width > 0 && b.Height > 0) _dirty[DirtyCount++] = b;
    }

    private void Repainted(Control c)
    {
        if (RepaintCount == _repainted.Length) Array.Resize(ref _repainted, _repainted.Length * 2);
        _repainted[RepaintCount++] = c;
    }

    /// <summary>Draws the part of the plaza inside <paramref name="region"/> (dirty rectangles, already
    /// clipped to by the caller): the floor and every visual that reaches into one of them, once
    /// (the benchmark's model of the compositor repainting a frame's dirty region).</summary>
    public void RenderRegion(DrawingContext context, IReadOnlyList<Rect> region)
    {
        if (_floor is not null) context.DrawImage(_floor, CreaturePainter.PixelSource(_floor), new Rect(Bounds.Size));
        _drawOrder.Clear();
        _drawOrder.AddRange(_bodies);
        _drawOrder.Sort(ByZ);
        foreach (var b in _drawOrder)
            if (Reaches(b.Translation, b.Big ? _danceSize : _bodySize, region)) DrawChild(context, b, b.Translation);
        foreach (var n in _names)
            if (n.IsVisible && Reaches(n.Translation, n.Built, region)) DrawChild(context, n, n.Translation);
        foreach (var b in _bubbles)
            if (b.IsVisible && Reaches(b.Translation, b.Built, region)) DrawChild(context, b, b.Translation);
    }

    private static bool Reaches(TranslateTransform t, Size size, IReadOnlyList<Rect> region)
    {
        var r = new Rect(t.X, t.Y, size.Width, size.Height);
        for (var i = 0; i < region.Count; i++)
            if (region[i].Intersects(r)) return true;
        return false;
    }

    /// <summary>Where a creature's name tag and bubble were placed this frame (tests); null when hidden.</summary>
    public (Rect? Name, Rect? Bubble) LabelsOf(Creature c)
    {
        var i = IndexOf(c);
        if (i < 0) return (null, null);
        return (_names[i].IsVisible ? _names[i].Rect : null, _bubbles[i].IsVisible ? _bubbles[i].Rect : null);
    }

    private int IndexOf(Creature c)
    {
        for (var i = 0; i < _bodies.Count; i++)
            if (ReferenceEquals(_bodies[i].Creature, c)) return i;
        return -1;
    }

    private void RepaintAll()
    {
        InvalidateVisual();
        foreach (var b in _bodies) b.InvalidateVisual();
        foreach (var l in _names) l.InvalidateVisual();
        foreach (var l in _bubbles) l.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var v in VisualChildren)
            if (v is Avalonia.Layout.Layoutable l) l.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var v in VisualChildren)
            if (v is Avalonia.Layout.Layoutable l) l.Arrange(new Rect(l.DesiredSize));
        return finalSize;
    }

    private static double Snap(double v, double scaling) => scaling > 0 ? Math.Round(v * scaling) / scaling : v;

    // ── Drawing ──

    public override void Render(DrawingContext context) => DrawFloor(context, new Rect(Bounds.Size));

    /// <summary>The whole plaza into <paramref name="context"/> at once: the floor, then every
    /// visible creature, tag and bubble in order (tests, the preview, the benchmark).</summary>
    public void RenderAll(DrawingContext context)
    {
        Render(context);
        _drawOrder.Clear();
        _drawOrder.AddRange(_bodies);
        _drawOrder.Sort(ByZ);
        foreach (var b in _drawOrder) DrawChild(context, b, b.Translation);
        foreach (var n in _names) if (n.IsVisible) DrawChild(context, n, n.Translation);
        foreach (var b in _bubbles) if (b.IsVisible) DrawChild(context, b, b.Translation);
    }

    private static readonly Comparison<BodyVisual> ByZ = (a, b) => a.ZIndex.CompareTo(b.ZIndex);
    private readonly List<BodyVisual> _drawOrder = new(64);

    /// <summary>
    /// Moves a visual. Only coordinates that changed are set: each set is an Avalonia property
    /// change (it allocates its small event args, Avalonia's cost for repainting only what moved;
    /// a MatrixTransform's args are four times bigger, measured).
    /// </summary>
    private static void MoveTo(TranslateTransform t, double x, double y)
    {
        if (t.X != x) t.X = x;
        if (t.Y != y) t.Y = y;
    }

    private static void DrawChild(DrawingContext context, Control child, TranslateTransform t)
    {
        using (context.PushTransform(Matrix.CreateTranslation(t.X, t.Y)))
        {
            if (child.Opacity < 1)
                using (context.PushOpacity(child.Opacity))
                    child.Render(context);
            else child.Render(context);
        }
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
        else ctx.DrawImage(_floor, CreaturePainter.PixelSource(_floor), bounds);
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

    /// <summary>One creature (its shadow, tile, eyes, hands and feet), drawn around its feet and
    /// moved by translation. Repainted only when its pose changes.</summary>
    private sealed class BodyVisual : Control
    {
        private readonly PlazaCanvas _owner;

        public BodyVisual(PlazaCanvas owner, Creature creature)
        {
            _owner = owner;
            Creature = creature;
            Translation = new TranslateTransform();
            RenderTransform = Translation;
            RenderTransformOrigin = RelativePoint.TopLeft;
            IsHitTestVisible = false;
        }

        public Creature Creature { get; }
        public TranslateTransform Translation { get; }
        public int Pose;
        /// <summary>Drawn with the tile built for the current size (else redrawn next frame).</summary>
        public bool TileOk;
        /// <summary>The dancer's bigger visual (room for its notes).</summary>
        public bool Big;

        protected override Size MeasureOverride(Size availableSize) => Big ? _owner._danceSize : _owner._bodySize;

        public override void Render(DrawingContext context)
        {
            var c = Creature;
            var painter = _owner.Painter;
            var at = c.Position;
            // The painter draws at the creature's position: here, that is the feet inside this visual.
            var feet = Big ? _owner._danceFeet : _owner._feet;
            c.Position = new Vector2((float)feet.X, (float)feet.Y);
            painter.DrawShadow(context, c);
            painter.DrawBody(context, c);
            c.Position = at;
            TileOk = painter.HasCurrentTile(c);
        }
    }

    /// <summary>A name tag or a speech bubble: a prebuilt label bitmap, moved by translation.</summary>
    private sealed class LabelVisual : Control
    {
        private readonly PlazaCanvas _owner;
        private readonly bool _bubble;

        public LabelVisual(PlazaCanvas owner, Creature creature, bool bubble)
        {
            _owner = owner;
            _bubble = bubble;
            Creature = creature;
            Translation = new TranslateTransform();
            RenderTransform = Translation;
            RenderTransformOrigin = RelativePoint.TopLeft;
            IsHitTestVisible = false;
            IsVisible = false;
        }

        public Creature Creature { get; }
        public TranslateTransform Translation { get; }
        /// <summary>The bubble's line (null for a name tag).</summary>
        public string? Line;
        public Size Built;
        public Rect Rect;

        protected override Size MeasureOverride(Size availableSize) => Built;

        public override void Render(DrawingContext context)
        {
            if (_bubble)
            {
                if (Line is { } line) _owner.Painter.DrawBubbleAt(context, line, default);
            }
            else _owner.Painter.DrawNameAt(context, Creature, default);
        }
    }
}
