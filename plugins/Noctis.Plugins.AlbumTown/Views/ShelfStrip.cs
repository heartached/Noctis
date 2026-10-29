using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>One cover on the shelf: an album or a playlist, in a group ("Playlists", or a genre).</summary>
public sealed record ShelfItem(RoomKind Kind, string Source, string Title, string Subtitle, IReadOnlyList<string> Covers, string Group);

/// <summary>
/// The cover shelf under the building (Jafezy's sketch: covers in a row, grouped by genre with
/// the genre written sideways between them). Drawn by hand and only the part in view, so a
/// library of thousands of albums stays light; it sits in a horizontal ScrollViewer. Press a
/// cover and drag it up into the building to build its room; double-click puts it in the first
/// free spot.
/// </summary>
public sealed class ShelfStrip : Control
{
    public const double Tile = 84;
    public const double Spacing = 12;
    public const double LabelWidth = 22;
    public const double StripHeight = Tile + 34;
    private const double DragThreshold = 6;

    private static readonly Typeface TitleFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
    private static readonly Typeface GroupFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly CoverCache _covers;
    private readonly List<(double X, ShelfItem? Item, string? Group)> _slots = new();
    private readonly Dictionary<ShelfItem, FormattedText> _titles = new();
    private readonly Dictionary<string, FormattedText> _groups = new(StringComparer.Ordinal);
    private IReadOnlyList<ShelfItem> _items = Array.Empty<ShelfItem>();
    private TownTheme _theme = TownTheme.Dark;
    private double _width;
    private ShelfItem? _pressed;
    private Point _pressAt;
    private bool _dragging;

    public ShelfStrip(CoverCache covers)
    {
        _covers = covers;
        covers.CoverReady += (_, _) => { if (IsEffectivelyVisible) InvalidateVisual(); };
        Cursor = new Cursor(StandardCursorType.Hand);
        Height = StripHeight;
    }

    /// <summary>The visible part (the scroll viewport), in this control's coordinates.</summary>
    public Func<Rect>? Viewport { get; set; }

    /// <summary>True when the item already has a room (drawn with a small dot).</summary>
    public Func<ShelfItem, bool>? IsPlaced { get; set; }

    public IReadOnlyList<ShelfItem> Items => _items;

