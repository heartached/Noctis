using System.Diagnostics;
using System.Globalization;
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
/// The Apartments: Jafezy's building grid ("the void") with the rooms you built, their pets
/// living in them, and the drag gestures: drop an album or playlist from the shelf to build a
/// room, drag a room to move it, drag its corner to resize it, "..." or right-click for its menu.
/// <para>
/// Built like the plaza for a steady frame: the empty grid is one cached bitmap, each room
/// (backdrop, frame, walls, doors, name) is a cached bitmap rebuilt only when it changes (a few
/// per frame at most), pets are the plaza's flat creature tiles, and nothing in a frame
/// allocates. Frames come from <see cref="TopLevel.RequestAnimationFrame"/> and only while
/// there is something alive to draw (pets or a drag); hidden, minimized or empty, it stops.
/// </para>
/// </summary>
public sealed class ApartmentsCanvas : Control
{
    /// <summary>Space kept around the grid (px).</summary>
    public const double Pad = 18;
    /// <summary>Space between neighbouring rooms (px, half on each).</summary>
    public const double Gap = 6;
    /// <summary>Room bitmaps (re)built per frame at most.</summary>
    public const int BuildsPerFrame = 4;
    private const double DragThreshold = 5;
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Comparison<Pet> ByDepth = (a, b) => a.Y.CompareTo(b.Y);

    private readonly CoverCache _petCovers;
    private readonly CoverCache _softCovers;
    private readonly Dictionary<string, RoomSprite> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomGeometry> _geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _coverColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(IDisposable Item, long Frame)> _retired = new();
    private readonly List<Pet> _order = new(64);
    private readonly List<string> _prune = new();
    private readonly List<Bitmap> _scratchCovers = new(4);
    private readonly Action<TimeSpan> _onFrame;
    private readonly DispatcherTimer _idle;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Cursor _arrow = new(StandardCursorType.Arrow);
    private readonly Cursor _hand = new(StandardCursorType.Hand);
    private readonly Cursor _move = new(StandardCursorType.SizeAll);
    private readonly Cursor _resize = new(StandardCursorType.BottomRightCorner);
    private TownTheme _theme = TownTheme.Dark;
    private RenderTargetBitmap? _void;
    private RenderTargetBitmap? _layer;
    private int _layerVersion = -1;
    private int _spriteBuilds;
    private (Size Size, double Scaling, TownTheme Theme, double Cell) _voidFor;
    private int _mapVersion = -1;
    private long _frame;
    private int _budget;
    private bool _attached;
    private bool _framePending;
    private TimeSpan? _lastFrame;
    private TimeSpan _last;
    private FormattedText? _hint;
    private TownTheme? _hintTheme;

    // Gestures.
    private enum DragKind { None, Pending, Move, Resize, Drop }
    private DragKind _drag;
    private Room? _dragRoom;
    private Point _pressAt;
    private Vector _grab;
    private Cells _ghost;
    private bool _ghostFits;
    private (RoomKind Kind, string Source)? _dropping;

    public ApartmentsCanvas(CoverCache petCovers, CoverCache softCovers, ApartmentGrid grid, ApartmentLife life)
    {
        _petCovers = petCovers;
        _softCovers = softCovers;
        Grid = grid;
        Life = life;
        // Plain shapes for hands, feet and eyes: this canvas repaints every frame, and each blit
        // would be one more image Avalonia records.
        AlbumPainter = new CreaturePainter(petCovers) { SpriteLimbs = false };
        SongPainter = new CreaturePainter(petCovers) { SpriteLimbs = false };
        petCovers.CoverReady += (_, _) => Refresh();
        softCovers.CoverReady += (_, _) => Refresh();
        ClipToBounds = true;
        Focusable = false;
        Cursor = _arrow;
        _onFrame = OnFrame;
        _idle = new DispatcherTimer(IdleInterval, DispatcherPriority.Background, (_, _) => CheckIdle());
    }

    public ApartmentGrid Grid { get; private set; }
    public ApartmentLife Life { get; }
    public CreaturePainter AlbumPainter { get; }
    public CreaturePainter SongPainter { get; }
    /// <summary>What each room shows (the view knows the albums and playlists).</summary>
    public Func<Room, RoomLook>? Describe { get; set; }
    /// <summary>Beat source (0..1), polled per frame.</summary>
    public Func<double?>? Beat { get; set; }
    public bool ShowNames { get; set; }
    public Pet? Hovered { get; private set; }
    public bool IsPaused { get; private set; } = true;
    /// <summary>Frames stepped (tests).</summary>
    public long Frames { get; private set; }
    /// <summary>Bitmaps drawn in the last frame (tests budget the frame by it).</summary>
    public int ImagesDrawn { get; private set; }
    /// <summary>Room bitmaps currently kept (tests).</summary>
    public int RoomSpriteCount => _sprites.Count;

