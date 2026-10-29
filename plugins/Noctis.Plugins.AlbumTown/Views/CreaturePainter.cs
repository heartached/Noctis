using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// Draws one creature, flat: a rounded-square album tile with a soft drop shadow, two small eyes
/// on the cover, and small round hands and feet floating beside and under it (no arms or legs).
/// <para>
/// Built for a steady frame: each creature's tile (cover, rounded corners, shadow, hairline edge)
/// is rendered once into a bitmap at its drawn size and the screen's pixel density, so a frame
/// only blits it 1:1 and adds a few ellipses for the eyes and limbs. Tiles are rebuilt when the
/// size, the density, the theme or the cover changes, a few per frame so a zoom never stalls
/// (meanwhile the old tile is drawn scaled). Speech bubbles, name tags and notes are bitmaps too,
/// made once per line: drawing text directly reshapes it every frame (~2.4 KB of garbage per
/// string in Avalonia 12), a bitmap costs one draw call. Nothing in the per-frame path allocates.
/// </para>
/// </summary>
public sealed class CreaturePainter : IDisposable
{
    /// <summary>Corner radius as a share of the tile (album tiles in Noctis are softly rounded).</summary>
    public const double CornerShare = 0.14;
    /// <summary>Tiles (re)built per frame at most.</summary>
    public const int BuildsPerFrame = 6;

