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

    /// <summary>Extra height under the card that its floor reflection uses (× cover edge).</summary>
    public static double ReflectionFactor(AlbumCarouselStyle style) =>
        style == AlbumCarouselStyle.Cover ? ReflectionHeight : 0;

    // Jewel case: 142 × 125 mm with a 120 mm insert → the cover fills the lid right of the spine.
    internal const double JewelWidth = 1.17, JewelHeight = 1.04, JewelSpine = 0.12;
    // Vinyl: the disc's centre sits this far right of the sleeve's centre.
    internal const double VinylDiscRadius = 0.47, VinylDiscOffset = 0.36;
    internal const double VinylWidth = 0.5 + VinylDiscOffset + VinylDiscRadius;
    // Cassette: shell ≈ 100 × 64 mm.
    internal const double CassetteWidth = 1.3, CassetteHeight = 0.83;
    internal const double ReflectionHeight = 0.3;

    private readonly Border _coverFrame;
    private readonly CachedImage _cover;
    private readonly TextBlock _placeholder;
    private readonly Border _reflectionFrame;
    private readonly CachedImage _reflection;
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

        // Floor reflection (plain cover only): the cover again, flipped, fading out downwards.
        _reflection = new CachedImage
        {
            Stretch = Stretch.UniformToFill,
            DecodeWidth = 256,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new ScaleTransform(1, -1),
        };
        _reflectionFrame = new Border
        {
            ClipToBounds = true,
            IsHitTestVisible = false,
            Child = _reflection,
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0), new GradientStop(Colors.Transparent, 1) },
            },
        };

        _gloss = new OverlayLayer(this) { IsHitTestVisible = false };
        _body = new BodyLayer(this) { IsHitTestVisible = false };

        Children.Add(_body);
        Children.Add(_coverFrame);
        Children.Add(_reflectionFrame);
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
            _reflection.SourcePath = _style == AlbumCarouselStyle.Cover ? path : null;
            _placeholder.IsVisible = string.IsNullOrEmpty(path);
            Avalonia.Automation.AutomationProperties.SetName(this, value?.Name);
            _body.InvalidateVisual();
            _gloss.InvalidateVisual();
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
        var reflect = _style == AlbumCarouselStyle.Cover;
        _reflectionFrame.IsVisible = reflect;
        _reflection.SourcePath = reflect ? _album?.ArtworkPath : null;
        _coverFrame.CornerRadius = new CornerRadius(_style switch
        {
            AlbumCarouselStyle.Cover => s * 0.012,
            AlbumCarouselStyle.Cassette => s * 0.018,
            _ => s * 0.004,
        });
        _placeholder.FontSize = Math.Max(12, s * 0.16);
        InvalidateMeasure();
        InvalidateArrange();
        _body.InvalidateVisual();
        _gloss.InvalidateVisual();
    }

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
        // Reflection: a cover-sized image flipped about its own middle, shown through a strip
        // under the card, so the strip sees the cover's bottom edge first.
        _reflectionFrame.Arrange(new Rect(0, s + s * 0.012, s, s * ReflectionHeight));
        _reflection.Width = s;
        _reflection.Height = s;
        var full = new Rect(CardSize);
        _body.Arrange(full);
        _gloss.Arrange(full);
        return CardSize;
    }

    // ── Case bodies (drawn behind the cover) ──────────────────────────────────

    private static readonly IBrush TrayBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x12));
    private static readonly IPen CaseEdgePen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen RidgePen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush HingeBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GroovePen = new Pen(new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen DiscRimPen = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xE3, 0xD6));
    private static readonly IPen LabelRingPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1);
    private static readonly IBrush ShellBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0x36, 0x36, 0x3C), 0), new GradientStop(Color.FromRgb(0x1E, 0x1E, 0x22), 1) },
    };
    private static readonly IBrush WindowBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x12, 0x15));
    private static readonly IBrush TapeBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x32, 0x24));
    private static readonly IBrush HubBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
    private static readonly IBrush HoleBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D));
    private static readonly IPen ScrewPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1);
    private static readonly IBrush ScrewBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));

    private void RenderBody(DrawingContext context)
    {
        switch (_style)
        {
            case AlbumCarouselStyle.JewelCase: RenderJewelTray(context); break;
            case AlbumCarouselStyle.Vinyl: RenderDisc(context); break;
            case AlbumCarouselStyle.Cassette: RenderShell(context); break;
        }
    }

    private void RenderJewelTray(DrawingContext ctx)
    {
        var s = _coverSize;
        var size = CardSize;
        var body = new Rect(size);
        var r = s * 0.018;
        ctx.DrawRectangle(TrayBrush, null, body, r, r);
        // Spine: a few moulded ridges and the two hinge knuckles at top and bottom.
        var spineW = JewelSpine * s;
        for (var i = 1; i <= 3; i++)
        {
            var x = Math.Round(spineW * i / 4) + 0.5;
            ctx.DrawLine(RidgePen, new Point(x, s * 0.08), new Point(x, size.Height - s * 0.08));
        }
        var knuckleH = s * 0.07;
        ctx.DrawRectangle(HingeBrush, null, new Rect(spineW * 0.18, s * 0.02, spineW * 0.64, knuckleH), s * 0.008, s * 0.008);
        ctx.DrawRectangle(HingeBrush, null, new Rect(spineW * 0.18, size.Height - s * 0.02 - knuckleH, spineW * 0.64, knuckleH), s * 0.008, s * 0.008);
    }

    private void RenderDisc(DrawingContext ctx)
    {
        var s = _coverSize;
        var c = new Point(s * (0.5 + VinylDiscOffset), s * 0.5);
        var radius = VinylDiscRadius * s;
        var disc = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x22, 0x22, 0x24), 0),
                new GradientStop(Color.FromRgb(0x0C, 0x0C, 0x0D), 0.75),
                new GradientStop(Color.FromRgb(0x16, 0x16, 0x18), 1),
            },
        };
        ctx.DrawEllipse(disc, DiscRimPen, c, radius, radius);
        // Grooves: fine rings across the playing surface.
        var labelR = radius * 0.34;
        var step = Math.Max(2.5, s * 0.016);
        for (var g = labelR + step * 1.5; g < radius - step; g += step)
            ctx.DrawEllipse(null, GroovePen, c, g, g);
        // Light catching the vinyl: two soft opposite bands across the disc.
        var sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0.2),
                new GradientStop(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0.33),
                new GradientStop(Colors.Transparent, 0.45),
                new GradientStop(Colors.Transparent, 0.55),
                new GradientStop(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0.67),
                new GradientStop(Colors.Transparent, 0.8),
            },
        };
        ctx.DrawEllipse(sheen, null, c, radius, radius);
        // Paper label and spindle hole (mostly behind the sleeve; its edge shows).
        ctx.DrawEllipse(LabelBrush, LabelRingPen, c, labelR, labelR);
        ctx.DrawEllipse(HoleBrush, null, c, s * 0.012, s * 0.012);
        // The sleeve's shadow falling on the disc right of its edge.
        var shadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x90, 0, 0, 0), 0), new GradientStop(Colors.Transparent, 1) },
        };
        using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2))))
            ctx.DrawRectangle(shadow, null, new Rect(s, 0, s * 0.07, s));
    }

    private void RenderShell(DrawingContext ctx)
    {
        var s = _coverSize;
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        ctx.DrawRectangle(ShellBrush, CaseEdgePen, new Rect(0, 0, w, h), s * 0.035, s * 0.035);

        // Bottom ridge: the trapezoid with the tape-head openings.
        var top = h * 0.78;
        var trapezoid = new StreamGeometry();
        using (var g = trapezoid.Open())
        {
            g.BeginFigure(new Point(w * 0.22, top), true);
            g.LineTo(new Point(w * 0.78, top));
            g.LineTo(new Point(w * 0.83, h));
            g.LineTo(new Point(w * 0.17, h));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), CaseEdgePen, trapezoid);
        var holeR = h * 0.028;
        foreach (var fx in new[] { 0.31, 0.69 })
            ctx.DrawEllipse(HoleBrush, null, new Point(w * fx, top + (h - top) * 0.55), holeR, holeR);
        foreach (var fx in new[] { 0.42, 0.58 })
            ctx.DrawRectangle(HoleBrush, null, new Rect(w * fx - holeR, top + (h - top) * 0.35, holeR * 2, holeR * 2.2), holeR * 0.4, holeR * 0.4);

        // Corner screws.
        var sr = h * 0.026;
        foreach (var p in new[] { new Point(w * 0.035, h * 0.06), new Point(w * 0.965, h * 0.06), new Point(w * 0.035, h * 0.94), new Point(w * 0.965, h * 0.94) })
        {
            ctx.DrawEllipse(ScrewBrush, ScrewPen, p, sr, sr);
            ctx.DrawLine(ScrewPen, new Point(p.X - sr * 0.6, p.Y), new Point(p.X + sr * 0.6, p.Y));
        }
    }

    /// <summary>Tape window over the lower middle of the label (drawn above the cover), two reels in it.</summary>
    private void RenderCassetteWindow(DrawingContext ctx)
    {
        var s = _coverSize;
        var w = CassetteWidth * s;
        var h = CassetteHeight * s;
        var label = CassetteLabelRect(s);
        var win = new Rect(w * 0.29, label.Y + label.Height * 0.52, w * 0.42, h * 0.18);
        ctx.DrawRectangle(WindowBrush, CaseEdgePen, win, h * 0.05, h * 0.05);
        var cy = win.Center.Y;
        var reelR = win.Height * 0.36;
        DrawReel(ctx, new Point(win.X + win.Width * 0.22, cy), reelR, reelR * 1.3);
        DrawReel(ctx, new Point(win.Right - win.Width * 0.22, cy), reelR, reelR * 0.8);
    }

    private static void DrawReel(DrawingContext ctx, Point c, double hubR, double tapeR)
    {
        ctx.DrawEllipse(TapeBrush, null, c, tapeR, tapeR);
        ctx.DrawEllipse(HubBrush, null, c, hubR * 0.62, hubR * 0.62);
        ctx.DrawEllipse(HoleBrush, null, c, hubR * 0.34, hubR * 0.34);
        // Six drive teeth.
        for (var i = 0; i < 6; i++)
        {
            var a = i * Math.PI / 3;
            var p = new Point(c.X + Math.Cos(a) * hubR * 0.34, c.Y + Math.Sin(a) * hubR * 0.34);
            ctx.DrawEllipse(HubBrush, null, p, hubR * 0.09, hubR * 0.09);
        }
    }

    /// <summary>Drawn behind the cover: the case body (Panel.Render is sealed).</summary>
    private sealed class BodyLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        public BodyLayer(AlbumCarouselCard card) => _card = card;
        public override void Render(DrawingContext context) => _card.RenderBody(context);
    }

    /// <summary>Drawn above the cover: the jewel case's clear lid (edge, spine seam, gloss),
    /// then the side-card dim, shaped to the card's silhouette so the transparent corners
    /// round a record or a cassette stay transparent.</summary>
    private sealed class OverlayLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        private static readonly IPen SeamPen = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 1);
        private static readonly IBrush Gloss = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.38),
                new GradientStop(Colors.Transparent, 0.39),
                new GradientStop(Colors.Transparent, 0.7),
                new GradientStop(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 0.71),
                new GradientStop(Colors.Transparent, 0.82),
            },
        };

        public OverlayLayer(AlbumCarouselCard card) => _card = card;

        public override void Render(DrawingContext context)
        {
            var s = _card._coverSize;
            var rect = new Rect(Bounds.Size);
            if (_card._style == AlbumCarouselStyle.JewelCase)
            {
                var r = s * 0.018;
                context.DrawRectangle(Gloss, CaseEdgePen, rect, r, r);
                var seam = Math.Round(JewelSpine * s) + 0.5;
                context.DrawLine(SeamPen, new Point(seam, 1), new Point(seam, rect.Height - 1));
            }
            else if (_card._style == AlbumCarouselStyle.Cassette)
            {
                _card.RenderCassetteWindow(context);
            }

            if (_card._dim <= 0) return;
            var wash = new SolidColorBrush(Colors.Black, _card._dim);
            switch (_card._style)
            {
                case AlbumCarouselStyle.Vinyl:
                    var c = new Point(s * (0.5 + VinylDiscOffset), s * 0.5);
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
}
