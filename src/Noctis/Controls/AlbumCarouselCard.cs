using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// One album in the <see cref="AlbumCarousel"/>: its cover, optionally dressed as a physical
/// release (<see cref="AlbumCarouselStyle"/>). The cases are original vector drawings (no
/// bitmaps): a clear CD jewel case with its hinge spine, a record sleeve with the disc peeking
/// out, and a cassette shell with the cover as its label. Code-built and recycled by the
/// carousel, so a card is just a few visuals: the case body is drawn by a layer behind the
/// cover image, the jewel case's lid gloss by a thin overlay above it.
///
/// Every style stands on a floor reflection (GitHub #233): the whole composed card (case,
/// cover, lid / window) drawn again upside down under it and fading out. It is one more
/// recorded draw list replaying the same shapes and the already-decoded cover bitmap, so the
/// carousel's glide (transforms only) never re-records it; it redraws only when the album,
/// style, size or decoded cover changes.
/// </summary>
public sealed class AlbumCarouselCard : Panel
{
    /// <summary>Card layout size as multiples of the cover edge, per style.</summary>
    public static Size SizeFactor(AlbumCarouselStyle style) => style switch
    {
        AlbumCarouselStyle.JewelCase => new Size(JewelWidth, JewelHeight),
        AlbumCarouselStyle.Vinyl => new Size(VinylWidth, 1),
        AlbumCarouselStyle.Cassette => new Size(CassetteWidth, CassetteHeight),
        _ => new Size(1, 1),
    };

    /// <summary>Extra height under the card that its floor reflection uses (× cover edge).
    /// Every style has one (GitHub #233); the parameter stays for callers sizing per style.</summary>
    public static double ReflectionFactor(AlbumCarouselStyle style) => ReflectionHeight + ReflectionGap;

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
    internal const double ReflectionHeight = 0.3;
    /// <summary>Gap between the card's bottom edge and its reflection (× cover edge).</summary>
    internal const double ReflectionGap = 0.012;

    private readonly Border _coverFrame;
    private readonly CachedImage _cover;
    private readonly TextBlock _placeholder;
    private readonly ReflectionLayer _reflection;
    private readonly OverlayLayer _gloss;
    private readonly BodyLayer _body;
    private double _dim;

    private Album? _album;
    private AlbumCarouselStyle _style;
    private double _coverSize = 260;

    public AlbumCarouselCard()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent; // the whole card is a click target

