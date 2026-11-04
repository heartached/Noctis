using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// A cover's colour, the way Noctis picks it for its album pages (DominantColorExtractor's
/// centre-weighted average that skips near-black and near-white pixels), ported here because a
/// plugin cannot call into the app. Then made room-worthy: saturated enough to read as a colour,
/// at a lightness that works as a frame on the dark and on the light palette.
/// </summary>
public static class CoverColor
{
    /// <summary>The cover's dominant colour, or null when it has no usable pixels.</summary>
    public static Color? Dominant(Bitmap? bitmap)
    {
        if (bitmap is null) return null;
        var size = bitmap.PixelSize;
        if (size.Width < 1 || size.Height < 1) return null;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
        var rgba = bitmap.Format is { } f && f == PixelFormat.Rgba8888;
        return FromPixels(pixels, size.Width, size.Height, stride, rgba);
    }

    /// <summary>Noctis's DominantFromPixels: centre-weighted average of the pixels that are
    /// neither near-black nor near-white. <paramref name="rgba"/> false = BGRA.</summary>
    public static Color? FromPixels(byte[] pixels, int width, int height, int rowBytes, bool rgba)
    {
        const int brightnessMin = 15;
        const int brightnessMax = 240;
        double totalR = 0, totalG = 0, totalB = 0, totalWeight = 0;
        var cx = width / 2.0;
        var cy = height / 2.0;
        var maxDist = Math.Sqrt(cx * cx + cy * cy);
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var o = row + x * 4;
                if (pixels[o + 3] < 128) continue;
                var r = rgba ? pixels[o] : pixels[o + 2];
                var g = pixels[o + 1];
                var b = rgba ? pixels[o + 2] : pixels[o];
                var brightness = (r * 299 + g * 587 + b * 114) / 1000;
                if (brightness < brightnessMin || brightness > brightnessMax) continue;
                var dx = x - cx;
                var dy = y - cy;
                var weight = 1.0 + (1.0 - Math.Sqrt(dx * dx + dy * dy) / maxDist);
                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                totalWeight += weight;
            }
        }
        if (totalWeight < 1.0) return null;
        return Color.FromRgb((byte)(totalR / totalWeight), (byte)(totalG / totalWeight), (byte)(totalB / totalWeight));
    }

    /// <summary>The room's frame colour for cover colour <paramref name="c"/>: the same hue, at
    /// least a little saturated, at a lightness that stands out on the palette.</summary>
    public static Color Frame(Color c, bool dark)
    {
        var (h, s, l) = ToHsl(c);
        s = s < 0.08 ? s : Math.Clamp(s * 1.15, 0.35, 0.85);
        l = dark ? Math.Clamp(l, 0.48, 0.66) : Math.Clamp(l, 0.40, 0.56);
        return FromHsl(h, s, l);
    }

    /// <summary>"#RRGGBB" (the playlist's own colour), or null.</summary>
    public static Color? Parse(string? hex)
        => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var c) ? c : null;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        if (s < 1e-6) { var v = (byte)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        return Color.FromRgb((byte)Math.Round(Hue(p2, q2, h + 1.0 / 3) * 255), (byte)Math.Round(Hue(p2, q2, h) * 255), (byte)Math.Round(Hue(p2, q2, h - 1.0 / 3) * 255));
    }
}

/// <summary>What a room shows: its name, a second line, its cover (or up to four for a mosaic)
/// and a colour to fall back on before (or without) a cover.</summary>
public sealed record RoomLook(string Title, string Subtitle, IReadOnlyList<string> Covers, Color? Fallback);

/// <summary>Where a room is being resized from: its right edge (wider), bottom edge (taller) or corner (both).</summary>
public enum RoomEdge { None, Right, Bottom, Corner }

/// <summary>A room's name lines (<see cref="RoomPainter.Text"/>), room px. A line that does not
/// fit scrolls: the canvas draws it every frame, the room's bitmap leaves it out.</summary>
public sealed record RoomText(FormattedText Title, FormattedText? Sub, Point TitleAt, Point SubAt, double Width, bool TitleScrolls, bool SubScrolls)
{
    public bool Scrolls => TitleScrolls || SubScrolls;
}

/// <summary>A doorway or ladder to draw inside a room, in the room's own px.</summary>
public readonly record struct RoomOpening(PortalKind Kind, double X, double Y, double Length);

/// <summary>
/// Draws one room into a bitmap, once: the tinted wall, the cover as a soft dimmed backdrop (a
/// tiny thumbnail drawn large is the blur), the floors, a divided room's interior walls, the
/// doorways and ladders, the coloured rounded frame of Jafezy's sketch, the name, the "..."
/// button and the corner grip. Frames then blit it 1:1. Nothing here runs per frame.
/// </summary>
public static class RoomPainter
{
    private static readonly Typeface TitleFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SubFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    /// <summary>Height of the name band at the top of a room (px); pets never stand in it.</summary>
    public static double HeaderHeight(double cell) => Math.Clamp(cell * 0.3, 26, 40);

