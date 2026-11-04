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
        var act = c.State == CreatureState.Talk && c.Chat is { } chat ? chat.Act : ChatAct.Talk;
        // A scuffle: both shake on the spot.
        var x = (double)c.Position.X + (act == ChatAct.Scuffle ? Math.Sin(c.Age * 45) * s * 0.035 : 0);
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
        DrawLimb(ctx, foot: true, new Point(x - s * 0.2 + swing, footY - liftL));
        DrawLimb(ctx, foot: true, new Point(x + s * 0.2 - swing, footY - liftR));

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
        EnsureLimbs();
        if (c.BlinkTimer < 0)
        {
            ctx.DrawLine(t.BlinkPen, new Point(x - eyeDx - s * 0.06, eyeY), new Point(x - eyeDx + s * 0.06, eyeY));
            ctx.DrawLine(t.BlinkPen, new Point(x + eyeDx - s * 0.06, eyeY), new Point(x + eyeDx + s * 0.06, eyeY));
        }
        else
        {
            if (SpriteLimbs) DrawSprite(ctx, _eyes!, new Point(x, eyeY));
            else
            {
                ctx.DrawEllipse(t.EyeWhite, t.EyePen, new Point(x - eyeDx, eyeY), s * 0.075, s * 0.09);
                ctx.DrawEllipse(t.EyeWhite, t.EyePen, new Point(x + eyeDx, eyeY), s * 0.075, s * 0.09);
            }
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
        else if (act == ChatAct.RockPaperScissors)
        {
            // Rock, paper, scissors: the hand toward the other pumps on each word.
            var pump = Math.Abs(Math.Sin(c.Age * 8)) * s * 0.18;
            if (c.Facing >= 0) ry -= pump; else ly -= pump;
        }
        else if (act == ChatAct.Scuffle)
        {
            // Little punches toward the other, one hand then the other.
            var jab = Math.Max(0, Math.Sin(c.Age * 14)) * s * 0.16;
            var jab2 = Math.Max(0, -Math.Sin(c.Age * 14)) * s * 0.16;
            if (c.Facing >= 0) { rx += jab; lx += jab2 * 0.6; ly -= s * 0.06; }
            else { lx -= jab; rx -= jab2 * 0.6; ry -= s * 0.06; }
        }
        else if (act == ChatAct.HighFive)
        {
            // The hand toward the other goes up high.
            if (c.Facing >= 0) { rx += s * 0.06; ry = top - s * 0.12; }
            else { lx -= s * 0.06; ly = top - s * 0.12; }
        }
        else if (c.State == CreatureState.Talk && c.Bubble is not null)
        {
            ry -= Math.Abs(Math.Sin(c.Age * 7)) * s * 0.07;
        }
        DrawLimb(ctx, foot: false, new Point(lx, ly));
        DrawLimb(ctx, foot: false, new Point(rx, ry));

        if (c.IsPlaying) DrawNotes(ctx, c, x, top);
    }

    // A hand and a foot, each a white ellipse with a hairline outline, made once per size, density
    // and theme: a creature has four, and blitting them costs a fraction of rasterizing an
    // antialiased filled and outlined ellipse every frame (the bulk of a creature's drawing).
    // The two eye whites too, as one (filled and outlined); the pupils stay plain dots so they can
    // look around. Each blit is one more image Avalonia records (it allocates a little for each),
    // so a canvas that repaints everything every frame (the Apartments) keeps the plain ellipses.
    private RenderTargetBitmap? _hand;
    private RenderTargetBitmap? _foot;
    private RenderTargetBitmap? _eyes;
    private (double Size, double Scaling, TownTheme? Theme) _limbsFor;

    /// <summary>Hands, feet and eye whites as cached bitmaps (true, the plaza) or drawn shapes.</summary>
    public bool SpriteLimbs { get; set; } = true;

    private void EnsureLimbs()
    {
        if (!SpriteLimbs || _limbsFor == (Size, Scaling, _theme)) return;
        if (_hand is not null) Retire(_hand);
        if (_foot is not null) Retire(_foot);
        if (_eyes is not null) Retire(_eyes);
        _hand = BuildEllipses(Size * 0.08, Size * 0.08, 0, _theme.Limb, _theme.LimbPen);
        _foot = BuildEllipses(Size * 0.1, Size * 0.065, 0, _theme.Limb, _theme.LimbPen);
        _eyes = BuildEllipses(Size * 0.075, Size * 0.09, Size * 0.17, _theme.EyeWhite, _theme.EyePen);
        _limbsFor = (Size, Scaling, _theme);
    }

    private void DrawLimb(DrawingContext ctx, bool foot, Point center)
    {
        if (!SpriteLimbs)
        {
            var s = Size;
            ctx.DrawEllipse(_theme.Limb, _theme.LimbPen, center, foot ? s * 0.1 : s * 0.08, foot ? s * 0.065 : s * 0.08);
            return;
        }
        EnsureLimbs();
        DrawSprite(ctx, foot ? _foot! : _hand!, center);
    }

    private void DrawSprite(DrawingContext ctx, RenderTargetBitmap bitmap, Point center)
    {
        var w = bitmap.PixelSize.Width / Scaling;
        var h = bitmap.PixelSize.Height / Scaling;
        ctx.DrawImage(bitmap, PixelSource(bitmap), new Rect(Snap(center.X - w / 2), Snap(center.Y - h / 2), w, h));
        ImagesDrawn++;
    }

    /// <summary>One ellipse, or two <paramref name="dx"/> left and right of the centre.</summary>
    private RenderTargetBitmap BuildEllipses(double rx, double ry, double dx, IBrush fill, IPen pen)
    {
        // Whole device pixels, odd sizes so the shape sits centred on a pixel.
        var px = new PixelSize((int)Math.Ceiling((rx * 2 + dx * 2 + 2) * Scaling) | 1, (int)Math.Ceiling((ry * 2 + 2) * Scaling) | 1);
        var bitmap = new RenderTargetBitmap(px, new Vector(96 * Scaling, 96 * Scaling));
        using var ctx = bitmap.CreateDrawingContext();
        var c = new Point(px.Width / Scaling / 2, px.Height / Scaling / 2);
        if (dx == 0) ctx.DrawEllipse(fill, pen, c, rx, ry);
        else
        {
            ctx.DrawEllipse(fill, pen, new Point(c.X - dx, c.Y), rx, ry);
            ctx.DrawEllipse(fill, pen, new Point(c.X + dx, c.Y), rx, ry);
        }
        return bitmap;
    }

    /// <summary>Name tag under the feet: a small flat pill, kept inside <paramref name="bounds"/>.</summary>
    public void DrawName(DrawingContext ctx, Creature c, Rect bounds)
    {
        if (LabelFor(c.Album.Title, LabelKind.Name) is not { } label) return;
        var rect = PlaceName(c, label, bounds);
        DrawLabel(ctx, label, rect.X, rect.Y);
    }

    /// <summary>Where <see cref="DrawName"/> puts the tag, or null when there is none yet.</summary>
    public Rect? NameBounds(Creature c, Rect bounds)
        => LabelFor(c.Album.Title, LabelKind.Name) is { } label ? PlaceName(c, label, bounds) : null;

    private Rect PlaceName(Creature c, Label label, Rect bounds)
        => Inside(new Rect(c.Position.X - label.Width / 2, c.Position.Y + Size * 0.06, label.Width, label.Height), bounds);

    // ── Labels drawn by their own visuals (the plaza moves them by transform) ──

    /// <summary>Size of <paramref name="c"/>'s name tag (made now if the frame's budget allows), or null.</summary>
    public Size? NameSize(Creature c)
        => LabelFor(c.Album.Title, LabelKind.Name) is { } label ? new Size(label.Width, label.Height) : null;

    /// <summary>Size of a bubble saying <paramref name="line"/> (made now if the budget allows), or null.</summary>
    public Size? BubbleSize(string line)
        => LabelFor(line, LabelKind.Bubble) is { } label ? new Size(label.Width, label.Height) : null;

    /// <summary>Draws <paramref name="c"/>'s name tag with its top-left at <paramref name="at"/>.</summary>
    public void DrawNameAt(DrawingContext ctx, Creature c, Point at)
    {
        if (LabelFor(c.Album.Title, LabelKind.Name) is { } label) DrawLabel(ctx, label, at.X, at.Y);
    }

    /// <summary>Draws a bubble saying <paramref name="line"/> with its top-left at <paramref name="at"/>.</summary>
    public void DrawBubbleAt(DrawingContext ctx, string line, Point at)
    {
        if (LabelFor(line, LabelKind.Bubble) is { } label) DrawLabel(ctx, label, at.X, at.Y);
    }

    /// <summary>Where a bubble goes when it flips under the feet (below the name tag).</summary>
    public double UnderFeet(Creature c) => c.Position.Y + Size * 0.06 + FontSize(LabelKind.Name) * 1.8 + 4;

    /// <summary>Marks <paramref name="c"/>'s tile as in use this frame (its visual may not redraw for a
    /// long while when it stands still, and an unused tile is freed after a few seconds).</summary>
    public void Touch(Creature c)
    {
        if (_sprites.TryGetValue(c.Album.Key, out var sprite)) sprite.UsedFrame = _frame;
    }

    /// <summary>True when <paramref name="c"/>'s tile is built for the current size, density and theme
    /// (its visual needs a redraw once it is).</summary>
    public bool HasCurrentTile(Creature c)
        => _sprites.TryGetValue(c.Album.Key, out var s) && !s.Stale && s.Size == Size && s.Scaling == Scaling;

    /// <summary>Speech bubble above the head: a flat rounded pill, kept inside <paramref name="bounds"/>.</summary>
    public void DrawBubble(DrawingContext ctx, Creature c, Rect bounds)
    {
        if (c.Bubble is { } line) DrawBubbleLine(ctx, line, c.Position.X, c.Position.Y - Size * 1.36 - Bob(c), c.Position.Y, bounds);
    }

    /// <summary>Where <see cref="DrawBubble"/> puts the bubble, or null when there is none.</summary>
    public Rect? BubbleBounds(Creature c, Rect bounds)
        => c.Bubble is { } line && LabelFor(line, LabelKind.Bubble) is { } label
            ? PlaceBubble(label, c.Position.X, c.Position.Y - Size * 1.36 - Bob(c), c.Position.Y, bounds)
            : null;

    /// <summary>
    /// Draws <paramref name="line"/> as a bubble whose tail points at (<paramref name="tipX"/>,
    /// <paramref name="headY"/>); returns where it went (null when not built yet).
    /// </summary>
    public Rect? DrawBubbleLine(DrawingContext ctx, string line, double tipX, double headY, double feetY, Rect bounds)
    {
        if (LabelFor(line, LabelKind.Bubble) is not { } label) return null;
        var rect = PlaceBubble(label, tipX, headY, feetY, bounds);
        DrawLabel(ctx, label, rect.X, rect.Y);
        return rect;
    }

    /// <summary>
    /// Above the head when it fits; near the top edge it flips under the feet (below the name
    /// tag) instead of covering the face, then it is shifted to stay inside the plaza.
    /// </summary>
    private Rect PlaceBubble(Label label, double tipX, double headY, double feetY, Rect bounds)
    {
        var y = headY - label.Height;
        if (y < bounds.Y + EdgeMargin)
            y = feetY + Size * 0.06 + FontSize(LabelKind.Name) * 1.8 + 4;
        return Inside(new Rect(tipX - label.Width / 2, y, label.Width, label.Height), bounds);
    }

    private const double EdgeMargin = 4;

    /// <summary><paramref name="r"/> shifted to lie inside <paramref name="bounds"/> (less a small margin) where it fits.</summary>
    private static Rect Inside(Rect r, Rect bounds)
    {
        var x = Math.Clamp(r.X, bounds.X + EdgeMargin, Math.Max(bounds.X + EdgeMargin, bounds.Right - r.Width - EdgeMargin));
        var y = Math.Clamp(r.Y, bounds.Y + EdgeMargin, Math.Max(bounds.Y + EdgeMargin, bounds.Bottom - r.Height - EdgeMargin));
        return new Rect(x, y, r.Width, r.Height);
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
    private Label? LabelFor(string text, LabelKind kind, bool force = false)
    {
        var size = FontSize(kind);
        var wrap = WrapWidth(kind, size);
        // The wrap width is part of the key: it follows the creature size even while the font
        // size sits at its clamp, and a bubble laid out for another width would wrap wrongly.
        if (_labels.TryGetValue((text, kind), out var label) && label.FontSize == size && label.Scaling == Scaling && label.Wrap == wrap)
        {
            label.UsedFrame = _frame;
            return label;
        }
        if (_labelBudget <= 0 && !force) return label; // an older one (other size) until there is budget
        _labelBudget--;
        if (label is not null) Retire(label.Bitmap);
        label = BuildLabel(text, kind, size, wrap);
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

    /// <summary>Width of the plaza in px; bubbles wrap narrower than it so they fit inside.</summary>
    public double AreaWidth { get; set; } = double.PositiveInfinity;

    /// <summary>
    /// Where a label's text wraps, in whole px. Bubbles: about 14 characters per line at least
    /// (so every line the town says fits in two), wider with bigger creatures, never wider than
    /// the plaza. Name tags: one line, trimmed with an ellipsis.
    /// </summary>
    private double WrapWidth(LabelKind kind, double size) => kind switch
    {
        LabelKind.Bubble => Math.Floor(Math.Max(size * 8, Math.Min(Math.Max(size * 14.5, Math.Min(Size * 2.6, size * 22)), AreaWidth - 2 * EdgeMargin - 2 * BubblePadX(size)))),
        LabelKind.Name => Math.Floor(Math.Max(size * 6, Math.Min(Math.Max(120, Size * 2.4), AreaWidth - 2 * EdgeMargin - 2 * size * 0.75))),
        _ => 0,
    };

    private static double BubblePadX(double size) => size * 0.95;

    /// <summary>Bubble lines wrap to at most this many lines (then trim with an ellipsis).</summary>
    public const int BubbleMaxLines = 2;

    private FormattedText Layout(string text, LabelKind kind, double size, double wrap, bool lineLimit, out double padX, out double padY)
    {
        var t = _theme;
        switch (kind)
        {
            case LabelKind.Bubble:
                padX = BubblePadX(size);
                padY = size * 0.55;
                var bubble = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, t.BubbleText) { MaxTextWidth = wrap };
                if (lineLimit)
                {
                    bubble.MaxLineCount = BubbleMaxLines;
                    bubble.Trimming = TextTrimming.WordEllipsis;
                }
                return bubble;
            case LabelKind.Name:
                padX = size * 0.75;
                padY = size * 0.2;
                return new FormattedText(text.Length == 0 ? "Untitled" : TalkLines.Shorten(text, 26), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, NameFace, size, t.TagText)
                {
                    MaxTextWidth = wrap,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
            default:
                padX = padY = 1;
                return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, t.AccentBrush);
        }
    }

    private Label BuildLabel(string text, LabelKind kind, double size, double wrap)
    {
        var t = _theme;
        var formatted = Layout(text, kind, size, wrap, lineLimit: true, out var padX, out var padY);
        IBrush? fill = kind switch { LabelKind.Bubble => t.Bubble, LabelKind.Name => t.Tag, _ => null };
        IPen? pen = kind == LabelKind.Bubble ? t.BubblePen : null;
        // Whole device pixels: the label is blitted 1:1, so its drawn size is exactly the
        // bitmap's pixels at this density (no resampling, no sliver lost to rounding).
        var px = new PixelSize(Math.Max(1, (int)Math.Ceiling((formatted.WidthIncludingTrailingWhitespace + padX * 2) * Scaling)),
            Math.Max(1, (int)Math.Ceiling((formatted.Height + padY * 2) * Scaling)));
        var w = px.Width / Scaling;
        var h = px.Height / Scaling;
        var bitmap = new RenderTargetBitmap(px, new Vector(96 * Scaling, 96 * Scaling));
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
        return new Label(bitmap, w, h, size, Scaling, wrap);
    }

    /// <summary>What a bubble for <paramref name="text"/> looks like at the current size and density (tests).</summary>
    public BubbleMeasure MeasureBubble(string text)
    {
        var size = FontSize(LabelKind.Bubble);
        var wrap = WrapWidth(LabelKind.Bubble, size);
        var shown = Layout(text, LabelKind.Bubble, size, wrap, lineLimit: true, out var padX, out var padY);
        var free = Layout(text, LabelKind.Bubble, size, wrap, lineLimit: false, out _, out _);
        var one = Layout("Xg", LabelKind.Bubble, size, wrap, lineLimit: false, out _, out _);
        var label = LabelFor(text, LabelKind.Bubble, force: true)!;
        return new BubbleMeasure(size, wrap, (int)Math.Round(free.Height / one.Height), padX, padY,
            new Rect(padX, padY, shown.WidthIncludingTrailingWhitespace, shown.Height), free.Height,
            label.Width, label.Height, label.Bitmap.PixelSize, label.Bitmap);
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

    /// <summary>
    /// The whole of <paramref name="bitmap"/> as a DrawImage source rect. Avalonia 12 (Skia)
    /// reads the source rect in the bitmap's PIXELS, not in its DIP <see cref="Bitmap.Size"/>:
    /// passing <c>new Rect(bitmap.Size)</c> for a bitmap rendered at 1.25x drew only its top-left
    /// 80%, stretched over the whole destination, so every bubble lost its right end mid-word
    /// (at 2x it lost half). AlbumTownPluginTests renders it at each density to keep it so.
    /// </summary>
    public static Rect PixelSource(Bitmap bitmap) => new(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

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
        _hand?.Dispose();
        _foot?.Dispose();
        _eyes?.Dispose();
        _hand = _foot = _eyes = null;
        _limbsFor = default;
    }

    /// <summary>A bubble's layout (tests): font, wrap width, lines the text needs, the text box
    /// inside the label, and the label's size in px and device pixels.</summary>
    public sealed record BubbleMeasure(double FontSize, double Wrap, int Lines, double PadX, double PadY, Rect Text, double FullTextHeight,
        double Width, double Height, PixelSize Pixels, RenderTargetBitmap Bitmap);

    private sealed class Label
    {
        public Label(RenderTargetBitmap bitmap, double width, double height, double fontSize, double scaling, double wrap)
        {
            Wrap = wrap;
            Bitmap = bitmap;
            Width = width;
            Height = height;
            FontSize = fontSize;
            Scaling = scaling;
            Source = PixelSource(bitmap);
        }

        public RenderTargetBitmap Bitmap { get; }
        public double Width { get; }
        public double Height { get; }
        public double FontSize { get; }
        public double Scaling { get; }
        public double Wrap { get; }
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
            Source = PixelSource(bitmap);
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
