using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// The physical releases drawn in code, shared by the album carousel's cases
/// (<see cref="AlbumCarouselCard"/>) and the lyrics page's now-playing artwork
/// (<see cref="MediaArtwork"/>), so a record or a cassette looks the same in both: a CD
/// jewel case with its hinge spine, a record sleeve with the disc, a cassette shell.
///
/// Sizes are in multiples of the cover edge <c>s</c> (the square art); every method draws in
/// real pixels, so 1px lines stay 1px at any size. The cover art itself is never drawn here
/// (except the record label): callers put their own image stack in the slots these rects
/// describe, between the layers drawn behind and in front of it.
///
/// The record and the reels are split from the light that falls on them so a spinning disc
/// can turn under a fixed lamp (<see cref="MediaCaseLayer"/> draws one part per control).
/// </summary>
internal static class MediaCaseArt
{
    // Jewel case: 142 × 125 mm with a 120 mm insert → the cover fills the lid right of the spine.
    internal const double JewelWidth = 1.17, JewelHeight = 1.04, JewelSpine = 0.12;
    // Vinyl: the disc's centre sits this far right of the sleeve's centre, far enough for the
    // edge of its label to show past the sleeve.
    internal const double VinylDiscRadius = 0.47, VinylDiscOffset = 0.42;
    /// <summary>The record label's radius as a fraction of the disc's (a 12-inch label is about a third).</summary>
    internal const double VinylLabelRadius = 0.34;
    internal const double VinylWidth = 0.5 + VinylDiscOffset + VinylDiscRadius;
    // Cassette: shell ≈ 100 × 64 mm.
    internal const double CassetteWidth = 1.3, CassetteHeight = 0.83;

    /// <summary>The disc's centre, sleeve at (0, 0, s, s).</summary>
    internal static Point VinylDiscCentre(double s) => new(s * (0.5 + VinylDiscOffset), s * 0.5);

    /// <summary>The paper label (where the cover art goes), shell at (0, 0).</summary>
    internal static Rect CassetteLabelRect(double s)
    {
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        return new Rect(w * 0.07, h * 0.08, w * 0.86, h * 0.56);
    }

    /// <summary>The tape window over the lower middle of the label, shell at (0, 0).</summary>
    internal static Rect CassetteWindowRect(double s)
    {
        var label = CassetteLabelRect(s);
        return new Rect(CassetteWidth * s * 0.29, label.Y + label.Height * 0.52, CassetteWidth * s * 0.42, CassetteHeight * s * 0.18);
    }

    /// <summary>The two reel hubs' centres, shell at (0, 0).</summary>
    internal static (Point Left, Point Right) CassetteReelCentres(double s)
    {
        var win = CassetteWindowRect(s);
        var cy = win.Center.Y;
        return (new Point(win.X + win.Width * 0.22, cy), new Point(win.Right - win.Width * 0.22, cy));
    }

    internal static double CassetteHubRadius(double s) => CassetteWindowRect(s).Height * 0.27;

    // Every brush here is relative to what it fills, so one set serves every drawing at any
    // size instead of new brushes per drawing.
    private static LinearGradientBrush Linear(double x0, double y0, double x1, double y1, params (uint Argb, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x0, y0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(x1, y1, RelativeUnit.Relative),
        };
        foreach (var (argb, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.FromUInt32(argb), offset));
        return brush;
    }

    private static RadialGradientBrush Radial(double originX, double originY, params (uint Argb, double Offset)[] stops)
    {
        var brush = new RadialGradientBrush { GradientOrigin = new RelativePoint(originX, originY, RelativeUnit.Relative) };
        foreach (var (argb, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.FromUInt32(argb), offset));
        return brush;
    }

    private static Pen PenOf(uint argb, double thickness = 1) => new(new SolidColorBrush(Color.FromUInt32(argb)), thickness);