    /// <summary>The "..." button's centre and radius, relative to the room.</summary>
    public static (Point Center, double Radius) MenuButton(Size room, double cell)
    {
        var r = Math.Clamp(cell * 0.09, 9, 13);
        var inset = FrameWidth(cell) + 4 + r;
        return (new Point(room.Width - inset, inset), r);
    }

    /// <summary>The corner grip (drag it to resize), relative to the room.</summary>
    public static Rect Grip(Size room, double cell)
    {
        var s = Math.Clamp(cell * 0.16, 14, 22);
        return new Rect(room.Width - s, room.Height - s, s, s);
    }

    /// <summary>A resize handle on the room's right edge, bottom edge or corner (room px): a small
    /// bump straddling the frame.</summary>
    public static Rect Handle(Size room, double cell, RoomEdge edge)
    {
        var t = Math.Clamp(cell * 0.07, 6, 9);
        var len = Math.Clamp(cell * 0.34, 20, 40);
        return edge switch
        {
            RoomEdge.Right => new Rect(room.Width - t * 0.75, (room.Height - len) / 2, t, len),
            RoomEdge.Bottom => new Rect((room.Width - len) / 2, room.Height - t * 0.75, len, t),
            RoomEdge.Corner => new Rect(room.Width - t * 1.4, room.Height - t * 1.4, t * 1.6, t * 1.6),
            _ => default,
        };
    }

    public static double FrameWidth(double cell) => Math.Clamp(cell * 0.04, 3, 6);

    public static double Corner(double cell) => Math.Clamp(cell * 0.1, 8, 16);

