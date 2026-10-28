using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// Draws one creature, 2.5D: a rounded cube whose front face is the album cover, a lighter lid and
/// a darker side in the cover's own tones, simple eyes over the cover, floating white glove nubs
/// for hands and little shoes for feet (Rayman style: no arms or legs, the limbs hover), and a
/// soft shadow. Everything is a handful of fills, so 60 of them stay cheap.
/// </summary>
public sealed class CreaturePainter
{
    public static readonly Color Accent = Color.FromRgb(0xFF, 0x5C, 0x8A);

    private static readonly IBrush ShadowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0x20, 0x18, 0x30));
    private static readonly IBrush GloveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFA, 0xF8, 0xFC));
    private static readonly IBrush ShoeBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x3B, 0x33, 0x47));
    private static readonly IBrush EyeWhite = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush Pupil = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1A, 0x26));
    private static readonly IBrush BubbleBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly IBrush BubbleText = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x33));
    private static readonly IBrush TagBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x24, 0x1F, 0x2C));
    private static readonly IBrush TagText = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush NoteBrush = new ImmutableSolidColorBrush(Accent);
    private static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x8C, 0x1E, 0x18, 0x28)), 1.2, lineJoin: PenLineJoin.Round);
    private static readonly IPen EyeLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1A, 0x26)), 1.4, lineCap: PenLineCap.Round);
    private static readonly IPen BubblePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x20, 0x18, 0x30)), 1);
    private static readonly IBrush Gloss = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45) },
    }.ToImmutable();
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly CoverCache _covers;
    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<Color, IBrush> _brushes = new();

    public CreaturePainter(CoverCache covers) => _covers = covers;

    /// <summary>Front face size in px.</summary>
    public double Size { get; set; } = 30;

    public void DrawShadow(DrawingContext ctx, Creature c)
    {
        var s = Size;
        var lift = Bob(c);
        // The shadow shrinks a little as the body bobs up.
        var k = 1 - lift / (s * 2);
        ctx.DrawEllipse(ShadowBrush, null, new Point(c.Position.X, c.Position.Y), s * 0.62 * k, s * 0.17 * k);
    }

    public void DrawGlow(DrawingContext ctx, Creature c)
    {
        if (!c.IsPlaying) return;
        var s = Size;
        var strength = 0.35 + 0.45 * c.Pulse;
        var center = new Point(c.Position.X, c.Position.Y - s * 0.75);
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(200 * strength), Accent.R, Accent.G, Accent.B), 0),
                new GradientStop(Color.FromArgb(0, Accent.R, Accent.G, Accent.B), 1),
            },
        };
        var r = s * (1.25 + 0.25 * c.Pulse);
        ctx.DrawEllipse(glow, null, center, r, r * 0.95);
    }

    public void DrawBody(DrawingContext ctx, Creature c)
    {
        var s = Size;
        var x = (double)c.Position.X;
        var ground = (double)c.Position.Y;
        var phase = c.WalkPhase;
        var stride = c.Stride;

        // Feet: two shoes hovering under the body, stepping with the walk.
        var swing = Math.Sin(phase) * s * 0.13 * stride;
        var liftL = Math.Max(0, Math.Sin(phase)) * s * 0.1 * stride;
        var liftR = Math.Max(0, -Math.Sin(phase)) * s * 0.1 * stride;
        if (c.State == CreatureState.Dance)
        {
            liftL = Math.Max(0, Math.Sin(phase)) * s * 0.12;
            liftR = Math.Max(0, -Math.Sin(phase)) * s * 0.12;
        }
        DrawNub(ctx, ShoeBrush, x - s * 0.24 + swing, ground - s * 0.07 - liftL, s * 0.17, s * 0.1);
        DrawNub(ctx, ShoeBrush, x + s * 0.24 - swing, ground - s * 0.07 - liftR, s * 0.17, s * 0.1);

        // Body: front face (the cover), lid and side. Squash a bit on the beat.
        var squash = c.State == CreatureState.Dance ? 1 - 0.08 * c.Pulse : 1;
        var w = s * (2 - squash);
        var h = s * squash;
        var bottom = ground - s * 0.3 - Bob(c);
        var left = x - w / 2;
        var top = bottom - h;
        var d = s * 0.24;
        var dy = d * 0.75;
        var side = c.Facing >= 0 ? 1 : -1; // the side face shows on the side it walks toward
        var cover = _covers.Get(c.Album.ArtworkPath);
        var topColor = cover?.Top ?? CoverCache.Pastel(c.Album.Key, 0.84f);
        var sideColor = cover?.Side ?? CoverCache.Pastel(c.Album.Key, 0.5f);
        var front = new Rect(left, top, w, h);

        var lidGeo = new StreamGeometry();
        using (var g = lidGeo.Open())
        {
            g.BeginFigure(new Point(left, top + 2), true);
            g.LineTo(new Point(left + side * d, top - dy));
            g.LineTo(new Point(left + w + side * d, top - dy));
            g.LineTo(new Point(left + w, top + 2));
            g.EndFigure(true);
        }
        var sideGeo = new StreamGeometry();
        using (var g = sideGeo.Open())
        {
            var edge = side > 0 ? left + w : left;
            g.BeginFigure(new Point(edge, top + 2), true);
            g.LineTo(new Point(edge + side * d, top - dy));
            g.LineTo(new Point(edge + side * d, bottom - dy - 2));
            g.LineTo(new Point(edge, bottom - 2));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brush(sideColor), Outline, sideGeo);
        ctx.DrawGeometry(Brush(topColor), Outline, lidGeo);

        var radius = s * 0.16;
        var rounded = new RoundedRect(front, radius);
        if (cover?.Image is { } image)
        {
            using (ctx.PushClip(rounded))
                ctx.DrawImage(image, new Rect(image.Size), front);
        }
        else
        {
            ctx.DrawRectangle(Brush(CoverCache.Pastel(c.Album.Key, 0.68f)), null, rounded);
            DrawText(ctx, "♪", new Point(x, top + h * 0.72), s * 0.42, BubbleText, center: true);
        }
        ctx.DrawRectangle(Gloss, Outline, rounded);

        // Eyes over the cover, looking where the creature looks.
        var eyeY = top + h * 0.38;
        var eyeDx = s * 0.19;
        var gx = Math.Clamp(c.Gaze.X, -1, 1) * s * 0.05;
        var gy = Math.Clamp(c.Gaze.Y, -1, 1) * s * 0.04;
        var blink = c.BlinkTimer < 0;
        foreach (var ex in new[] { x - eyeDx, x + eyeDx })
        {
            if (blink)
            {
                ctx.DrawLine(EyeLine, new Point(ex - s * 0.1, eyeY), new Point(ex + s * 0.1, eyeY));
                continue;
            }
            ctx.DrawEllipse(EyeWhite, EyeLine, new Point(ex, eyeY), s * 0.12, s * 0.15);
            ctx.DrawEllipse(Pupil, null, new Point(ex + gx, eyeY + gy + s * 0.02), s * 0.06, s * 0.075);
        }

        // Hands: white glove nubs floating beside the body.
        var handY = bottom - h * 0.42;
        var armSwing = Math.Sin(phase) * s * 0.09 * stride;
        var lx = left - s * 0.17;
        var rx = left + w + s * 0.17 + (side > 0 ? d * 0.4 : 0);
        if (side < 0) lx -= d * 0.4;
        var ly = handY + armSwing;
        var ry = handY - armSwing;
        if (c.State == CreatureState.Wave)
        {
            // One hand up, waving.
            var waveX = Math.Sin(phase) * s * 0.12;
            if (side > 0) { rx += waveX; ry = top - s * 0.12; }
            else { lx += waveX; ly = top - s * 0.12; }
        }
        else if (c.State == CreatureState.Dance)
        {
            ly = top + h * 0.1 - Math.Abs(Math.Sin(phase)) * s * 0.25;
            ry = top + h * 0.1 - Math.Abs(Math.Cos(phase)) * s * 0.25;
        }
        else if (c.State == CreatureState.Talk && c.Bubble is not null)
        {
            ry -= Math.Abs(Math.Sin(c.Age * 7)) * s * 0.08;
        }
        DrawNub(ctx, GloveBrush, lx, ly, s * 0.12, s * 0.12);
        DrawNub(ctx, GloveBrush, rx, ry, s * 0.12, s * 0.12);

        if (c.IsPlaying) DrawNotes(ctx, c, x, top - dy);
    }

    /// <summary>Name tag under the creature.</summary>
    public void DrawName(DrawingContext ctx, Creature c)
    {
        var text = Text(c.Album.Title.Length > 26 ? c.Album.Title[..25].TrimEnd() + "..." : c.Album.Title, 10.5, TagText, 160);
        var w = text.Width + 12;
        var h = text.Height + 4;
        var rect = new Rect(c.Position.X - w / 2, c.Position.Y + 6, w, h);
        ctx.DrawRectangle(TagBrush, null, new RoundedRect(rect, h / 2));
        ctx.DrawText(text, new Point(rect.X + 6, rect.Y + 2));
    }

    /// <summary>Speech bubble above the head.</summary>
    public void DrawBubble(DrawingContext ctx, Creature c, Rect bounds)
    {
        if (c.Bubble is not { } line) return;
        var s = Size;
        var text = Text(line, 11.5, BubbleText, 170);
        var w = text.Width + 18;
        var h = text.Height + 12;
        var tipX = c.Position.X;
        var tipY = c.Position.Y - s * 1.55 - Bob(c);
        var x = Math.Clamp(tipX - w / 2, bounds.X + 4, bounds.Right - w - 4);
        var y = Math.Max(bounds.Y + 4, tipY - h - 7);
        var rect = new Rect(x, y, w, h);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            var tx = Math.Clamp(tipX, rect.X + 12, rect.Right - 12);
            g.BeginFigure(new Point(tx - 6, rect.Bottom - 1), true);
            g.LineTo(new Point(tx, rect.Bottom + 7));
            g.LineTo(new Point(tx + 6, rect.Bottom - 1));
            g.EndFigure(true);
        }
        ctx.DrawRectangle(BubbleBrush, BubblePen, new RoundedRect(rect, 11));
        ctx.DrawGeometry(BubbleBrush, null, tail);
        ctx.DrawText(text, new Point(rect.X + 9, rect.Y + 6));
    }

    private void DrawNotes(DrawingContext ctx, Creature c, double x, double top)
    {
        for (var i = 0; i < 2; i++)
        {
            var t = (c.Age * 0.6 + i * 0.5) % 1.0;
            var nx = x + (i == 0 ? -1 : 1) * Size * (0.35 + 0.2 * t);
            var ny = top - Size * 0.2 - t * Size * 0.9;
            using (ctx.PushOpacity(1 - t))
                DrawText(ctx, i == 0 ? "♪" : "♫", new Point(nx, ny), Size * 0.42, NoteBrush, center: true);
        }
    }

    private static void DrawNub(DrawingContext ctx, IBrush brush, double cx, double cy, double rx, double ry)
        => ctx.DrawEllipse(brush, Outline, new Point(cx, cy), rx, ry);

    private void DrawText(DrawingContext ctx, string s, Point at, double size, IBrush brush, bool center)
    {
        var t = Text(s, size, brush, 0);
        ctx.DrawText(t, center ? new Point(at.X - t.Width / 2, at.Y - t.Height / 2) : at);
    }

    /// <summary>Formatted text, built once per string, size and colour.</summary>
    private FormattedText Text(string s, double size, IBrush brush, double maxWidth)
    {
        var key = string.Concat(s, "|", size.ToString("0.#", CultureInfo.InvariantCulture), "|", brush.GetHashCode().ToString(CultureInfo.InvariantCulture));
        if (_texts.TryGetValue(key, out var t)) return t;
        if (_texts.Count > 600) _texts.Clear();
        t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush);
        if (maxWidth > 0) t.MaxTextWidth = maxWidth;
        _texts[key] = t;
        return t;
    }

    private IBrush Brush(Color color)
    {
        if (_brushes.TryGetValue(color, out var b)) return b;
        if (_brushes.Count > 1000) _brushes.Clear();
        return _brushes[color] = new ImmutableSolidColorBrush(color);
    }

    /// <summary>How far the body floats up this frame (walk bob or the dance bounce).</summary>
    private double Bob(Creature c)
    {
        var s = Size;
        return c.State == CreatureState.Dance
            ? Math.Abs(Math.Sin(c.WalkPhase)) * s * 0.12 + c.Pulse * s * 0.1
            : Math.Abs(Math.Sin(c.WalkPhase)) * s * 0.06 * c.Stride;
    }
}