    private static readonly IBrush HoleBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D));

    // ── Jewel case ──────────────────────────────────────────────────────────

    // A black tray seen through the clear lid; the hinge spine is clear plastic curving round
    // the hinge, so it is shaded like a cylinder lit from the left.
    private static readonly IBrush TrayBrush = Linear(0, 0, 0, 1, (0xFF1C1C21, 0), (0xFF0C0C0E, 1));
    private static readonly IBrush HingeBrush = Linear(0, 0, 1, 0,
        (0xFF34343B, 0), (0xFF6E6E78, 0.12), (0xFF2E2E35, 0.32), (0xFF1A1A1F, 0.75), (0xFF111114, 0.96), (0xFF2C2C33, 1));
    private static readonly IBrush KnuckleBrush = Linear(0, 0, 1, 0,
        (0xFF4A4A53, 0), (0xFFA4A4AE, 0.35), (0xFF4C4C55, 0.62), (0xFF222228, 1));
    private static readonly IPen KnucklePen = PenOf(0x70000000);
    private static readonly IPen RibShadePen = PenOf(0x70000000);
    private static readonly IPen RibLightPen = PenOf(0x22FFFFFF);

    /// <summary>The tray and hinge spine behind the cover, case at (0, 0, size).</summary>
    internal static void DrawJewelTray(DrawingContext ctx, Size size, double s)
    {
        var body = new Rect(size);
        var r = s * 0.018;
        ctx.DrawRectangle(TrayBrush, null, body, r, r);
        var spineW = JewelSpine * s;
        using (ctx.PushClip(new RoundedRect(body, r)))
        {
            ctx.DrawRectangle(HingeBrush, null, new Rect(0, 0, spineW, size.Height));
            // Grip ribs moulded into the spine: each a dark groove with a lit edge beside it.
            var top = s * 0.11;
            var bottom = size.Height - s * 0.11;
            for (var i = 0; i < 5; i++)
            {
                var x = Math.Round(spineW * (0.34 + i * 0.13)) + 0.5;
                ctx.DrawLine(RibShadePen, new Point(x, top), new Point(x, bottom));
                ctx.DrawLine(RibLightPen, new Point(x + 1, top), new Point(x + 1, bottom));
            }
            // Hinge knuckles at top and bottom: short clear cylinders on the case's left edge.
            var kw = spineW * 0.42;
            var kh = s * 0.075;
            var kx = spineW * 0.08;
            foreach (var ky in new[] { s * 0.018, size.Height - s * 0.018 - kh })
                ctx.DrawRectangle(KnuckleBrush, KnucklePen, new Rect(kx, ky, kw, kh), kw * 0.3, kw * 0.3);
        }
    }

    // Glare on the lid: a broad wash from the top-left corner that ends in a crisp edge, then
    // a thin second streak, as clear plastic reflects a window.
    private static readonly IBrush LidGloss = Linear(0, 0, 1, 1,
        (0x34FFFFFF, 0), (0x14FFFFFF, 0.28), (0x0AFFFFFF, 0.4), (0x00FFFFFF, 0.401),
        (0x00FFFFFF, 0.64), (0x12FFFFFF, 0.68), (0x00FFFFFF, 0.74));
    // Polished lid edge: lit along the top and left, falling off to the bottom right.
    private static readonly IPen LidEdgePen = new Pen(Linear(0, 0, 1, 1, (0xA0FFFFFF, 0), (0x40FFFFFF, 0.5), (0x22FFFFFF, 1)), 1);
    private static readonly IPen LidInnerPen = PenOf(0x70000000);
    private static readonly IPen LidLipPen = new Pen(Linear(0, 0, 1, 0, (0x00FFFFFF, 0), (0x50FFFFFF, 0.3), (0x10FFFFFF, 1)), 1);
    private static readonly IPen SeamPen = PenOf(0x48FFFFFF);
    private static readonly IPen SeamShadePen = PenOf(0x90000000);

    /// <summary>The clear lid over the cover: glare, spine seam, lip and edges.</summary>
    internal static void DrawJewelLid(DrawingContext ctx, Rect rect, double s)
    {
        var r = s * 0.018;
        ctx.DrawRectangle(LidGloss, null, rect, r, r);
        // Where the lid's front meets the hinge: a groove with a lit lip beside it.
        var seam = Math.Round(JewelSpine * s) + 0.5;
        ctx.DrawLine(SeamShadePen, new Point(seam, 1), new Point(seam, rect.Height - 1));
        ctx.DrawLine(SeamPen, new Point(seam + 1, 1), new Point(seam + 1, rect.Height - 1));
        // The front's raised lip catching the light along the top.
        var lipY = Math.Round(s * 0.009) + 0.5;
        ctx.DrawLine(LidLipPen, new Point(seam + s * 0.02, lipY), new Point(rect.Width - s * 0.03, lipY));
        // Edges last: bright outside, a dark line inside for the plastic's thickness.
        ctx.DrawRectangle(null, LidEdgePen, rect.Deflate(0.5), r, r);
        ctx.DrawRectangle(null, LidInnerPen, rect.Deflate(1.5), r, r);
    }

    // ── Record and sleeve ───────────────────────────────────────────────────

    // Black PVC that is darkest in the grooves and catches the light along the rim.
    private static readonly IBrush DiscBrush = Radial(0.5, 0.5,
        (0xFF1E1E21, 0), (0xFF101012, 0.4), (0xFF0E0E10, 0.95), (0xFF222226, 0.985), (0xFF0A0A0B, 1));
    // Light on grooves fans out in wedges from the centre (the cross-shaped sheen of a record).
    private static readonly IBrush DiscSheenBrush = new ConicGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0.03),
            new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.125),
            new GradientStop(Colors.Transparent, 0.22),
            new GradientStop(Colors.Transparent, 0.31),
            new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.375),
            new GradientStop(Colors.Transparent, 0.44),
            new GradientStop(Colors.Transparent, 0.53),
            new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.625),
            new GradientStop(Colors.Transparent, 0.72),
            new GradientStop(Colors.Transparent, 0.81),
            new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.875),
            new GradientStop(Colors.Transparent, 0.94),
        },
    };
    private static readonly IPen GroovePen = PenOf(0x12FFFFFF);
    private static readonly IPen GrooveFaintPen = PenOf(0x08FFFFFF);
    private static readonly IPen TrackGapPen = PenOf(0x90000000, 1.5);
    private static readonly IPen DiscRimPen = PenOf(0x30FFFFFF);
    private static readonly IPen RunOutPen = PenOf(0x18FFFFFF);
    /// <summary>Where the quiet gaps between songs fall, as fractions of the grooved band.</summary>
    private static readonly double[] TrackGaps = { 0.2, 0.41, 0.6, 0.83 };
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xE3, 0xD6));
    // The label's paper edge sits a little in shadow, its centre a little in the light.
    private static readonly IBrush LabelShadeBrush = Radial(0.4, 0.38,
        (0x18FFFFFF, 0), (0x00000000, 0.55), (0x00000000, 0.82), (0x50000000, 1));
    private static readonly IPen LabelRingPen = PenOf(0x50000000);
    private static readonly IPen SpindleLipPen = PenOf(0x30FFFFFF);

    /// <summary>The disc: vinyl, grooves from the lead-in to the run-out (broken by the darker
    /// gaps between songs) and the run-out ring. Round, so it may turn.</summary>
    /// <param name="grooves">False leaves out the grooves (most of a record's shapes), for
    /// the floor reflection where they cannot be seen.</param>
    internal static void DrawRecord(DrawingContext ctx, Point c, double radius, bool grooves = true)
    {
        ctx.DrawEllipse(DiscBrush, null, c, radius, radius);
        var runOut = radius * VinylLabelRadius * 1.2;
        if (grooves)
        {
            var outer = radius * 0.965;
            var step = Math.Max(1.6, radius * 0.019);
            var n = 0;
            for (var g = runOut + step; g < outer; g += step, n++)
                ctx.DrawEllipse(null, n % 2 == 0 ? GroovePen : GrooveFaintPen, c, g, g);
            foreach (var f in TrackGaps)
            {
                var g = runOut + step + (outer - runOut - step) * f;
                ctx.DrawEllipse(null, TrackGapPen, c, g, g);
            }
        }
        ctx.DrawEllipse(null, RunOutPen, c, runOut, runOut);
    }

    /// <summary>The paper label: the cover art printed small, or plain paper without art.</summary>
    internal static void DrawRecordLabel(DrawingContext ctx, Point c, double labelR, IImage? art)
    {
        var label = new Rect(c.X - labelR, c.Y - labelR, labelR * 2, labelR * 2);
        if (art is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            using (ctx.PushGeometryClip(new EllipseGeometry(label)))
                ctx.DrawImage(image, AlbumCarouselCard.UniformToFillSource(image.Size, label.Size), label);
        }
        else
        {
            ctx.DrawEllipse(LabelBrush, null, c, labelR, labelR);
        }
    }

    /// <summary>Over the label: its shaded paper edge and the spindle hole.</summary>
    internal static void DrawRecordLabelFinish(DrawingContext ctx, Point c, double labelR)
    {
        ctx.DrawEllipse(LabelShadeBrush, LabelRingPen, c, labelR, labelR);
        var hole = labelR * 0.075;
        ctx.DrawEllipse(HoleBrush, SpindleLipPen, c, hole, hole);
    }

    /// <summary>The light on the record (sheen wedges and the lit rim): fixed while the disc turns.</summary>
    internal static void DrawRecordLight(DrawingContext ctx, Point c, double radius)
    {
        ctx.DrawEllipse(DiscSheenBrush, DiscRimPen, c, radius - 0.5, radius - 0.5);
    }

    // Printed card with a faint satin sheen, the record's ring pressed through it, and the open
    // edge on the right where its two plies part.
    private static readonly IBrush SleeveSheen = Linear(0, 0, 1, 1, (0x16FFFFFF, 0), (0x00FFFFFF, 0.45), (0x00000000, 0.7), (0x18000000, 1));
    private static readonly IBrush SleeveOpening = Linear(0, 0, 1, 0, (0x00000000, 0), (0x48000000, 0.85), (0x22FFFFFF, 0.86), (0x30000000, 1));
    private static readonly IPen SleeveEdgePen = new Pen(Linear(0, 0, 1, 1, (0x50FFFFFF, 0), (0x18FFFFFF, 0.5), (0x40000000, 1)), 1);
    // Ring wear: a soft worn band where the record inside presses through the card.
    private static readonly IBrush RingWear = Radial(0.5, 0.5, (0x00FFFFFF, 0.9), (0x0EFFFFFF, 0.95), (0x00FFFFFF, 1));

    /// <summary>The sleeve's card finish over the cover art.</summary>
    internal static void DrawSleeveFinish(DrawingContext ctx, Rect sleeve)
    {
        var s = sleeve.Width;
        ctx.DrawRectangle(SleeveSheen, null, sleeve);
        ctx.DrawEllipse(RingWear, null, sleeve.Center, s * 0.46, s * 0.46);
        ctx.DrawRectangle(SleeveOpening, null, new Rect(sleeve.Right - s * 0.028, sleeve.Y, s * 0.028, sleeve.Height));
        ctx.DrawRectangle(null, SleeveEdgePen, sleeve.Deflate(0.5));
    }

    // ── Cassette ────────────────────────────────────────────────────────────

    // Moulded plastic lit from above, a paper label round the art, the raised ridge at the
    // foot with the tape-head openings, five screws.
    private static readonly IBrush ShellBrush = Linear(0, 0, 0, 1, (0xFF3E3E46, 0), (0xFF2B2B31, 0.5), (0xFF1D1D22, 1));
    private static readonly IPen ShellEdgePen = new Pen(Linear(0, 0, 0, 1, (0x70FFFFFF, 0), (0x1CFFFFFF, 0.45), (0x60000000, 1)), 1);
    private static readonly IPen ShellRimPen = new Pen(Linear(0, 0, 0, 1, (0x50000000, 0), (0x14FFFFFF, 1)), 1);
    private static readonly IBrush LabelPaperBrush = Linear(0, 0, 0, 1, (0xFFF1ECE1, 0), (0xFFDCD5C6, 1));
    private static readonly IPen LabelEdgePen = PenOf(0x60000000);
    private static readonly IBrush RidgeBrush = Linear(0, 0, 0, 1, (0xFF33333A, 0), (0xFF1B1B20, 1));
    private static readonly IPen RidgeEdgePen = PenOf(0x70000000);
    private static readonly IPen RidgeLightPen = PenOf(0x38FFFFFF);
    private static readonly IPen HoleLipPen = PenOf(0x22FFFFFF);
    private static readonly IBrush ScrewBrush = Radial(0.35, 0.3, (0xFFC4C4CA, 0), (0xFF7A7A82, 0.6), (0xFF4A4A50, 1));
    private static readonly IPen ScrewPen = PenOf(0xA0000000);

    /// <summary>The shell behind the label, shell at (0, 0).</summary>
    internal static void DrawCassetteShell(DrawingContext ctx, double s)
    {
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        var shell = new Rect(0, 0, w, h);
        var r = s * 0.035;
        ctx.DrawRectangle(ShellBrush, null, shell, r, r);
        ctx.DrawRectangle(null, ShellEdgePen, shell.Deflate(0.5), r, r);
        // The moulded rim round the face.
        ctx.DrawRectangle(null, ShellRimPen, shell.Deflate(s * 0.014), r * 0.7, r * 0.7);

        // Paper label: a cream border round the art (the art itself is the caller's image).
        var paper = CassetteLabelRect(s).Inflate(s * 0.014);
        ctx.DrawRectangle(LabelPaperBrush, LabelEdgePen, paper, s * 0.024, s * 0.024);

        // Bottom ridge: the raised trapezoid with the tape-head openings.
        var top = h * 0.78;
        var trapezoid = new StreamGeometry();
        using (var g = trapezoid.Open())
        {
            g.BeginFigure(new Point(w * 0.22, top), true);
            g.LineTo(new Point(w * 0.78, top));
            g.LineTo(new Point(w * 0.83, h - 0.5));
            g.LineTo(new Point(w * 0.17, h - 0.5));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(RidgeBrush, RidgeEdgePen, trapezoid);
        ctx.DrawLine(RidgeLightPen, new Point(w * 0.22 + 1, top + 1), new Point(w * 0.78 - 1, top + 1));
        var holeR = h * 0.028;
        foreach (var fx in new[] { 0.31, 0.69 })
            ctx.DrawEllipse(HoleBrush, HoleLipPen, new Point(w * fx, top + (h - top) * 0.5), holeR, holeR);
        foreach (var fx in new[] { 0.42, 0.58 })
            ctx.DrawRectangle(HoleBrush, HoleLipPen, new Rect(w * fx - holeR, top + (h - top) * 0.3, holeR * 2, holeR * 2.2), holeR * 0.4, holeR * 0.4);

        // Screws: four corners and one in the ridge.
        var sr = h * 0.021;
        var slot = PenOf(0xB0000000, Math.Max(1, sr * 0.28));
        foreach (var p in new[] { new Point(w * 0.035, h * 0.06), new Point(w * 0.965, h * 0.06), new Point(w * 0.035, h * 0.94), new Point(w * 0.965, h * 0.94), new Point(w * 0.5, top + (h - top) * 0.62) })
        {
            ctx.DrawEllipse(ScrewBrush, ScrewPen, p, sr, sr);
            ctx.DrawLine(slot, new Point(p.X - sr * 0.55, p.Y), new Point(p.X + sr * 0.55, p.Y));
            ctx.DrawLine(slot, new Point(p.X, p.Y - sr * 0.55), new Point(p.X, p.Y + sr * 0.55));
        }
    }

    private static readonly IBrush WindowBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0x0E, 0x0E, 0x11));
    // Clear plastic over the window: a soft glare across its upper half.
    private static readonly IBrush WindowGlareBrush = Linear(0, 0, 0.3, 1, (0x26FFFFFF, 0), (0x0AFFFFFF, 0.42), (0x00FFFFFF, 0.43), (0x00FFFFFF, 1));
    private static readonly IPen WindowRimPen = PenOf(0xA0000000, 1.5);
    private static readonly IPen WindowLipPen = new Pen(Linear(0, 0, 0, 1, (0x10FFFFFF, 0), (0x40FFFFFF, 1)), 1);
    // Wound tape: matt brown, a little lighter towards the outer layers.
    private static readonly IBrush TapePackBrush = Radial(0.5, 0.5,
        (0xFF3C2A1E, 0), (0xFF4A3426, 0.7), (0xFF563C2B, 0.9), (0xFF2A1D15, 1));
    private static readonly IPen TapeEdgePen = PenOf(0x50000000);
    private static readonly IBrush HubBrush = Radial(0.4, 0.35, (0xFFFFFFFF, 0), (0xFFE4E4E8, 0.6), (0xFFB4B4BC, 1));
    private static readonly IPen HubEdgePen = PenOf(0x60000000);

    /// <summary>Behind the reels: the dark window and the two tape packs (most of the tape on
    /// the left reel; the window crops the big pack). Shell at (0, 0).</summary>
    internal static void DrawCassetteWindow(DrawingContext ctx, double s)
    {
        var win = CassetteWindowRect(s);
        var wr = CassetteHeight * s * 0.05;
        ctx.DrawRectangle(WindowBrush, null, win, wr, wr);
        var (left, right) = CassetteReelCentres(s);
        using (ctx.PushClip(new RoundedRect(win, wr)))
        {
            ctx.DrawEllipse(TapePackBrush, TapeEdgePen, left, win.Height * 0.6, win.Height * 0.6);
            ctx.DrawEllipse(TapePackBrush, TapeEdgePen, right, win.Height * 0.38, win.Height * 0.38);
        }
    }

    /// <summary>In front of the reels: the window's glare, dark rim and lit lower lip. Shell at (0, 0).</summary>
    internal static void DrawCassetteGlass(DrawingContext ctx, double s)
    {
        var win = CassetteWindowRect(s);
        var wr = CassetteHeight * s * 0.05;
        using (ctx.PushClip(new RoundedRect(win, wr)))
            ctx.DrawRectangle(WindowGlareBrush, null, win);
        ctx.DrawRectangle(null, WindowRimPen, win, wr, wr);
        ctx.DrawRectangle(null, WindowLipPen, win.Deflate(1.5), wr, wr);
    }

    /// <summary>A reel hub: a white ring with six drive teeth pointing into its open centre.</summary>
    internal static void DrawReel(DrawingContext ctx, Point c, double r)
    {
        ctx.DrawEllipse(HubBrush, HubEdgePen, c, r, r);
        var hole = r * 0.66;
        ctx.DrawEllipse(HoleBrush, null, c, hole, hole);
        var teeth = new StreamGeometry();
        using (var g = teeth.Open())
        {
            for (var i = 0; i < 6; i++)
            {
                var a = i * Math.PI / 3;
                var (dx, dy) = (Math.Cos(a), Math.Sin(a));
                var (tx, ty) = (-dy, dx);
                var baseR = hole + 0.5;
                var tipR = r * 0.47;
                var bw = r * 0.13;
                var tw = r * 0.08;
                g.BeginFigure(new Point(c.X + dx * baseR + tx * bw, c.Y + dy * baseR + ty * bw), true);
                g.LineTo(new Point(c.X + dx * tipR + tx * tw, c.Y + dy * tipR + ty * tw));
                g.LineTo(new Point(c.X + dx * tipR - tx * tw, c.Y + dy * tipR - ty * tw));
                g.LineTo(new Point(c.X + dx * baseR - tx * bw, c.Y + dy * baseR - ty * bw));
                g.EndFigure(true);
            }
        }
        ctx.DrawGeometry(HubBrush, null, teeth);
    }
}