    public TownTheme Palette
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(value, _theme)) return;
            _theme = value;
            _titles.Clear();
            _groups.Clear();
            InvalidateVisual();
        }
    }

    /// <summary>A cover started being dragged (the view follows it over the building).</summary>
    public event EventHandler<(ShelfItem Item, PointerEventArgs Args)>? DragStarted;
    public event EventHandler<PointerEventArgs>? DragMoved;
    public event EventHandler<PointerEventArgs>? DragEnded;
    /// <summary>Double-click: build its room in the first free spot.</summary>
    public event EventHandler<ShelfItem>? Activated;

    /// <summary>New items, already grouped and in order (groups in the order they first appear).</summary>
    public void SetItems(IReadOnlyList<ShelfItem> items)
    {
        _items = items;
        _slots.Clear();
        _titles.Clear();
        var x = Spacing;
        string? group = null;
        foreach (var item in items)
        {
            if (!string.Equals(item.Group, group, StringComparison.Ordinal))
            {
                group = item.Group;
                if (_slots.Count > 0) x += Spacing / 2;
                _slots.Add((x, null, group));
                x += LabelWidth + Spacing / 2;
            }
            _slots.Add((x, item, null));
            x += Tile + Spacing;
        }
        _width = x;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Where <paramref name="item"/>'s cover is (tests), or null.</summary>
    public Rect? TileRect(ShelfItem item)
    {
        foreach (var (x, it, _) in _slots)
            if (ReferenceEquals(it, item)) return new Rect(x, 8, Tile, Tile);
        return null;
    }

    public ShelfItem? ItemAt(Point p)
    {
        if (p.Y < 8 || p.Y > 8 + Tile + 22) return null;
        foreach (var (x, item, _) in _slots)
            if (item is not null && p.X >= x && p.X <= x + Tile) return item;
        return null;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(_width, 1), StripHeight);

    public override void Render(DrawingContext ctx)
    {
        var view = Viewport?.Invoke() ?? new Rect(Bounds.Size);
        foreach (var (x, item, group) in _slots)
        {
            if (x > view.Right + Tile || x + Tile < view.Left - Tile) continue;
            if (group is not null) DrawGroup(ctx, group, x);
            else if (item is not null) DrawTile(ctx, item, x);
        }
    }

    private void DrawGroup(DrawingContext ctx, string group, double x)
    {
        if (!_groups.TryGetValue(group, out var text))
            _groups[group] = text = new FormattedText(TalkLines.Shorten(group, 22), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, GroupFace, 11, _theme.Muted)
            {
                MaxTextWidth = Tile + 20, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
        // Written sideways, bottom to top, centred on the covers (as in Jafezy's sketch).
        var cx = x + LabelWidth / 2;
        var cy = 8 + Tile / 2;
        using (ctx.PushTransform(Matrix.CreateTranslation(-text.Width / 2, -text.Height / 2) * Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(cx, cy)))
            ctx.DrawText(text, new Point(0, 0));
    }

    private void DrawTile(DrawingContext ctx, ShelfItem item, double x)
    {
        var tile = new Rect(x, 8, Tile, Tile);
        var rounded = new RoundedRect(tile, 10);
        ctx.DrawRectangle(_theme.Placeholder, null, rounded);
        using (ctx.PushClip(rounded))
        {
            if (item.Covers.Count <= 1)
            {
                if (_covers.Get(item.Covers.Count == 1 ? item.Covers[0] : null)?.Image is { } image) ctx.DrawImage(image, Crop(image), tile);
                else if (item.Kind == RoomKind.Playlist) DrawNote(ctx, tile);
            }
            else
            {
                // A playlist without a cover of its own: its first covers, four to a tile.
                for (var i = 0; i < 4; i++)
                {
                    var q = new Rect(x + i % 2 * Tile / 2, 8 + i / 2 * Tile / 2, Tile / 2, Tile / 2);
                    if (_covers.Get(item.Covers[i % item.Covers.Count])?.Image is { } image) ctx.DrawImage(image, Crop(image), q);
                }
            }
        }
        ctx.DrawRectangle(null, _theme.ShelfEdge, rounded);
        if (IsPlaced?.Invoke(item) == true)
        {
            // Has a room already: a small accent dot in the corner.
            ctx.DrawEllipse(_theme.Card, null, new Point(tile.Right - 11, tile.Top + 11), 7, 7);
            ctx.DrawEllipse(_theme.AccentBrush, null, new Point(tile.Right - 11, tile.Top + 11), 4.5, 4.5);
        }
        if (!_titles.TryGetValue(item, out var title))
            _titles[item] = title = new FormattedText(item.Title.Length == 0 ? "Untitled" : item.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleFace, 11, _theme.Text)
            {
                MaxTextWidth = Tile, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
        ctx.DrawText(title, new Point(x, tile.Bottom + 5));
    }

    private void DrawNote(DrawingContext ctx, Rect tile)
    {
        if (!_groups.TryGetValue("♫", out var note))
            _groups["♫"] = note = new FormattedText("♫", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GroupFace, 28, _theme.PlaceholderText);
        ctx.DrawText(note, new Point(tile.Center.X - note.Width / 2, tile.Center.Y - note.Height / 2));
    }

    private static Rect Crop(Avalonia.Media.Imaging.Bitmap image)
    {
        var px = image.PixelSize;
        var side = Math.Min(px.Width, px.Height);
        return new Rect((px.Width - side) / 2.0, (px.Height - side) / 2.0, side, side);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || ItemAt(point.Position) is not { } item) return;
        if (e.ClickCount >= 2)
        {
            _pressed = null;
            Activated?.Invoke(this, item);
            e.Handled = true;
            return;
        }
        _pressed = item;
        _pressAt = point.Position;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressed is null) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            var d = p - _pressAt;
            if (Math.Sqrt(d.X * d.X + d.Y * d.Y) < DragThreshold) return;
            _dragging = true;
            DragStarted?.Invoke(this, (_pressed, e));
        }
        DragMoved?.Invoke(this, e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is null) return;
        var dragging = _dragging;
        _pressed = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (dragging) DragEnded?.Invoke(this, e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressed is not null && _dragging) DragEnded?.Invoke(this, null!);
        _pressed = null;
        _dragging = false;
    }

    /// <summary>
    /// The shelf's items: "Playlists" first, then albums grouped by genre (biggest genres first,
    /// "Other" last), each group by artist and title. <paramref name="query"/> keeps the ones
    /// whose title, artist, genre or playlist name contain every word.
    /// </summary>
    public static IReadOnlyList<ShelfItem> Arrange(IEnumerable<ShelfItem> playlists, IEnumerable<ShelfItem> albums, string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Match(ShelfItem i) => words.All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || i.Subtitle.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Group.Contains(w, StringComparison.OrdinalIgnoreCase));
        var list = new List<ShelfItem>();
        list.AddRange(playlists.Where(Match));
        var groups = albums.Where(Match)
            .GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, OtherGenre, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var name = g.First().Group;
            list.AddRange(g.OrderBy(a => a.Subtitle, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .Select(a => a.Group == name ? a : a with { Group = name }));
        }
        return list;
    }

    public const string OtherGenre = "Other";
    public const string PlaylistsGroup = "Playlists";
}
