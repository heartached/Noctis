using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// The Apartments building, drawn floor by floor: each artist's home is as wide as
/// <see cref="Apartments.UnitsFor"/> says, with its residents (album cubes) inside. Static: it
/// redraws when the building, the scroll position or a cover changes, and only the floors in
/// view are drawn (and only their covers decoded).
/// </summary>
public sealed class ApartmentsCanvas : Control
{
    public const double Unit = 66;
    public const double FloorHeight = 124;
    private const double Pad = 24;
    private const double Roof = 70;
    private const double Resident = 20;

    private static readonly IBrush Sky = new ImmutableSolidColorBrush(Color.FromRgb(0x1B, 0x18, 0x22));
    private static readonly IBrush Facade = new ImmutableSolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x45));
    private static readonly IBrush RoofBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x4B, 0x41, 0x5A));
    private static readonly IBrush Floorboards = new ImmutableSolidColorBrush(Color.FromRgb(0xB8, 0x8C, 0x6A));
    private static readonly IBrush Vacant = new ImmutableSolidColorBrush(Color.FromRgb(0x2C, 0x27, 0x34));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x33));
    private static readonly IBrush DimLabel = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x2A, 0x24, 0x33));
    private static readonly IBrush SignText = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush VacantText = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly IPen RoomPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x10, 0x0C, 0x16)), 1.5);
    private static readonly IPen CubePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x8C, 0x1E, 0x18, 0x28)), 1);
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly CoverCache _covers;
    private Building _building = new(Array.Empty<Apartment>(), Apartments.DefaultUnitsPerFloor, 0);
    private readonly List<(Rect Rect, TownAlbum Album)> _hits = new();
    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);

    public ApartmentsCanvas(CoverCache covers)
    {
        _covers = covers;
        covers.CoverReady += (_, _) => { if (IsEffectivelyVisible) InvalidateVisual(); };
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>The part of this control the user can see (the scroll viewport), in its own coordinates.</summary>
    public Func<Rect>? Viewport { get; set; }

    public Building Building
    {
        get => _building;
        set
        {
            _building = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public event EventHandler<TownAlbum>? ResidentClicked;

    public Size BuildingSize => new(Pad * 2 + _building.UnitsPerFloor * Unit, Pad + Roof + Math.Max(1, _building.Floors) * FloorHeight + 36);

    protected override Size MeasureOverride(Size availableSize) => BuildingSize;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        Cursor = new Cursor(_hits.Any(h => h.Rect.Contains(p)) ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        foreach (var (rect, album) in _hits)
            if (rect.Contains(p))
            {
                ResidentClicked?.Invoke(this, album);
                e.Handled = true;
                return;
            }
    }

    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        var size = BuildingSize;
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Sky, bounds);
        var x0 = Math.Max(Pad, (bounds.Width - size.Width) / 2 + Pad);
        var width = _building.UnitsPerFloor * Unit;

        // Roof and sign.
        var roof = new StreamGeometry();
        using (var g = roof.Open())
        {
            g.BeginFigure(new Point(x0 - 10, Pad + Roof), true);
            g.LineTo(new Point(x0 + 30, Pad + 12));
            g.LineTo(new Point(x0 + width - 30, Pad + 12));
            g.LineTo(new Point(x0 + width + 10, Pad + Roof));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(RoofBrush, null, roof);
        var sign = Text("Album Town Apartments", 15, SignText);
        ctx.DrawText(sign, new Point(x0 + (width - sign.Width) / 2, Pad + 12 + (Roof - 12 - sign.Height) / 2));

        var top = Pad + Roof;
        ctx.FillRectangle(Facade, new Rect(x0 - 6, top, width + 12, Math.Max(1, _building.Floors) * FloorHeight + 6));
        var view = Viewport?.Invoke() ?? bounds;

        for (var floor = 0; floor < _building.Floors; floor++)
        {
            var y = top + floor * FloorHeight;
            if (y > view.Bottom + FloorHeight || y + FloorHeight < view.Top - FloorHeight) continue;
            var used = 0;
            foreach (var apt in _building.Apartments)
            {
                if (apt.Floor != floor) continue;
                DrawApartment(ctx, apt, new Rect(x0 + apt.Column * Unit + 3, y + 6, apt.Units * Unit - 6, FloorHeight - 9));
                used = Math.Max(used, apt.Column + apt.Units);
            }
            if (used < _building.UnitsPerFloor)
            {
                var r = new Rect(x0 + used * Unit + 3, y + 6, (_building.UnitsPerFloor - used) * Unit - 6, FloorHeight - 9);
                ctx.DrawRectangle(Vacant, RoomPen, new RoundedRect(r, 8));
                var t = Text("For rent", 11, VacantText);
                ctx.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
            }
        }
        if (_building.Floors == 0)
        {
            var t = Text("Nobody lives here yet.", 13, VacantText);
            ctx.DrawText(t, new Point(x0 + (width - t.Width) / 2, top + 40));
        }
    }

    private void DrawApartment(DrawingContext ctx, Apartment apt, Rect r)
    {
        var wall = CoverCache.Pastel(apt.Artist, 0.86f);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(wall), RoomPen, new RoundedRect(r, 8));
        // Floorboards.
        ctx.FillRectangle(Floorboards, new Rect(r.X + 1, r.Bottom - 9, r.Width - 2, 8));

        var name = Text(Fit(apt.Artist, apt.Units), 11, LabelBrush);
        ctx.DrawText(name, new Point(r.X + 7, r.Y + 5));
        var tier = Text(apt.Albums.Count == 1 ? apt.Tier : $"{apt.Tier} · {apt.Albums.Count.ToString(CultureInfo.InvariantCulture)} albums", 9.5, DimLabel);
        if (tier.Width < r.Width - 12) ctx.DrawText(tier, new Point(r.X + 7, r.Y + 20));

        // Residents: little cubes lined up on the floor, in rows.
        var capacity = Apartments.Capacity(apt.Units);
        var perRow = Math.Max(1, (int)((r.Width - 10) / (Resident + 8)));
        var shown = Math.Min(apt.Albums.Count, capacity);
        for (var i = 0; i < shown; i++)
        {
            var row = i / perRow;
            var col = i % perRow;
            var cx = r.X + 8 + col * (Resident + 8);
            var cy = r.Bottom - 12 - Resident - row * (Resident + 9);
            if (cy < r.Y + 38) break; // keep clear of the name and tier
            if (i == shown - 1 && apt.Albums.Count > shown)
            {
                var more = Text("+" + (apt.Albums.Count - shown + 1).ToString(CultureInfo.InvariantCulture), 10, LabelBrush);
                ctx.DrawText(more, new Point(cx + 2, cy + 4));
                break;
            }
            DrawResident(ctx, apt.Albums[i], new Rect(cx, cy, Resident, Resident));
        }
    }

    private void DrawResident(DrawingContext ctx, TownAlbum album, Rect front)
    {
        var cover = _covers.Get(album.ArtworkPath);
        var d = 5.0;
        var lid = new StreamGeometry();
        using (var g = lid.Open())
        {
            g.BeginFigure(front.TopLeft, true);
            g.LineTo(new Point(front.X + d, front.Y - d));
            g.LineTo(new Point(front.Right + d, front.Y - d));
            g.LineTo(new Point(front.Right + d, front.Bottom - d));
            g.LineTo(front.BottomRight);
            g.LineTo(front.TopRight);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new ImmutableSolidColorBrush(cover?.Side ?? CoverCache.Pastel(album.Key, 0.5f)), CubePen, lid);
        var rounded = new RoundedRect(front, 3);
        if (cover?.Image is { } image)
            using (ctx.PushClip(rounded))
                ctx.DrawImage(image, new Rect(image.Size), front);
        else ctx.DrawRectangle(new ImmutableSolidColorBrush(CoverCache.Pastel(album.Key, 0.7f)), null, rounded);
        ctx.DrawRectangle(null, CubePen, rounded);
        // Two dot eyes so they are clearly residents, not pictures on a shelf.
        ctx.DrawEllipse(Brushes.White, null, new Point(front.X + front.Width * 0.32, front.Y + front.Height * 0.38), 2.4, 2.8);
        ctx.DrawEllipse(Brushes.White, null, new Point(front.X + front.Width * 0.68, front.Y + front.Height * 0.38), 2.4, 2.8);
        ctx.DrawEllipse(LabelBrush, null, new Point(front.X + front.Width * 0.32, front.Y + front.Height * 0.42), 1.2, 1.4);
        ctx.DrawEllipse(LabelBrush, null, new Point(front.X + front.Width * 0.68, front.Y + front.Height * 0.42), 1.2, 1.4);
        _hits.Add((front.Inflate(3), album));
    }

    private static string Fit(string s, int units)
    {
        var max = units * 9;
        return s.Length <= max ? s : s[..Math.Max(1, max - 3)].TrimEnd() + "...";
    }

    private FormattedText Text(string s, double size, IBrush brush)
    {
        var key = s + "|" + size.ToString("0.#", CultureInfo.InvariantCulture) + "|" + brush.GetHashCode().ToString(CultureInfo.InvariantCulture);
        if (_texts.TryGetValue(key, out var t)) return t;
        if (_texts.Count > 2000) _texts.Clear();
        return _texts[key] = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush);
    }
}