    private readonly CoverCache _covers;
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private readonly Queue<(RenderTargetBitmap Bitmap, long Frame)> _retired = new();
    private readonly List<string> _prune = new();
    private readonly Dictionary<(string Text, LabelKind Kind), Label> _labels = new();
    private readonly List<(string, LabelKind)> _pruneLabels = new();
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface NameFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);

    private TownTheme _theme = TownTheme.Dark;
    private long _frame;
    private int _budget = BuildsPerFrame;
    private int _labelBudget = LabelsPerFrame;

    /// <summary>Text labels (bubbles, name tags) made per frame at most.</summary>
    public const int LabelsPerFrame = 8;

    private enum LabelKind { Bubble, Name, Note, Chord }

    public CreaturePainter(CoverCache covers) => _covers = covers;

    /// <summary>Width of the cover tile in px.</summary>
    public double Size { get; set; } = PlazaWorld.BaseSize;

    /// <summary>Device pixels per px (the window's render scaling); tiles are rendered at it.</summary>
    public double Scaling { get; set; } = 1;

    public TownTheme Palette
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(value, _theme)) return;
            _theme = value;
            // Shadows, edges and text colours come from the theme: rebuild.
            foreach (var s in _sprites.Values) s.Stale = true;
            ClearLabels();
        }
    }

    /// <summary>Bitmaps drawn in the current frame (tiles and labels; tests budget the frame by it).</summary>
    public int ImagesDrawn { get; private set; }

    /// <summary>Tiles currently kept (tests).</summary>
    public int SpriteCount => _sprites.Count;

    /// <summary>Starts a frame: refills the build budget, frees tiles nobody drew for a while.</summary>
    public void BeginFrame()
    {
        _frame++;
        _budget = BuildsPerFrame;
        _labelBudget = LabelsPerFrame;
        ImagesDrawn = 0;
        while (_retired.Count > 0 && _frame - _retired.Peek().Frame > 120) _retired.Dequeue().Bitmap.Dispose();
        if (_frame % 120 != 0) return;
        _pruneLabels.Clear();
        foreach (var (key, label) in _labels)
            if (_frame - label.UsedFrame > 240) _pruneLabels.Add(key);
        foreach (var key in _pruneLabels)
        {
            Retire(_labels[key].Bitmap);
            _labels.Remove(key);
        }
        _prune.Clear();
        foreach (var (key, sprite) in _sprites)
            if (_frame - sprite.UsedFrame > 240) _prune.Add(key);
        foreach (var key in _prune)
        {
            Retire(_sprites[key].Bitmap);
            _sprites.Remove(key);
        }
    }

    public void DrawShadow(DrawingContext ctx, Creature c)
    {
        var s = Size;
        // The shadow stays on the ground and shrinks a little as the body bobs up.
        var k = 1 - Bob(c) / (s * 2);
        ctx.DrawEllipse(_theme.GroundShadow, null, new Point(c.Position.X, c.Position.Y), s * 0.46 * k, s * 0.1 * k);
    }

    public void DrawBody(DrawingContext ctx, Creature c)
    {
        var t = _theme;
        var s = Size;
        var x = (double)c.Position.X;
        var ground = (double)c.Position.Y;
        var phase = c.WalkPhase;
        var stride = c.Stride;
        var sin = Math.Sin(phase);

        // Feet: two small rounded shoes floating under the tile, stepping with the walk.
        var swing = sin * s * 0.1 * stride;
        var lift = c.State == CreatureState.Dance ? s * 0.1 : s * 0.08 * stride;
        var liftL = Math.Max(0, sin) * lift;
        var liftR = Math.Max(0, -sin) * lift;
        var footY = ground - s * 0.07;
        ctx.DrawEllipse(t.Limb, t.LimbPen, new Point(x - s * 0.2 + swing, footY - liftL), s * 0.1, s * 0.065);
        ctx.DrawEllipse(t.Limb, t.LimbPen, new Point(x + s * 0.2 - swing, footY - liftR), s * 0.1, s * 0.065);

        // The tile. Squashes a little on the beat.
        var squash = c.State == CreatureState.Dance ? 1 - 0.07 * c.Pulse : 1;
        var w = s * (2 - squash);
        var h = s * squash;
        var bottom = ground - s * 0.3 - Bob(c);
        var left = x - w / 2;
        var top = bottom - h;
        var tile = new Rect(left, top, w, h);
        DrawTile(ctx, c, tile);

        if (c.IsPlaying)
            using (ctx.PushOpacity(0.45 + 0.55 * c.Pulse))
                ctx.DrawRectangle(null, t.AccentRing, new RoundedRect(tile.Inflate(4), s * CornerShare + 4));

        // Eyes on the cover, looking where the creature looks.
        var eyeY = top + h * 0.36;
        var eyeDx = s * 0.17;
        var gx = Math.Clamp(c.Gaze.X, -1, 1) * s * 0.03;
        var gy = Math.Clamp(c.Gaze.Y, -1, 1) * s * 0.025;
        if (c.BlinkTimer < 0)
        {
            ctx.DrawLine(t.BlinkPen, new Point(x - eyeDx - s * 0.06, eyeY), new Point(x - eyeDx + s * 0.06, eyeY));
            ctx.DrawLine(t.BlinkPen, new Point(x + eyeDx - s * 0.06, eyeY), new Point(x + eyeDx + s * 0.06, eyeY));
        }
        else
        {
            ctx.DrawEllipse(t.EyeWhite, t.EyePen, new Point(x - eyeDx, eyeY), s * 0.075, s * 0.09);
            ctx.DrawEllipse(t.EyeWhite, t.EyePen, new Point(x + eyeDx, eyeY), s * 0.075, s * 0.09);
            ctx.DrawEllipse(t.Pupil, null, new Point(x - eyeDx + gx, eyeY + gy + s * 0.01), s * 0.038, s * 0.045);
            ctx.DrawEllipse(t.Pupil, null, new Point(x + eyeDx + gx, eyeY + gy + s * 0.01), s * 0.038, s * 0.045);
        }

        // Hands: round and floating beside the tile.
        var handY = bottom - h * 0.36;
        var armSwing = sin * s * 0.07 * stride;
        var lx = left - s * 0.13;
        var rx = left + w + s * 0.13;
        var ly = handY + armSwing;
        var ry = handY - armSwing;
        if (c.State == CreatureState.Wave)
        {
            // One hand up, waving, on the side it faces.
            var waveX = sin * s * 0.1;
            if (c.Facing >= 0) { rx += waveX; ry = top - s * 0.06; }
            else { lx += waveX; ly = top - s * 0.06; }
        }
        else if (c.State == CreatureState.Dance)
        {
            ly = top + h * 0.15 - Math.Abs(sin) * s * 0.22;
            ry = top + h * 0.15 - Math.Abs(Math.Cos(phase)) * s * 0.22;
        }
        else if (c.State == CreatureState.Talk && c.Bubble is not null)
        {
            ry -= Math.Abs(Math.Sin(c.Age * 7)) * s * 0.07;
        }
        ctx.DrawEllipse(t.Limb, t.LimbPen, new Point(lx, ly), s * 0.08, s * 0.08);
        ctx.DrawEllipse(t.Limb, t.LimbPen, new Point(rx, ry), s * 0.08, s * 0.08);

        if (c.IsPlaying) DrawNotes(ctx, c, x, top);
    }

    /// <summary>Name tag under the feet: a small flat pill.</summary>
    public void DrawName(DrawingContext ctx, Creature c)
    {
        if (LabelFor(c.Album.Title, LabelKind.Name) is not { } label) return;
        DrawLabel(ctx, label, c.Position.X - label.Width / 2, c.Position.Y + Size * 0.06);
    }

    /// <summary>Speech bubble above the head: a flat rounded pill.</summary>
    public void DrawBubble(DrawingContext ctx, Creature c, Rect bounds)
    {
        if (c.Bubble is not { } line || LabelFor(line, LabelKind.Bubble) is not { } label) return;
        var tipX = c.Position.X;
        var tipY = c.Position.Y - Size * 1.36 - Bob(c);
        var x = Math.Clamp(tipX - label.Width / 2, bounds.X + 4, Math.Max(bounds.X + 4, bounds.Right - label.Width - 4));
        var y = Math.Max(bounds.Y + 4, tipY - label.Height);
        DrawLabel(ctx, label, x, y);
    }

    private void DrawNotes(DrawingContext ctx, Creature c, double x, double top)
    {
        for (var i = 0; i < 2; i++)
        {
            if (LabelFor(i == 0 ? "\u266A" : "\u266B", i == 0 ? LabelKind.Note : LabelKind.Chord) is not { } glyph) continue;
            var t = (c.Age * 0.6 + i * 0.5) % 1.0;
            var nx = x + (i == 0 ? -1 : 1) * Size * (0.38 + 0.18 * t);
            var ny = top - Size * 0.12 - t * Size * 0.7;
            using (ctx.PushOpacity(1 - t))
                DrawLabel(ctx, glyph, nx - glyph.Width / 2, ny - glyph.Height / 2);
        }
    }

    private void DrawLabel(DrawingContext ctx, Label label, double x, double y)
    {
        ctx.DrawImage(label.Bitmap, label.Source, new Rect(Snap(x), Snap(y), label.Width, label.Height));
        ImagesDrawn++;
    }

    /// <summary>The label bitmap for <paramref name="text"/>, made now if the frame's budget allows.</summary>
    private Label? LabelFor(string text, LabelKind kind)
    {
        var size = FontSize(kind);
        if (_labels.TryGetValue((text, kind), out var label) && label.FontSize == size && label.Scaling == Scaling)
        {
            label.UsedFrame = _frame;
            return label;
        }
        if (_labelBudget <= 0) return label; // an older one (other size) until there is budget
        _labelBudget--;
        if (label is not null) Retire(label.Bitmap);
        label = BuildLabel(text, kind, size);
        label.UsedFrame = _frame;
        _labels[(text, kind)] = label;
        return label;
    }

    private double FontSize(LabelKind kind) => kind switch
    {
        LabelKind.Bubble => Quantize(Math.Clamp(Size * 0.13, 12, 16)),
        LabelKind.Name => Quantize(Math.Clamp(Size * 0.12, 11, 14)),
        _ => Quantize(Math.Max(12, Size * 0.3)),
    };

    private Label BuildLabel(string text, LabelKind kind, double size)
    {
        var t = _theme;
        FormattedText formatted;
        double padX, padY;
        IBrush? fill;
        IPen? pen = null;
        switch (kind)
        {
            case LabelKind.Bubble:
                formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, t.BubbleText)
                {
                    MaxTextWidth = Math.Max(170, Size * 2.6),
                };
                padX = size * 0.95;
                padY = size * 0.55;
                fill = t.Bubble;
                pen = t.BubblePen;
                break;
            case LabelKind.Name:
                var shown = text.Length == 0 ? "Untitled" : text.Length > 26 ? text[..25].TrimEnd() + "..." : text;
                formatted = new FormattedText(shown, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, NameFace, size, t.TagText)
                {
                    MaxTextWidth = Math.Max(120, Size * 2.4),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                padX = size * 0.75;
                padY = size * 0.2;
                fill = t.Tag;
                break;
            default:
                formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, t.AccentBrush);
                padX = padY = 1;
                fill = null;
                break;
        }
        var w = Math.Ceiling(formatted.Width + padX * 2);
        var h = Math.Ceiling(formatted.Height + padY * 2);
        var bitmap = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Ceiling(w * Scaling)), Math.Max(1, (int)Math.Ceiling(h * Scaling))),
            new Vector(96 * Scaling, 96 * Scaling));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            if (fill is not null)
            {
                var rect = new Rect(0.5, 0.5, w - 1, h - 1);
                var radius = kind == LabelKind.Bubble ? Math.Min(rect.Height / 2, size * 1.25) : rect.Height / 2;
                ctx.DrawRectangle(fill, pen, new RoundedRect(rect, radius));
            }
            ctx.DrawText(formatted, new Point(padX, padY));
        }
        return new Label(bitmap, w, h, size, Scaling);
    }

    private void ClearLabels()
    {
        foreach (var label in _labels.Values) Retire(label.Bitmap);
        _labels.Clear();
    }

    /// <summary>The cover tile into <paramref name="tile"/>: the prebuilt bitmap when there is one.</summary>
    private void DrawTile(DrawingContext ctx, Creature c, Rect tile)
    {
        var sprite = SpriteFor(c);
        if (sprite is null)
        {
            // No tile yet (over this frame's build budget): the plain cover, clipped.
            var rounded = new RoundedRect(tile, Size * CornerShare);
            ctx.DrawRectangle(_theme.Placeholder, null, rounded);
            if (_covers.Get(c.Album.ArtworkPath)?.Image is { } image)
                using (ctx.PushClip(rounded))
                    ctx.DrawImage(image, SquareCrop(image.Size), tile);
            return;
        }
        // The bitmap holds the tile plus room for its shadow; place it so the tile lands on
        // whole device pixels (a 1:1 blit stays crisp).
        var kx = tile.Width / sprite.Size;
        var ky = tile.Height / sprite.Size;
        var dest = new Rect(Snap(tile.X - sprite.Pad * kx), Snap(tile.Y - sprite.Pad * ky), sprite.Logical * kx, sprite.Logical * ky);
        ctx.DrawImage(sprite.Bitmap, sprite.Source, dest);
        ImagesDrawn++;
    }

    private Sprite? SpriteFor(Creature c)
    {
        var key = c.Album.Key;
        _sprites.TryGetValue(key, out var sprite);
        var path = c.Album.ArtworkPath;
        var cover = string.IsNullOrEmpty(path) ? null : _covers.Get(path);
        var hasCover = cover?.Image is not null;
        if (sprite is not null)
        {
            sprite.UsedFrame = _frame;
            var current = !sprite.Stale && sprite.Size == Size && sprite.Scaling == Scaling && sprite.HasCover == hasCover;
            if (current || _budget <= 0) return sprite;
        }
        else if (_budget <= 0) return null;
        _budget--;
        var built = Build(c.Album, cover?.Image);
        built.UsedFrame = _frame;
        if (sprite is not null) Retire(sprite.Bitmap);
        _sprites[key] = built;
        return built;
    }

    private Sprite Build(TownAlbum album, Bitmap? image)
    {
        var s = Size;
        var pad = Math.Ceiling(s * 0.3);
        var logical = s + 2 * pad;
        var px = Math.Max(1, (int)Math.Ceiling(logical * Scaling));
        var bitmap = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96 * Scaling, 96 * Scaling));
        var tile = new Rect(pad, pad, s, s);
        var rounded = new RoundedRect(tile, s * CornerShare);
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var shadow = new BoxShadows(new BoxShadow { OffsetY = s * 0.05, Blur = s * 0.2, Color = _theme.TileShadow });
            var fill = image is null ? new ImmutableSolidColorBrush(CoverCache.Pastel(album.Key, _theme.IsDark ? 0.52f : 0.72f)) : _theme.Placeholder;
            ctx.DrawRectangle(fill, null, rounded, shadow);
            if (image is not null)
            {
                using (ctx.PushClip(rounded))
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(image, SquareCrop(image.Size), tile);
            }
            else
            {
                var note = new FormattedText("♪", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, s * 0.36, _theme.PlaceholderText);
                ctx.DrawText(note, new Point(tile.Center.X - note.Width / 2, tile.Y + s * 0.72 - note.Height / 2));
            }
            ctx.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(_theme.CoverEdge), 1), new RoundedRect(tile.Deflate(0.5), s * CornerShare - 0.5));
        }
        return new Sprite(bitmap, s, Scaling, pad, logical, image is not null);
    }

    private void Retire(RenderTargetBitmap bitmap) => _retired.Enqueue((bitmap, _frame));

    private double Snap(double v) => Scaling > 0 ? Math.Round(v * Scaling) / Scaling : v;

    private static Rect SquareCrop(Size size)
    {
        var side = Math.Min(size.Width, size.Height);
        return new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
    }

    private static double Quantize(double size) => Math.Round(size * 2) / 2;

    /// <summary>How far the body floats up this frame (walk bob or the dance bounce).</summary>
    private double Bob(Creature c)
    {
        var s = Size;
        return c.State == CreatureState.Dance
            ? Math.Abs(Math.Sin(c.WalkPhase)) * s * 0.1 + c.Pulse * s * 0.08
            : Math.Abs(Math.Sin(c.WalkPhase)) * s * 0.05 * c.Stride;
    }

    public void Dispose()
    {
        foreach (var s in _sprites.Values) s.Bitmap.Dispose();
        _sprites.Clear();
        foreach (var l in _labels.Values) l.Bitmap.Dispose();
        _labels.Clear();
        while (_retired.Count > 0) _retired.Dequeue().Bitmap.Dispose();
    }

    private sealed class Label
    {
        public Label(RenderTargetBitmap bitmap, double width, double height, double fontSize, double scaling)
        {
            Bitmap = bitmap;
            Width = width;
            Height = height;
            FontSize = fontSize;
            Scaling = scaling;
            Source = new Rect(bitmap.Size);
        }

        public RenderTargetBitmap Bitmap { get; }
        public double Width { get; }
        public double Height { get; }
        public double FontSize { get; }
        public double Scaling { get; }
        public Rect Source { get; }
        public long UsedFrame { get; set; }
    }

    private sealed class Sprite
    {
        public Sprite(RenderTargetBitmap bitmap, double size, double scaling, double pad, double logical, bool hasCover)
        {
            Bitmap = bitmap;
            Size = size;
            Scaling = scaling;
            Pad = pad;
            Logical = logical;
            HasCover = hasCover;
            Source = new Rect(bitmap.Size);
        }

        public RenderTargetBitmap Bitmap { get; }
        public double Size { get; }
        public double Scaling { get; }
        public double Pad { get; }
        public double Logical { get; }
        public bool HasCover { get; }
        public Rect Source { get; }
        public bool Stale { get; set; }
        public long UsedFrame { get; set; }
    }
}