/// <summary>Which <see cref="MediaCaseArt"/> part a <see cref="MediaCaseLayer"/> draws.</summary>
public enum MediaCasePart
{
    /// <summary>The disc and its grooves, filling the layer (round: it may turn).</summary>
    Record,
    /// <summary>The label's shaded edge and spindle hole, the layer being the label.</summary>
    RecordLabelFinish,
    /// <summary>The fixed light on a record filling the layer.</summary>
    RecordLight,
    /// <summary>The sleeve's card finish, the layer being the sleeve.</summary>
    SleeveFinish,
    /// <summary>The cassette shell, the layer being the shell.</summary>
    CassetteShell,
    /// <summary>The cassette's window and tape packs, the layer being the shell.</summary>
    CassetteWindow,
    /// <summary>One reel hub filling the layer (round: it may turn).</summary>
    CassetteReel,
    /// <summary>The cassette window's glass, the layer being the shell.</summary>
    CassetteGlass,
}

/// <summary>
/// One <see cref="MediaCaseArt"/> part drawn to fill this control, for XAML costumes
/// (<see cref="MediaArtwork"/>) that interleave the drawing with their own image stack and
/// turn some parts with a RenderTransform. The draw list is recorded once per size, so a
/// turning record or reel costs only its transform per frame.
/// </summary>
public sealed class MediaCaseLayer : Control
{
    public static readonly StyledProperty<MediaCasePart> PartProperty =
        AvaloniaProperty.Register<MediaCaseLayer, MediaCasePart>(nameof(Part));