    /// <summary>Side of one grid cell in px (set by the layout).</summary>
    public double Cell { get; private set; } = 96;
    /// <summary>Top-left of the grid in px.</summary>
    public Point Origin { get; private set; }

    public TownTheme Palette
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(value, _theme)) return;
            _theme = value;
            AlbumPainter.Palette = value;
            SongPainter.Palette = value;
            InvalidateVisual();
        }
    }

    /// <summary>A pet was clicked (it waves too).</summary>
    public event EventHandler<Pet>? PetClicked;
    /// <summary>The "..." button, the name band or a right-click asked for a room's menu.</summary>
    public event EventHandler<Room>? RoomMenuRequested;

    /// <summary>Replaces the building (a fresh load).</summary>
    public void SetGrid(ApartmentGrid grid)
    {
        Grid = grid;
        _mapVersion = -1;
        Refresh();
    }

    /// <summary>The building or a room's looks changed: rebuild what depends on it and draw.</summary>
    public void Refresh()
    {
        if (!_attached) return;
        InvalidateVisual();
        RequestFrame();
    }

    /// <summary>A room's name or covers changed (playlist edited): rebuild its bitmap.</summary>
    public void Restyle()
    {
        foreach (var s in _sprites.Values) s.Stale = true;
        Refresh();
    }

    // ── Geometry ──

    /// <summary>Cell size and origin for <paramref name="size"/>: the grid as big as fits, centred,
    /// on whole device pixels.</summary>
    public static (double Cell, Point Origin) LayoutFor(Size size, int columns, int rows, double scaling)
    {
        scaling = scaling > 0 ? scaling : 1;
        var cell = Math.Max(40, Math.Min((size.Width - 2 * Pad) / columns, (size.Height - 2 * Pad) / rows));
        cell = Math.Floor(cell * scaling) / scaling;
        var x = Math.Round((size.Width - cell * columns) / 2 * scaling) / scaling;
        var y = Math.Round((size.Height - cell * rows) / 2 * scaling) / scaling;
        return (cell, new Point(Math.Max(0, x), Math.Max(0, y)));
    }

    /// <summary>Pixel density to draw at instead of the window's (headless previews at 125%).</summary>
    public double? ScalingOverride { get; set; }

    private double Scaling => ScalingOverride ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void UpdateGrid()
    {
        var scaling = Scaling;
        var (cell, origin) = LayoutFor(Bounds.Size, Grid.Columns, Grid.Rows, scaling);
        Cell = cell;
        Origin = origin;
        AlbumPainter.Scaling = SongPainter.Scaling = scaling;
        AlbumPainter.Size = SnapSize(cell * ApartmentLife.AlbumPetSize, scaling);
        SongPainter.Size = SnapSize(cell * ApartmentLife.SongPetSize, scaling);
        AlbumPainter.AreaWidth = SongPainter.AreaWidth = Bounds.Width;
    }

    private static double SnapSize(double size, double scaling) => Math.Max(12, Math.Round(size * scaling / 2) * 2 / scaling);

    /// <summary>A room's drawn rectangle in px.</summary>
    public Rect RoomRect(Cells c)
        => new(Origin.X + c.X * Cell + Gap / 2, Origin.Y + c.Y * Cell + Gap / 2, c.W * Cell - Gap, c.H * Cell - Gap);

    public Point ToPx(double x, double y) => new(Origin.X + x * Cell, Origin.Y + y * Cell);

    public (double X, double Y) ToCells(Point p) => ((p.X - Origin.X) / Cell, (p.Y - Origin.Y) / Cell);

    /// <summary>The room under <paramref name="p"/> (px), or null.</summary>
    public Room? RoomAt(Point p)
    {
        foreach (var r in Grid.Rooms)
            if (RoomRect(r.Cells).Contains(p)) return r;
        return null;
    }

    // ── Frames ──

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
        _tracker.Stop();
        IsPaused = true;
        _void?.Dispose();
        _void = null;
        _layer?.Dispose();
        _layer = null;
        foreach (var s in _sprites.Values) s.Bitmap.Dispose();
        _sprites.Clear();
        while (_retired.Count > 0) _retired.Dequeue().Item.Dispose();
        AlbumPainter.Dispose();
        SongPainter.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _attached) RequestFrame();
    }

    /// <summary>Hidden, zero-sized or in a minimized window: nothing to animate.</summary>
    public bool ShouldPause()
        => !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1
           || TopLevel.GetTopLevel(this) is not { } top
           || top is Window { WindowState: WindowState.Minimized };

    /// <summary>Something moves: pets live here, or a drag is on.</summary>
    public bool IsAlive => Life.Pets.Count > 0 || _drag is DragKind.Move or DragKind.Resize or DragKind.Drop;

    private void RequestFrame()
    {
        if (_framePending || !_attached || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(_onFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        if (!_attached) return;
        if (ShouldPause())
        {
            IsPaused = true;
            _lastFrame = null;
            _gate.Reset();
            _idle.Start();
            return;
        }
        IsPaused = false;
        // The Motion setting's rate (15 fps while the window is not in front); a drag is always smooth.
        var focused = TopLevel.GetTopLevel(this) is not Window w || w.IsActive;
        var interval = IsDragging ? TimeSpan.FromSeconds(1.0 / 60) : Town.Motion.Interval(Motion, focused);
        if (!_gate.Pass(_clock.Elapsed, interval, out var dt))
        {
            RequestFrame();
            return;
        }
        _lastFrame = time;
        _last = _clock.Elapsed;
        Step(dt);
        InvalidateVisual();
        // Nothing alive (no pets, no drag): stop asking for frames until something changes.
        if (IsAlive) RequestFrame();
        else { IsPaused = true; _lastFrame = null; _gate.Reset(); }
    }

    /// <summary>The "Motion" setting: frames per second while pets live here.</summary>
    public MotionMode Motion { get; set; } = MotionMode.Balanced;
    private readonly FrameGate _gate = new();

    private void CheckIdle()
    {
        if (!_attached) { _idle.Stop(); return; }
        if (ShouldPause()) return;
        _idle.Stop();
        RequestFrame();
    }

    /// <summary>One frame driven by hand (tests).</summary>
    public void Tick()
    {
        var now = _clock.Elapsed;
        var dt = (float)(now - _last).TotalSeconds;
        _last = now;
        if (ShouldPause()) { IsPaused = true; return; }
        IsPaused = false;
        Step(dt);
        InvalidateVisual();
    }

    /// <summary>Steps life (tests and the preview call it directly).</summary>
    public void Step(float dt)
    {
        UpdateGrid();
        Life.Now = DateTimeOffset.Now;
        var pulse = Beat?.Invoke() ?? 0;
        Life.Step(dt, (float)Math.Clamp(pulse, 0, 1));
        Frames++;
    }

    // ── Drawing ──

    public override void Render(DrawingContext ctx)
    {
        UpdateGrid();
        _frame++;
        _budget = BuildsPerFrame;
        ImagesDrawn = 0;
        while (_retired.Count > 0 && _frame - _retired.Peek().Frame > 30) _retired.Dequeue().Item.Dispose();
        AlbumPainter.BeginFrame();
        SongPainter.BeginFrame();
        var bounds = new Rect(Bounds.Size);
        var scaling = AlbumPainter.Scaling;

        // The grid and every room: one bitmap, redone only when a room (or the size, density or
        // theme) changes. A frame blits it once instead of thirty room bitmaps.
        if (_mapVersion != Grid.Version) RebuildGeometry();
        DrawBuilding(ctx, bounds, scaling);

        // Pets: feet in px, front to back.
        var pets = Life.Pets;
        _order.Clear();
        for (var i = 0; i < pets.Count; i++)
        {
            var p = pets[i];
            var at = ToPx(p.X, p.Y);
            p.Creature.Position = new System.Numerics.Vector2((float)at.X, (float)at.Y);
            _order.Add(p);
        }
        _order.Sort(ByDepth);
        foreach (var p in _order) PainterFor(p).DrawShadow(ctx, p.Creature);
        foreach (var p in _order)
        {
            var painter = PainterFor(p);
            if (p.Creature.Age < 0.5f)
                using (ctx.PushOpacity(Math.Clamp(p.Creature.Age * 2, 0, 1)))
                    painter.DrawBody(ctx, p.Creature);
            else painter.DrawBody(ctx, p.Creature);
        }
        foreach (var p in _order)
            if (ShowNames || ReferenceEquals(p, Hovered) || p.Creature.IsPlaying) PainterFor(p).DrawName(ctx, p.Creature, bounds);
        foreach (var p in _order) PainterFor(p).DrawBubble(ctx, p.Creature, bounds);
        ImagesDrawn += AlbumPainter.ImagesDrawn + SongPainter.ImagesDrawn;

        DrawGhost(ctx);
        if (Grid.Rooms.Count == 0) DrawHint(ctx, bounds);
        if (_frame % 240 == 0) PruneSprites();
    }

    private CreaturePainter PainterFor(Pet p) => p.IsSong ? SongPainter : AlbumPainter;

    /// <summary>Times the building layer was redrawn (tests: it must not happen every frame).</summary>
    public int LayerBuilds { get; private set; }

    private void DrawBuilding(DrawingContext ctx, Rect bounds, double scaling)
    {
        var builtBefore = _spriteBuilds;
        var rooms = Grid.Rooms;
        for (var i = 0; i < rooms.Count; i++) SpriteFor(rooms[i], RoomRect(rooms[i].Cells).Size, scaling);
        var voidChanged = _void is null || _voidFor != (bounds.Size, scaling, _theme, Cell);
        if (voidChanged)
        {
            if (_void is not null) _retired.Enqueue((_void, _frame));
            _void = BuildVoid(bounds.Size, scaling);
            _voidFor = (bounds.Size, scaling, _theme, Cell);
        }
        if (_layer is null || voidChanged || _spriteBuilds != builtBefore || _layerVersion != Grid.Version)
        {
            if (_layer is not null) _retired.Enqueue((_layer, _frame));
            _layer = BuildLayer(bounds.Size, scaling);
            _layerVersion = Grid.Version;
            LayerBuilds++;
        }
        if (_layer is null) ctx.FillRectangle(_theme.FloorBrush, bounds);
        else
        {
            ctx.DrawImage(_layer, CreaturePainter.PixelSource(_layer), new Rect(0, 0, _layer.PixelSize.Width / scaling, _layer.PixelSize.Height / scaling));
            ImagesDrawn++;
        }
    }

    /// <summary>The empty grid plus every room's bitmap (or a plain tile while it is being built).</summary>
    private RenderTargetBitmap? BuildLayer(Size size, double scaling)
    {
        if (_void is null) return null;
        var bitmap = new RenderTargetBitmap(_void.PixelSize, new Vector(96 * scaling, 96 * scaling));
        using var ctx = bitmap.CreateDrawingContext();
        ctx.DrawImage(_void, CreaturePainter.PixelSource(_void), new Rect(0, 0, _void.PixelSize.Width / scaling, _void.PixelSize.Height / scaling));
        foreach (var room in Grid.Rooms)
        {
            var rect = RoomRect(room.Cells);
            var dest = new Rect(Snap(rect.X, scaling), Snap(rect.Y, scaling), rect.Width, rect.Height);
            if (_sprites.TryGetValue(room.Id, out var sprite) && sprite.Size == rect.Size)
                ctx.DrawImage(sprite.Bitmap, sprite.Source, new Rect(dest.X, dest.Y, sprite.Bitmap.PixelSize.Width / scaling, sprite.Bitmap.PixelSize.Height / scaling));
            else ctx.DrawRectangle(_theme.Placeholder, null, new RoundedRect(dest, RoomPainter.Corner(Cell)));
        }
        return bitmap;
    }

    private RenderTargetBitmap? BuildVoid(Size size, double scaling)
    {
        var px = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        if (px.Width < 1 || px.Height < 1) return null;
        var bitmap = new RenderTargetBitmap(px, new Vector(96 * scaling, 96 * scaling));
        using var ctx = bitmap.CreateDrawingContext();
        ctx.FillRectangle(_theme.FloorBrush, new Rect(size));
        var slot = new ImmutableSolidColorBrush(_theme.IsDark ? Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        var slotPen = new ImmutablePen(new ImmutableSolidColorBrush(_theme.IsDark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00)), 1, new ImmutableDashStyle(new double[] { 3, 4 }, 0));
        var radius = RoomPainter.Corner(Cell);
        for (var y = 0; y < Grid.Rows; y++)
            for (var x = 0; x < Grid.Columns; x++)
            {
                var r = RoomRect(new Cells(x, y, 1, 1));
                ctx.DrawRectangle(slot, slotPen, new RoundedRect(r.Deflate(0.5), radius));
            }
        return bitmap;
    }

    private RoomSprite? SpriteFor(Room room, Size size, double scaling)
    {
        _sprites.TryGetValue(room.Id, out var sprite);
        if (!_geometry.TryGetValue(room.Id, out var geo)) return sprite;
        var covers = CoversReady(geo.Look);
        if (sprite is not null)
        {
            sprite.UsedFrame = _frame;
            var current = !sprite.Stale && sprite.Size == size && sprite.Scaling == scaling && ReferenceEquals(sprite.Theme, _theme)
                && sprite.Geometry.Signature == geo.Signature && sprite.Covers == covers && sprite.Cell == Cell;
            if (current || _budget <= 0) return sprite;
        }
        else if (_budget <= 0) return null;
        _budget--;
        _spriteBuilds++;
        var built = BuildSprite(room, geo, size, scaling, covers);
        built.UsedFrame = _frame;
        if (sprite is not null) _retired.Enqueue((sprite.Bitmap, _frame));
        _sprites[room.Id] = built;
        return built;
    }

    /// <summary>How many of the room's backdrop covers are decoded (starts the rest).</summary>
    private int CoversReady(RoomLook look)
    {
        var n = 0;
        for (var i = 0; i < look.Covers.Count; i++)
            if (_softCovers.Get(look.Covers[i]) is not null) n++;
        return n;
    }

    private RoomSprite BuildSprite(Room room, RoomGeometry geo, Size size, double scaling, int ready)
    {
        _scratchCovers.Clear();
        foreach (var path in geo.Look.Covers)
            if (_softCovers.Get(path)?.Image is { } img) _scratchCovers.Add(img);
        var frame = CoverColor.Frame(TintFor(geo.Look), _theme.IsDark);
        var rect = RoomRect(room.Cells);
        var areas = new List<Rect>(geo.Areas.Count);
        foreach (var a in geo.Areas)
        {
            var tl = ToPx(a.X, a.Y);
            var br = ToPx(a.Right, a.Bottom);
            // Clip the area to the room's drawn rectangle (rooms are inset by half the gap).
            var r = new Rect(new Point(Math.Max(tl.X, rect.X), Math.Max(tl.Y, rect.Y)), new Point(Math.Min(br.X, rect.Right), Math.Min(br.Y, rect.Bottom)));
            areas.Add(r.Translate(new Vector(-rect.X, -rect.Y)));
        }
        var openings = new List<RoomOpening>(geo.Openings.Count);
        var doorHeight = Math.Min(Cell * 0.52, Cell * 0.8);
        foreach (var o in geo.Openings)
        {
            var at = ToPx(o.X, o.Y);
            var x = at.X - rect.X;
            var y = at.Y - rect.Y;
            if (o.Kind == PortalKind.Door)
            {
                var floorBand = Math.Min(o.AreaHeight * Cell * 0.16, Cell * 0.13);
                y = Math.Min(y, rect.Height) - floorBand;
                openings.Add(new RoomOpening(PortalKind.Door, Math.Clamp(x, 0, rect.Width), y, Math.Min(doorHeight, o.AreaHeight * Cell * 0.62)));
            }
            else openings.Add(new RoomOpening(PortalKind.Ladder, x, y, o.Length * Cell - Math.Min(o.AreaHeight * Cell * 0.16, Cell * 0.13)));
        }
        var bitmap = RoomPainter.Build(size, scaling, Cell, _theme, frame, geo.Look, _scratchCovers, areas, openings);
        _scratchCovers.Clear();
        return new RoomSprite(bitmap, size, scaling, _theme, geo, ready, Cell);
    }

    /// <summary>The room's colour: its (first) cover's, else the playlist's own, else one from its name.</summary>
    private Color TintFor(RoomLook look)
    {
        if (look.Covers.Count > 0)
        {
            var path = look.Covers[0];
            if (_coverColors.TryGetValue(path, out var known)) return known;
            if (_softCovers.Get(path) is { } cover)
            {
                var c = CoverColor.Dominant(cover.Image) ?? look.Fallback ?? CoverCache.Pastel(look.Title, 0.6f);
                if (cover.Image is not null) _coverColors[path] = c;
                return c;
            }
        }
        return look.Fallback ?? CoverCache.Pastel(look.Title, 0.6f);
    }

    /// <summary>The colour of <paramref name="room"/>'s frame right now (tests, the menu).</summary>
    public Color FrameColor(Room room)
        => _geometry.TryGetValue(room.Id, out var geo) ? CoverColor.Frame(TintFor(geo.Look), _theme.IsDark) : CoverColor.Frame(CoverCache.Pastel(room.Source), _theme.IsDark);

    /// <summary>Rooms, areas and doors changed: recompute per room what its bitmap shows.</summary>
    private void RebuildGeometry()
    {
        _mapVersion = Grid.Version;
        _geometry.Clear();
        var map = Life.Map;
        foreach (var room in Grid.Rooms)
        {
            var look = Describe?.Invoke(room) ?? new RoomLook(room.Source, "", Array.Empty<string>(), null);
            var areas = map.AreasOf(room);
            var rects = areas.Select(a => a.Rect).ToList();
            var openings = new List<GeoOpening>();
            foreach (var portal in map.Portals)
            {
                if (portal.Kind == PortalKind.TrapDoor) continue; // not in v1
                if (portal.Kind == PortalKind.Ladder)
                {
                    // Once per ladder: from the area above.
                    if (ReferenceEquals(portal.From.Room, room) && portal.From.Rect.Bottom <= portal.To.Rect.Y + 1e-6)
                        openings.Add(new GeoOpening(PortalKind.Ladder, portal.FromPoint.X, portal.Line, portal.To.Rect.H, portal.To.Rect.H));
                    continue;
                }
                // Doors: every door on this room's walls (inside it once, between rooms on each side).
                if (!ReferenceEquals(portal.From.Room, room)) continue;
                if (!portal.BetweenRooms && portal.From.Rect.X > portal.To.Rect.X) continue;
                openings.Add(new GeoOpening(PortalKind.Door, portal.Line, portal.From.Floor, Math.Min(portal.From.Rect.H, portal.To.Rect.H), 0));
            }
            _geometry[room.Id] = new RoomGeometry(look, rects, openings);
        }
    }

    private void PruneSprites()
    {
        _prune.Clear();
        foreach (var (id, sprite) in _sprites)
            if (Grid.Find(id) is null || _frame - sprite.UsedFrame > 240) _prune.Add(id);
        foreach (var id in _prune)
        {
            _retired.Enqueue((_sprites[id].Bitmap, _frame));
            _sprites.Remove(id);
        }
    }

    private void DrawGhost(DrawingContext ctx)
    {
        if (_drag is not (DragKind.Move or DragKind.Resize or DragKind.Drop) || (_drag == DragKind.Drop && _ghost.W == 0)) return;
        var r = RoomRect(_ghost);
        var fill = _ghostFits ? _theme.GhostFill : _theme.GhostBlocked;
        var pen = _ghostFits ? _theme.GhostPen : _theme.GhostBlockedPen;
        ctx.DrawRectangle(fill, pen, new RoundedRect(r, RoomPainter.Corner(Cell)));
    }

    private void DrawHint(DrawingContext ctx, Rect bounds)
    {
        if (_hint is null || !ReferenceEquals(_hintTheme, _theme))
        {
            _hintTheme = _theme;
            _hint = new FormattedText("Drag an album or a playlist from the shelf into the void to build a room.",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 15, _theme.Muted)
            {
                MaxTextWidth = Math.Max(200, Math.Min(520, bounds.Width - 40)),
                TextAlignment = TextAlignment.Center,
            };
        }
        ctx.DrawText(_hint, new Point((bounds.Width - _hint.MaxTextWidth) / 2, (bounds.Height - _hint.Height) / 2));
    }

    private static double Snap(double v, double scaling) => scaling > 0 ? Math.Round(v * scaling) / scaling : v;

    // ── Gestures ──

    /// <summary>Starts dragging a shelf item over the building.</summary>
    public void BeginDrop(RoomKind kind, string source)
    {
        _dropping = (kind, source);
        _drag = DragKind.Drop;
        _ghost = default;
        RequestFrame();
    }

    /// <summary>The shelf item is over <paramref name="p"/> (px): shows where its room would go.</summary>
    public void UpdateDrop(Point p)
    {
        if (_drag != DragKind.Drop) return;
        var (cx, cy) = ToCells(p);
        if (!new Rect(Bounds.Size).Contains(p) || cx < 0 || cy < 0 || cx >= Grid.Columns || cy >= Grid.Rows)
        {
            _ghost = default;
            InvalidateVisual();
            return;
        }
        // An album or playlist that already has a room: that room moves here (one room each).
        var existing = _dropping is { } d ? Grid.FindSource(d.Kind, d.Source) : null;
        var (x, y) = ((int)Math.Floor(cx), (int)Math.Floor(cy));
        _ghost = existing is null ? new Cells(x, y, 1, 1) : existing.Cells with { X = x, Y = y };
        _ghostFits = Grid.CanPlace(_ghost, existing);
        InvalidateVisual();
    }

    /// <summary>Dropped at <paramref name="p"/> (px): builds the room (or moves the one it has). The room, or null.</summary>
    public Room? EndDrop(Point p)
    {
        if (_drag != DragKind.Drop || _dropping is not { } d) { CancelDrop(); return null; }
        UpdateDrop(p);
        Room? room = null;
        if (_ghost.W > 0 && _ghostFits) room = Grid.TryAdd(d.Kind, d.Source, _ghost.X, _ghost.Y);
        CancelDrop();
        return room;
    }

    public void CancelDrop()
    {
        _dropping = null;
        if (_drag == DragKind.Drop) _drag = DragKind.None;
        _ghost = default;
        InvalidateVisual();
    }

    /// <summary>True while a room is being moved or resized, or a shelf item dragged over.</summary>
    public bool IsDragging => _drag is DragKind.Move or DragKind.Resize or DragKind.Drop;
    /// <summary>Where the dragged room would land, and whether it fits (tests).</summary>
    public (Cells Cells, bool Fits) Ghost => (_ghost, _ghostFits);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        if (point.Properties.IsRightButtonPressed)
        {
            if (RoomAt(p) is { } r) { RoomMenuRequested?.Invoke(this, r); e.Handled = true; }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (!Press(p)) return;
        e.Handled = true;
        // A room move or resize is followed on the window, so losing the capture (Windows
        // WM_CAPTURECHANGED) does not drop it halfway (see PointerTracker).
        if (_dragRoom is not null) _tracker.Start(this, OnTrackedMoved, OnTrackedReleased);
    }

    private readonly PointerTracker _tracker = new();

    private void OnTrackedMoved(PointerEventArgs e)
    {
        // The button came up where the window could not see it: put the room back.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _tracker.Stop(); CancelGesture(); return; }
        Move(e.GetPosition(this));
    }

    private void OnTrackedReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        _tracker.Stop();
        if (Release(e.GetPosition(this))) e.Handled = true;
    }

    private void CancelGesture()
    {
        if (_drag is not (DragKind.Move or DragKind.Resize or DragKind.Pending)) return;
        _drag = DragKind.None;
        _dragRoom = null;
        _ghost = default;
        InvalidateVisual();
    }

    /// <summary>A left press at <paramref name="p"/> (px). True when it hit something.</summary>
    public bool Press(Point p)
    {
        var room = RoomAt(p);
        Rect rect = default;
        var local = default(Point);
        if (room is not null)
        {
            // The "..." button and the corner grip win over a pet standing in front of them.
            rect = RoomRect(room.Cells);
            local = new Point(p.X - rect.X, p.Y - rect.Y);
            var (menu, radius) = RoomPainter.MenuButton(rect.Size, Cell);
            if (Distance(local, menu) <= radius + 3)
            {
                RoomMenuRequested?.Invoke(this, room);
                return true;
            }
            if (RoomPainter.Grip(rect.Size, Cell).Inflate(3).Contains(local))
            {
                StartGesture(room, p, rect, DragKind.Resize);
                RequestFrame();
                return true;
            }
        }
        var (cx, cy) = ToCells(p);
        if (Life.HitTest(cx, cy, Hovered) is { } pet)
        {
            Life.Wave(pet);
            PetClicked?.Invoke(this, pet);
            return true;
        }
        if (room is null) return false;
        StartGesture(room, p, rect, DragKind.Pending);
        return true;
    }

    private void StartGesture(Room room, Point p, Rect rect, DragKind kind)
    {
        _dragRoom = room;
        _pressAt = p;
        _grab = p - rect.TopLeft;
        _ghost = room.Cells;
        _ghostFits = true;
        _drag = kind;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_tracker.IsActive) Move(e.GetPosition(this)); // else the tracker moves the room
    }

    /// <summary>The pointer moved to <paramref name="p"/> (px).</summary>
    public void Move(Point p)
    {
        if (_drag == DragKind.Pending && _dragRoom is not null && Distance(p, _pressAt) > DragThreshold)
        {
            _drag = DragKind.Move;
            RequestFrame();
        }
        if (_drag == DragKind.Move && _dragRoom is { } moving)
        {
            var (cx, cy) = ToCells(p - _grab + new Vector(Gap / 2, Gap / 2));
            var x = Math.Clamp((int)Math.Round(cx), 0, Math.Max(0, Grid.Columns - moving.Cells.W));
            var y = Math.Clamp((int)Math.Round(cy), 0, Math.Max(0, Grid.Rows - moving.Cells.H));
            _ghost = moving.Cells with { X = x, Y = y };
            _ghostFits = Grid.CanPlace(_ghost, moving);
            SetCursor(_move);
            InvalidateVisual();
            return;
        }
        if (_drag == DragKind.Resize && _dragRoom is { } sizing)
        {
            var (cx, cy) = ToCells(p);
            (_ghost, _ghostFits) = Grid.ResizeTarget(sizing, Math.Max(0.5, cx - sizing.Cells.X), Math.Max(0.5, cy - sizing.Cells.Y));
            SetCursor(_resize);
            InvalidateVisual();
            return;
        }
        if (_drag == DragKind.Pending) return;
        UpdateHover(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Handled) return; // the tracker finished the gesture
        if (Release(e.GetPosition(this))) e.Handled = true;
    }

    /// <summary>The left button came up at <paramref name="p"/> (px): finishes a move or resize,
    /// or (a click on the name band) asks for the room's menu.</summary>
    public bool Release(Point p)
    {
        var room = _dragRoom;
        var kind = _drag;
        _dragRoom = null;
        if (kind is DragKind.Drop) return false;
        _drag = DragKind.None;
        if (room is null) return false;
        switch (kind)
        {
            case DragKind.Move when _ghostFits:
                Grid.TryMove(room, _ghost.X, _ghost.Y);
                break;
            case DragKind.Resize when _ghostFits:
                Grid.TryResize(room, _ghost.W, _ghost.H);
                break;
            case DragKind.Pending:
                var rect = RoomRect(room.Cells);
                if (p.Y - rect.Y <= RoomPainter.HeaderHeight(Cell) + RoomPainter.FrameWidth(Cell)) RoomMenuRequested?.Invoke(this, room);
                break;
        }
        _ghost = default;
        UpdateHover(p);
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag == DragKind.None) SetHovered(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_tracker.IsActive) return; // still followed on the window: the gesture carries on
        CancelGesture();
    }

    private void UpdateHover(Point p)
    {
        var (cx, cy) = ToCells(p);
        var pet = Life.HitTest(cx, cy, Hovered);
        SetHovered(pet);
        if (pet is not null) { SetCursor(_hand); return; }
        if (RoomAt(p) is { } room)
        {
            var rect = RoomRect(room.Cells);
            var local = new Point(p.X - rect.X, p.Y - rect.Y);
            var (menu, radius) = RoomPainter.MenuButton(rect.Size, Cell);
            if (Distance(local, menu) <= radius + 3) { SetCursor(_hand); return; }
            if (RoomPainter.Grip(rect.Size, Cell).Inflate(3).Contains(local)) { SetCursor(_resize); return; }
        }
        SetCursor(_arrow);
    }

    private void SetHovered(Pet? pet)
    {
        if (ReferenceEquals(pet, Hovered)) return;
        Hovered = pet;
        Life.Held = pet;
        InvalidateVisual();
    }

    private void SetCursor(Cursor c)
    {
        if (!ReferenceEquals(Cursor, c)) Cursor = c;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ── Cached per-room data ──

    private readonly record struct GeoOpening(PortalKind Kind, double X, double Y, double AreaHeight, double Length);

    private sealed class RoomGeometry
    {
        public RoomGeometry(RoomLook look, IReadOnlyList<CellRect> areas, IReadOnlyList<GeoOpening> openings)
        {
            Look = look;
            Areas = areas;
            Openings = openings;
            // What the bitmap depends on, as text: a room another room moved past keeps its bitmap.
            var sb = new System.Text.StringBuilder();
            sb.Append(look.Title).Append('\u0001').Append(look.Subtitle).Append('\u0001').Append(look.Fallback?.ToString() ?? "");
            foreach (var c in look.Covers) sb.Append('\u0001').Append(c);
            foreach (var a in areas) sb.Append('|').Append(a.ToString());
            foreach (var o in openings) sb.Append('|').Append(o.ToString());
            Signature = sb.ToString();
        }

        public string Signature { get; }

        public RoomLook Look { get; }
        public IReadOnlyList<CellRect> Areas { get; }
        public IReadOnlyList<GeoOpening> Openings { get; }
    }

    private sealed class RoomSprite
    {
        public RoomSprite(RenderTargetBitmap bitmap, Size size, double scaling, TownTheme theme, RoomGeometry geometry, int covers, double cell)
        {
            Bitmap = bitmap;
            Size = size;
            Scaling = scaling;
            Theme = theme;
            Geometry = geometry;
            Covers = covers;
            Cell = cell;
            Source = CreaturePainter.PixelSource(bitmap);
        }

        public RenderTargetBitmap Bitmap { get; }
        public Size Size { get; }
        public double Scaling { get; }
        public TownTheme Theme { get; }
        public RoomGeometry Geometry { get; }
        public int Covers { get; }
        public double Cell { get; }
        public Rect Source { get; }
        public bool Stale { get; set; }
        public long UsedFrame { get; set; }
    }
}