    /// <param name="size">The room's drawn size in px.</param>
    /// <param name="areas">The room's areas, in the room's px.</param>
    /// <param name="openings">Doorways (on walls) and ladders (in floors), in the room's px.</param>
    /// <param name="covers">Decoded backdrop covers: one, or up to four for a mosaic.</param>
    /// <param name="text">The name lines (<see cref="Text"/>), null for none (a one-cell room).</param>
    public static RenderTargetBitmap Build(Size size, double scaling, double cell, TownTheme theme, Color frame, RoomLook look,
        IReadOnlyList<Bitmap> covers, IReadOnlyList<Rect> areas, IReadOnlyList<RoomOpening> openings, RoomText? text = null)
    {
        var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scaling)), Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
        var w = px.Width / scaling;
        var h = px.Height / scaling;
        var bitmap = new RenderTargetBitmap(px, new Vector(96 * scaling, 96 * scaling));
        var dark = theme.IsDark;
        var rect = new Rect(0, 0, w, h);
        var fw = FrameWidth(cell);
        var radius = Corner(cell);
        var wall = dark ? TownTheme.Mix(Color.FromRgb(0x22, 0x22, 0x26), frame, 0.20) : TownTheme.Mix(Colors.White, frame, 0.14);
        var shade = dark ? TownTheme.Mix(wall, Colors.Black, 0.5) : TownTheme.Mix(wall, Colors.Black, 0.28);
        var inner = new RoundedRect(rect.Deflate(fw / 2), Math.Max(0, radius - fw / 2));

        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(wall), null, inner);
            using (ctx.PushClip(inner))
            {
                // The cover, soft and dimmed: a ~48 px thumbnail stretched over the wall is the blur.
                if (covers.Count > 0)
                {
                    using (ctx.PushOpacity(dark ? 0.5 : 0.42))
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    {
                        if (covers.Count == 1) DrawFill(ctx, covers[0], rect);
                        else
                            for (var i = 0; i < 4; i++)
                                DrawFill(ctx, covers[i % covers.Count], new Rect(i % 2 * w / 2, i / 2 * h / 2, w / 2, h / 2));
                    }
                    ctx.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(dark ? (byte)0x8C : (byte)0x8C, wall.R, wall.G, wall.B)), rect);
                }
                // A soft band of light at the top (where the name sits) and the floors.
                ctx.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(dark ? (byte)0x50 : (byte)0x70, wall.R, wall.G, wall.B), 0),
                        new GradientStop(Color.FromArgb(0, wall.R, wall.G, wall.B), 1),
                    },
                }, new Rect(0, 0, w, HeaderHeight(cell) * 1.6));
                var floor = new ImmutableSolidColorBrush(Color.FromArgb(dark ? (byte)0x80 : (byte)0x55, shade.R, shade.G, shade.B));
                var floorLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x90, frame.R, frame.G, frame.B)), 1.5);
                foreach (var a in areas)
                {
                    var band = Math.Min(a.Height * 0.16, cell * 0.13);
                    ctx.FillRectangle(floor, new Rect(a.X, a.Bottom - band, a.Width, band));
                    ctx.DrawLine(floorLine, new Point(a.X, a.Bottom - band), new Point(a.Right, a.Bottom - band));
                }

                // Interior walls of a divided playlist apartment.
                if (areas.Count > 1)
                {
                    var wallPen = new ImmutablePen(new ImmutableSolidColorBrush(frame), Math.Max(2, fw * 0.75), lineCap: PenLineCap.Square);
                    foreach (var a in areas)
                    {
                        if (a.Right < w - fw - 1) ctx.DrawLine(wallPen, new Point(a.Right, a.Y), new Point(a.Right, a.Bottom));
                        if (a.Bottom < h - fw - 1) ctx.DrawLine(wallPen, new Point(a.X, a.Bottom), new Point(a.Right, a.Bottom));
                    }
                }

                // Doorways (walls) and ladders (floors).
                var doorBrush = new ImmutableSolidColorBrush(shade);
                var doorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, frame.R, frame.G, frame.B)), 1.5);
                var rungPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xC0, frame.R, frame.G, frame.B)), 1.6, lineCap: PenLineCap.Round);
                foreach (var o in openings)
                {
                    if (o.Kind == PortalKind.Door)
                    {
                        var dw = Math.Max(fw * 2.4, cell * 0.11);
                        var dh = o.Length;
                        var left = Math.Clamp(o.X - dw / 2, 0, w - dw);
                        var door = new Rect(left, o.Y - dh, dw, dh);
                        var top = new RoundedRect(door, dw / 2, dw / 2, 1, 1);
                        ctx.DrawRectangle(doorBrush, doorPen, top);
                    }
                    else if (o.Kind == PortalKind.Stairs)
                    {
                        // A ladder up the wall to the doorway onto the higher floor next door.
                        var lw = Math.Max(10, cell * 0.1);
                        var bottom = o.Y + o.Length;
                        ctx.DrawLine(rungPen, new Point(o.X - lw / 2, o.Y), new Point(o.X - lw / 2, bottom));
                        ctx.DrawLine(rungPen, new Point(o.X + lw / 2, o.Y), new Point(o.X + lw / 2, bottom));
                        for (var y = o.Y + 5; y < bottom - 2; y += 8)
                            ctx.DrawLine(rungPen, new Point(o.X - lw / 2, y), new Point(o.X + lw / 2, y));
                    }
                    else if (o.Kind == PortalKind.TrapDoor)
                    {
                        // The hatch down to the room below.
                        var lw = Math.Max(10, cell * 0.1) + 8;
                        ctx.DrawRectangle(doorBrush, doorPen, new RoundedRect(new Rect(o.X - lw / 2, o.Y + 1, lw, Math.Max(3, o.Length - 2)), 2));
                    }
                    else
                    {
                        // A ladder up to the opening in the floor (or ceiling) above.
                        var lw = Math.Max(10, cell * 0.1);
                        var gap = new Rect(o.X - lw / 2 - 3, o.Y - fw, lw + 6, fw * 2);
                        ctx.FillRectangle(doorBrush, gap);
                        var bottom = o.Y + o.Length;
                        ctx.DrawLine(rungPen, new Point(o.X - lw / 2, o.Y - fw), new Point(o.X - lw / 2, bottom));
                        ctx.DrawLine(rungPen, new Point(o.X + lw / 2, o.Y - fw), new Point(o.X + lw / 2, bottom));
                        for (var y = o.Y + 6; y < bottom - 2; y += 8)
                            ctx.DrawLine(rungPen, new Point(o.X - lw / 2, y), new Point(o.X + lw / 2, y));
                    }
                }
            }

            // The frame: Jafezy's coloured border, flat and rounded.
            ctx.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(frame), fw), inner);

            // Name and second line: drawn here when they fit, by the canvas (scrolling) when not.
            if (text is not null)
            {
                if (!text.TitleScrolls) ctx.DrawText(text.Title, text.TitleAt);
                if (text.Sub is { } subLine && !text.SubScrolls) ctx.DrawText(subLine, text.SubAt);
            }

            // "..." (remove, play, choose songs) and the corner grip.
            var (menu, menuR) = MenuButton(rect.Size, cell);
            ctx.DrawEllipse(theme.Chip, null, menu, menuR, menuR);
            var dot = menuR * 0.16;
            for (var i = -1; i <= 1; i++) ctx.DrawEllipse(theme.Text, null, new Point(menu.X + i * menuR * 0.42, menu.Y), dot, dot);
            var grip = Grip(rect.Size, cell);
            var gripPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xD0, frame.R, frame.G, frame.B)), 1.6, lineCap: PenLineCap.Round);
            for (var i = 1; i <= 3; i++)
            {
                var k = grip.Width * (0.22 * i);
                ctx.DrawLine(gripPen, new Point(grip.Right - fw - 2 - k, grip.Bottom - fw - 2), new Point(grip.Right - fw - 2, grip.Bottom - fw - 2 - k));
            }
        }
        return bitmap;
    }

    /// <summary>Smallest name size a long name shrinks to before it scrolls instead.</summary>
    public const double MinTitleSize = 10;

    /// <summary>
    /// The room's name and second line, laid out in the name band (room px). Each line keeps
    /// its size when it fits, shrinks a little when it almost does, and otherwise scrolls
    /// (<see cref="RoomText.TitleScrolls"/>): long names ran out of the room or were cut
    /// (Discord, Jafezy 11-02). Null for a one-cell room: too small for words.
    /// </summary>
    public static RoomText? Text(RoomLook look, Size size, double cell, TownTheme theme, bool oneCell)
    {
        if (oneCell) return null;
        var fw = FrameWidth(cell);
        var head = HeaderHeight(cell);
        var (menu, menuR) = MenuButton(size, cell);
        var textLeft = fw + 8;
        var width = Math.Max(10, menu.X - menuR - 6 - textLeft);
        var baseSize = Math.Clamp(cell * 0.1, 11, 14);
        var (title, titleScrolls) = Fit(look.Title.Length == 0 ? "Untitled" : look.Title, TitleFace, baseSize, width, theme.Text);
        var showSub = look.Subtitle.Length > 0 && size.Height > cell * 0.8;
        var (sub, subScrolls) = showSub ? Fit(look.Subtitle, SubFace, Math.Max(9.5, baseSize - 2), width, theme.Muted) : (null, false);
        var both = title.Height + (sub?.Height ?? 0);
        var ty = fw + Math.Max(3, (head - both) / 2);
        return new RoomText(title, sub, new Point(textLeft, ty), new Point(textLeft, ty + title.Height), width, titleScrolls, subScrolls);
    }

    private static (FormattedText Text, bool Scrolls) Fit(string text, Typeface face, double size, double width, IBrush brush)
    {
        var full = Line(text, face, size, brush);
        if (full.WidthIncludingTrailingWhitespace <= width) return (full, false);
        // Up to two px smaller when that makes it fit; else it scrolls at its own size.
        for (var s = size - 1; s >= Math.Max(MinTitleSize - 0.5, size - 2); s -= 1)
        {
            var smaller = Line(text, face, s, brush);
            if (smaller.WidthIncludingTrailingWhitespace <= width) return (smaller, false);
        }
        return (full, true);
    }

    private static FormattedText Line(string text, Typeface face, double size, IBrush brush)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush) { MaxLineCount = 1 };

    /// <summary>Gap between the end of a scrolling name and its start coming round again (px).</summary>
    public const double MarqueeGap = 36;
    /// <summary>Scroll speed (px per second) and the pause at the start of each round (seconds).</summary>
    public const double MarqueeSpeed = 26;
    public const double MarqueePause = 1.8;

    /// <summary>How far a scrolling line of <paramref name="textWidth"/> px has moved left after
    /// <paramref name="seconds"/>: it rests at the start, scrolls until its start comes round
    /// again (after the gap), rests, and so on.</summary>
    public static double MarqueeOffset(double seconds, double textWidth)
    {
        var distance = textWidth + MarqueeGap;
        var period = MarqueePause + distance / MarqueeSpeed;
        var t = seconds % period;
        return t < MarqueePause ? 0 : Math.Min(distance, (t - MarqueePause) * MarqueeSpeed);
    }

    /// <summary>Draws <paramref name="line"/> scrolled, clipped to <paramref name="width"/>, at
    /// <paramref name="at"/> (px). Two copies so the start follows the end round.</summary>
    public static void DrawMarquee(DrawingContext ctx, FormattedText line, Point at, double width, double seconds)
    {
        var w = line.WidthIncludingTrailingWhitespace;
        var off = MarqueeOffset(seconds, w);
        using (ctx.PushClip(new Rect(at.X, at.Y, width, line.Height)))
        {
            ctx.DrawText(line, new Point(at.X - off, at.Y));
            if (off > 0) ctx.DrawText(line, new Point(at.X - off + w + MarqueeGap, at.Y));
        }
    }

    private static void DrawFill(DrawingContext ctx, Bitmap image, Rect into)
    {
        // Uniform to fill: the middle of the cover, cropped to the target's shape.
        var src = image.Size;
        var scale = Math.Max(into.Width / src.Width, into.Height / src.Height);
        var sw = into.Width / scale;
        var sh = into.Height / scale;
        var px = image.PixelSize.Width / src.Width;
        var source = new Rect((src.Width - sw) / 2 * px, (src.Height - sh) / 2 * px, sw * px, sh * px);
        using (ctx.PushClip(into))
            ctx.DrawImage(image, source, into);
    }
}