    static MediaCaseLayer()
    {
        AffectsRender<MediaCaseLayer>(PartProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<MediaCaseLayer>(false);
    }

    public MediaCasePart Part { get => GetValue(PartProperty); set => SetValue(PartProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var c = new Point(size.Width / 2, size.Height / 2);
        var r = Math.Min(size.Width, size.Height) / 2;
        var s = size.Width / MediaCaseArt.CassetteWidth;
        switch (Part)
        {
            case MediaCasePart.Record: MediaCaseArt.DrawRecord(context, c, r); break;
            case MediaCasePart.RecordLabelFinish: MediaCaseArt.DrawRecordLabelFinish(context, c, r); break;
            case MediaCasePart.RecordLight: MediaCaseArt.DrawRecordLight(context, c, r); break;
            case MediaCasePart.SleeveFinish: MediaCaseArt.DrawSleeveFinish(context, new Rect(size)); break;
            case MediaCasePart.CassetteShell: MediaCaseArt.DrawCassetteShell(context, s); break;
            case MediaCasePart.CassetteWindow: MediaCaseArt.DrawCassetteWindow(context, s); break;
            case MediaCasePart.CassetteReel: MediaCaseArt.DrawReel(context, c, r); break;
            case MediaCasePart.CassetteGlass: MediaCaseArt.DrawCassetteGlass(context, s); break;
        }
    }
}