        _cover = new CachedImage
        {
            Stretch = Stretch.UniformToFill,
            // A ceiling: CachedImage decodes the smallest bucket covering its drawn size.
            DecodeWidth = 768,
        };
        RenderOptions.SetBitmapInterpolationMode(_cover, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _placeholder = new TextBlock
        {
            Text = Loc.T("LibraryAlbums.Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.2,
            Foreground = Brushes.White,
        };
        _coverFrame = new Border
        {
            ClipToBounds = true,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x40), 0), new GradientStop(Color.FromRgb(0x1C, 0x1C, 0x20), 1) },
            },
            Child = new Panel { Children = { _placeholder, _cover } },
        };

        // Floor reflection (every style, GitHub #233): the composed card again, flipped,
        // fading out downwards. Redrawn once the cover has decoded (it draws that bitmap),
        // as is the body, whose record label prints the cover too.
        _reflection = new ReflectionLayer(this)
        {
            ClipToBounds = true,
            IsHitTestVisible = false,
            OpacityMask = ReflectionFade,
        };
        RenderOptions.SetBitmapInterpolationMode(_reflection, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _gloss = new OverlayLayer(this) { IsHitTestVisible = false };
        _body = new BodyLayer(this) { IsHitTestVisible = false };
        _cover.PropertyChanged += (_, e) =>
        {
            if (e.Property != Image.SourceProperty) return;
            _reflection.InvalidateVisual();
            if (_style == AlbumCarouselStyle.Vinyl) _body.InvalidateVisual();
        };

        Children.Add(_body);
        Children.Add(_coverFrame);
        Children.Add(_reflection);
        Children.Add(_gloss);
        ApplyStyle();
    }

    /// <summary>Index of the album in the carousel's list (set by the carousel when it binds the card).</summary>
    public int Index { get; set; } = -1;

    public Album? Album
    {
        get => _album;
        set
        {
            if (ReferenceEquals(_album, value)) return;
            _album = value;
            var path = value?.ArtworkPath;
            _cover.SourcePath = path;
            _placeholder.IsVisible = string.IsNullOrEmpty(path);
            Avalonia.Automation.AutomationProperties.SetName(this, value?.Name);
            _body.InvalidateVisual();
            _gloss.InvalidateVisual();
            _reflection.InvalidateVisual();
        }
    }

    public AlbumCarouselStyle CaseStyle
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            ApplyStyle();
        }
    }

    /// <summary>Edge of the square cover, px; the card's size follows from it and the style.</summary>
    public double CoverSize
    {
        get => _coverSize;
        set
        {
            if (Math.Abs(_coverSize - value) < 0.01) return;
            _coverSize = value;
            ApplyStyle();
        }
    }

    /// <summary>0..1 black wash (side cards sit a little in the dark).</summary>
    public double Dim
    {
        get => _dim;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _dim) < 0.004) return;
            _dim = v;
            _gloss.InvalidateVisual();
        }
    }

    /// <summary>The card's own layout size at the current cover size and style.</summary>
    public Size CardSize
    {
        get
        {
            var f = SizeFactor(_style);
            return new Size(f.Width * _coverSize, f.Height * _coverSize);
        }
    }

    private void ApplyStyle()
    {
        var s = _coverSize;
        _coverFrame.CornerRadius = new CornerRadius(CoverCornerRadius);
        _placeholder.FontSize = Math.Max(12, s * 0.16);
        InvalidateMeasure();
        InvalidateArrange();
        _body.InvalidateVisual();
        _gloss.InvalidateVisual();
        _reflection.InvalidateVisual();
    }

    private double CoverCornerRadius => _coverSize * _style switch
    {
        AlbumCarouselStyle.Cover => 0.012,
        AlbumCarouselStyle.Cassette => 0.018,
        _ => 0.004,
    };

    /// <summary>Where the cover image sits inside the card, per style.</summary>
    internal Rect CoverRect
    {
        get
        {
            var s = _coverSize;
            return _style switch
            {
                AlbumCarouselStyle.JewelCase => new Rect(JewelWidth * s - s - 0.025 * s, (JewelHeight * s - s) / 2, s, s),
                AlbumCarouselStyle.Cassette => CassetteLabelRect(s),
                _ => new Rect(0, 0, s, s),
            };
        }
    }

    private static Rect CassetteLabelRect(double s)
    {
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        return new Rect(w * 0.07, h * 0.08, w * 0.86, h * 0.56);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(Size.Infinity);
        return CardSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = _coverSize;
        var cover = CoverRect;
        _coverFrame.Arrange(cover);
        var full = new Rect(CardSize);
        // Reflection: a strip under the card, the card mirrored about the gap's middle, so
        // the strip sees the card's bottom edge first.
        _reflection.Arrange(new Rect(0, full.Height + s * ReflectionGap, full.Width, s * ReflectionHeight));
        _body.Arrange(full);
        _gloss.Arrange(full);
        return CardSize;
    }

    // ── Case bodies (drawn behind the cover) ──────────────────────────────────

    // Every brush here is relative to what it fills, so one set serves every card (and its
    // reflection) at any size instead of new brushes per drawing.
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

    /// <param name="reflection">Drawing the floor reflection: leaves out the record's grooves,
    /// which cannot be seen at a third of the brightness and are most of a record's shapes.</param>
    private void RenderBody(DrawingContext context, bool reflection = false)
    {
        switch (_style)
        {
            case AlbumCarouselStyle.JewelCase: RenderJewelTray(context); break;
            case AlbumCarouselStyle.Vinyl: RenderDisc(context, grooves: !reflection); break;
            case AlbumCarouselStyle.Cassette: RenderShell(context); break;
        }
    }

    // Jewel case: a black tray seen through the clear lid; the hinge spine is clear plastic
    // curving round the hinge, so it is shaded like a cylinder lit from the left.
    private static readonly IBrush TrayBrush = Linear(0, 0, 0, 1, (0xFF1C1C21, 0), (0xFF0C0C0E, 1));
    private static readonly IBrush HingeBrush = Linear(0, 0, 1, 0,
        (0xFF34343B, 0), (0xFF6E6E78, 0.12), (0xFF2E2E35, 0.32), (0xFF1A1A1F, 0.75), (0xFF111114, 0.96), (0xFF2C2C33, 1));
    private static readonly IBrush KnuckleBrush = Linear(0, 0, 1, 0,
        (0xFF4A4A53, 0), (0xFFA4A4AE, 0.35), (0xFF4C4C55, 0.62), (0xFF222228, 1));
    private static readonly IPen KnucklePen = PenOf(0x70000000);
    private static readonly IPen RibShadePen = PenOf(0x70000000);
    private static readonly IPen RibLightPen = PenOf(0x22FFFFFF);

    private void RenderJewelTray(DrawingContext ctx)
    {
        var s = _coverSize;
        var size = CardSize;
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

    // Vinyl: black PVC that is darkest in the grooves and catches the light along the rim.
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
    private static readonly IBrush SleeveShadowBrush = Linear(0, 0, 1, 0, (0x90000000, 0), (0x00000000, 1));

    private static Point VinylDiscCentre(double s) => new(s * (0.5 + VinylDiscOffset), s * 0.5);

    private void RenderDisc(DrawingContext ctx, bool grooves = true)
    {
        var s = _coverSize;
        var c = VinylDiscCentre(s);
        var radius = VinylDiscRadius * s;
        var disc = new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        ctx.DrawEllipse(DiscBrush, null, c, radius, radius);
        var labelR = radius * VinylLabelRadius;
        var runOut = labelR * 1.2;
        if (grooves)
        {
            // Grooves from the lead-in to the run-out, alternating so they read as texture,
            // broken by the darker gaps between songs.
            var outer = radius * 0.965;
            var step = Math.Max(1.6, s * 0.009);
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
        ctx.DrawEllipse(DiscSheenBrush, DiscRimPen, c, radius - 0.5, radius - 0.5);

        // Label: the album art again, printed small, as on the now-playing record.
        var label = new Rect(c.X - labelR, c.Y - labelR, labelR * 2, labelR * 2);
        if (_cover.Source is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            using (ctx.PushGeometryClip(new EllipseGeometry(label)))
                ctx.DrawImage(image, UniformToFillSource(image.Size, label.Size), label);
        }
        else
        {
            ctx.DrawEllipse(LabelBrush, null, c, labelR, labelR);
        }
        ctx.DrawEllipse(LabelShadeBrush, LabelRingPen, c, labelR, labelR);
        ctx.DrawEllipse(HoleBrush, SpindleLipPen, c, s * 0.012, s * 0.012);

        // The sleeve's shadow falling on the disc right of its edge.
        using (ctx.PushGeometryClip(new EllipseGeometry(disc)))
            ctx.DrawRectangle(SleeveShadowBrush, null, new Rect(s, 0, s * 0.07, s));
    }

    // Cassette: moulded plastic lit from above, a paper label round the art, the raised
    // ridge at the foot with the tape-head openings, five screws.
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

    private void RenderShell(DrawingContext ctx)
    {
        var s = _coverSize;
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        var shell = new Rect(0, 0, w, h);
        var r = s * 0.035;
        ctx.DrawRectangle(ShellBrush, null, shell, r, r);
        ctx.DrawRectangle(null, ShellEdgePen, shell.Deflate(0.5), r, r);
        // The moulded rim round the face.
        ctx.DrawRectangle(null, ShellRimPen, shell.Deflate(s * 0.014), r * 0.7, r * 0.7);

        // Paper label: a cream border round the art (the art itself is the cover image).
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

    /// <summary>Tape window over the lower middle of the label (drawn above the cover), two reels in it.</summary>
    private void RenderCassetteWindow(DrawingContext ctx)
    {
        var s = _coverSize;
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        var label = CassetteLabelRect(s);
        var win = new Rect(w * 0.29, label.Y + label.Height * 0.52, w * 0.42, h * 0.18);
        var wr = h * 0.05;
        ctx.DrawRectangle(WindowBrush, null, win, wr, wr);
        var cy = win.Center.Y;
        var left = new Point(win.X + win.Width * 0.22, cy);
        var right = new Point(win.Right - win.Width * 0.22, cy);
        using (ctx.PushClip(new RoundedRect(win, wr)))
        {
            // Most of the tape is wound on the left reel; the window crops the big pack.
            ctx.DrawEllipse(TapePackBrush, TapeEdgePen, left, win.Height * 0.6, win.Height * 0.6);
            ctx.DrawEllipse(TapePackBrush, TapeEdgePen, right, win.Height * 0.38, win.Height * 0.38);
            var hubR = win.Height * 0.27;
            DrawReel(ctx, left, hubR);
            DrawReel(ctx, right, hubR);
            ctx.DrawRectangle(WindowGlareBrush, null, win);
        }
        ctx.DrawRectangle(null, WindowRimPen, win, wr, wr);
        ctx.DrawRectangle(null, WindowLipPen, win.Deflate(1.5), wr, wr);
    }

    /// <summary>A reel hub: a white ring with six drive teeth pointing into its open centre.</summary>
    private static void DrawReel(DrawingContext ctx, Point c, double r)
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


    /// <summary>Drawn behind the cover: the case body (Panel.Render is sealed).</summary>
    private sealed class BodyLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        public BodyLayer(AlbumCarouselCard card) => _card = card;
        public override void Render(DrawingContext context) => _card.RenderBody(context);
    }

    /// <summary>Drawn above the cover: the jewel case's clear lid (edges, spine seam, glare),
    /// the record sleeve's card finish, the cassette's window; then the side-card dim, shaped
    /// to the card's silhouette so the transparent corners round a record or a cassette stay
    /// transparent.</summary>
    private sealed class OverlayLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        // Glare on the lid: a broad wash from the top-left corner that ends in a crisp edge,
        // then a thin second streak, as clear plastic reflects a window.
        private static readonly IBrush Gloss = Linear(0, 0, 1, 1,
            (0x34FFFFFF, 0), (0x14FFFFFF, 0.28), (0x0AFFFFFF, 0.4), (0x00FFFFFF, 0.401),
            (0x00FFFFFF, 0.64), (0x12FFFFFF, 0.68), (0x00FFFFFF, 0.74));
        // Polished lid edge: lit along the top and left, falling off to the bottom right.
        private static readonly IPen LidEdgePen = new Pen(Linear(0, 0, 1, 1, (0xA0FFFFFF, 0), (0x40FFFFFF, 0.5), (0x22FFFFFF, 1)), 1);
        private static readonly IPen LidInnerPen = PenOf(0x70000000);
        private static readonly IPen LidLipPen = new Pen(Linear(0, 0, 1, 0, (0x00FFFFFF, 0), (0x50FFFFFF, 0.3), (0x10FFFFFF, 1)), 1);
        private static readonly IPen SeamPen = PenOf(0x48FFFFFF);
        private static readonly IPen SeamShadePen = PenOf(0x90000000);

        // Record sleeve: printed card with a faint satin sheen, the record's ring pressed
        // through it, and the open edge on the right where its two plies part.
        private static readonly IBrush SleeveSheen = Linear(0, 0, 1, 1, (0x16FFFFFF, 0), (0x00FFFFFF, 0.45), (0x00000000, 0.7), (0x18000000, 1));
        private static readonly IBrush SleeveOpening = Linear(0, 0, 1, 0, (0x00000000, 0), (0x48000000, 0.85), (0x22FFFFFF, 0.86), (0x30000000, 1));
        private static readonly IPen SleeveEdgePen = new Pen(Linear(0, 0, 1, 1, (0x50FFFFFF, 0), (0x18FFFFFF, 0.5), (0x40000000, 1)), 1);
        // Ring wear: a soft worn band where the record inside presses through the card.
        private static readonly IBrush RingWear = Radial(0.5, 0.5, (0x00FFFFFF, 0.9), (0x0EFFFFFF, 0.95), (0x00FFFFFF, 1));

        public OverlayLayer(AlbumCarouselCard card) => _card = card;

        /// <summary>What sits above the cover: the jewel case's lid, the sleeve's finish, the cassette's window.</summary>
        internal static void RenderLid(AlbumCarouselCard card, DrawingContext context, Rect rect)
        {
            var s = card._coverSize;
            if (card._style == AlbumCarouselStyle.JewelCase)
            {
                var r = s * 0.018;
                context.DrawRectangle(Gloss, null, rect, r, r);
                // Where the lid's front meets the hinge: a groove with a lit lip beside it.
                var seam = Math.Round(JewelSpine * s) + 0.5;
                context.DrawLine(SeamShadePen, new Point(seam, 1), new Point(seam, rect.Height - 1));
                context.DrawLine(SeamPen, new Point(seam + 1, 1), new Point(seam + 1, rect.Height - 1));
                // The front's raised lip catching the light along the top.
                var lipY = Math.Round(s * 0.009) + 0.5;
                context.DrawLine(LidLipPen, new Point(seam + s * 0.02, lipY), new Point(rect.Width - s * 0.03, lipY));
                // Edges last: bright outside, a dark line inside for the plastic's thickness.
                context.DrawRectangle(null, LidEdgePen, rect.Deflate(0.5), r, r);
                context.DrawRectangle(null, LidInnerPen, rect.Deflate(1.5), r, r);
            }
            else if (card._style == AlbumCarouselStyle.Vinyl)
            {
                var sleeve = new Rect(0, 0, s, s);
                context.DrawRectangle(SleeveSheen, null, sleeve);
                context.DrawEllipse(RingWear, null, sleeve.Center, s * 0.46, s * 0.46);
                context.DrawRectangle(SleeveOpening, null, new Rect(s - s * 0.028, 0, s * 0.028, s));
                context.DrawRectangle(null, SleeveEdgePen, sleeve.Deflate(0.5));
            }
            else if (card._style == AlbumCarouselStyle.Cassette)
            {
                card.RenderCassetteWindow(context);
            }
        }


        public override void Render(DrawingContext context)
        {
            var s = _card._coverSize;
            var rect = new Rect(Bounds.Size);
            RenderLid(_card, context, rect);

            if (_card._dim <= 0) return;
            var wash = new SolidColorBrush(Colors.Black, _card._dim);
            switch (_card._style)
            {
                case AlbumCarouselStyle.Vinyl:
                    var c = VinylDiscCentre(s);
                    var radius = VinylDiscRadius * s;
                    // One union, so the sleeve-over-disc overlap is not washed twice.
                    context.DrawGeometry(wash, null, new CombinedGeometry(GeometryCombineMode.Union,
                        new EllipseGeometry(new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2)),
                        new RectangleGeometry(new Rect(0, 0, s, s))));
                    break;
                case AlbumCarouselStyle.Cover:
                    context.DrawRectangle(wash, null, new Rect(0, 0, s, s), s * 0.012, s * 0.012);
                    break;
                default:
                    context.DrawRectangle(wash, null, rect, s * 0.035, s * 0.035);
                    break;
            }
        }
    }

    /// <summary>The reflection's fade: a third of the card's brightness at the floor line,
    /// nothing at the strip's bottom (what the plain cover's reflection always used).</summary>
    private static readonly IBrush ReflectionFade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0), new GradientStop(Colors.Transparent, 1) },
    };

    /// <summary>Times this card's reflection was (re)drawn (tests: a glide must not redraw it).</summary>
    internal int ReflectionRenders { get; private set; }

    /// <summary>Where the reflection strip sits, in card space.</summary>
    internal Rect ReflectionBounds => _reflection.Bounds;

    /// <summary>Draws the whole card (body, cover, lid) flipped, for the floor reflection.</summary>
    private void RenderReflection(DrawingContext ctx)
    {
        ReflectionRenders++;
        var card = CardSize;
        // Card space to strip space: y' = cardHeight - y puts the card's bottom edge at the
        // strip's top, mirrored about the middle of the gap between them.
        using (ctx.PushTransform(new Matrix(1, 0, 0, -1, 0, card.Height)))
        {
            RenderBody(ctx, reflection: true);
            var cover = CoverRect;
            using (ctx.PushClip(new RoundedRect(cover, CoverCornerRadius)))
            {
                ctx.DrawRectangle(_coverFrame.Background, null, cover);
                if (_cover.Source is { } image && image.Size.Width > 0 && image.Size.Height > 0)
                    ctx.DrawImage(image, UniformToFillSource(image.Size, cover.Size), cover);
            }
            OverlayLayer.RenderLid(this, ctx, new Rect(card));
        }
    }

    /// <summary>The part of an image of <paramref name="image"/> size that Stretch.UniformToFill
    /// shows in a box of <paramref name="box"/> size (centred crop), as the cover image does.</summary>
    internal static Rect UniformToFillSource(Size image, Size box)
    {
        if (box.Width <= 0 || box.Height <= 0) return new Rect(image);
        var scale = Math.Max(box.Width / image.Width, box.Height / image.Height);
        var w = box.Width / scale;
        var h = box.Height / scale;
        return new Rect((image.Width - w) / 2, (image.Height - h) / 2, w, h);
    }

    /// <summary>Drawn under the card: its floor reflection (Panel.Render is sealed).</summary>
    private sealed class ReflectionLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        public ReflectionLayer(AlbumCarouselCard card) => _card = card;
        public override void Render(DrawingContext context) => _card.RenderReflection(context);
    }
}
