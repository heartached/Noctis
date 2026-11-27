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

    // Case proportions live with the drawing (MediaCaseArt), shared with the lyrics page.
    internal const double JewelWidth = MediaCaseArt.JewelWidth, JewelHeight = MediaCaseArt.JewelHeight, JewelSpine = MediaCaseArt.JewelSpine;
    internal const double VinylDiscRadius = MediaCaseArt.VinylDiscRadius, VinylDiscOffset = MediaCaseArt.VinylDiscOffset;
    internal const double VinylLabelRadius = MediaCaseArt.VinylLabelRadius;
    internal const double VinylWidth = MediaCaseArt.VinylWidth;
    internal const double CassetteWidth = MediaCaseArt.CassetteWidth, CassetteHeight = MediaCaseArt.CassetteHeight;
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
                AlbumCarouselStyle.Cassette => MediaCaseArt.CassetteLabelRect(s),
                _ => new Rect(0, 0, s, s),
            };
        }
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

    // ── Case bodies (drawn behind the cover; the drawing is MediaCaseArt's) ───

    /// <param name="reflection">Drawing the floor reflection: leaves out the record's grooves,
    /// which cannot be seen at a third of the brightness and are most of a record's shapes.</param>
    private void RenderBody(DrawingContext context, bool reflection = false)
    {
        var s = _coverSize;
        switch (_style)
        {
            case AlbumCarouselStyle.JewelCase: MediaCaseArt.DrawJewelTray(context, CardSize, s); break;
            case AlbumCarouselStyle.Vinyl: RenderDisc(context, grooves: !reflection); break;
            case AlbumCarouselStyle.Cassette: MediaCaseArt.DrawCassetteShell(context, s); break;
        }
    }

    private static readonly IBrush SleeveShadowBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0x90, 0, 0, 0), 0), new GradientStop(Colors.Transparent, 1) },
    };

    private void RenderDisc(DrawingContext ctx, bool grooves = true)
    {
        var s = _coverSize;
        var c = MediaCaseArt.VinylDiscCentre(s);
        var radius = VinylDiscRadius * s;
        var labelR = radius * VinylLabelRadius;
        MediaCaseArt.DrawRecord(ctx, c, radius, grooves);
        MediaCaseArt.DrawRecordLabel(ctx, c, labelR, _cover.Source);
        MediaCaseArt.DrawRecordLabelFinish(ctx, c, labelR);
        MediaCaseArt.DrawRecordLight(ctx, c, radius);
        // The sleeve's shadow falling on the disc right of its edge.
        using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2))))
            ctx.DrawRectangle(SleeveShadowBrush, null, new Rect(s, 0, s * 0.07, s));
    }

    /// <summary>Tape window over the lower middle of the label (drawn above the cover), two reels in it.</summary>
    private void RenderCassetteWindow(DrawingContext ctx)
    {
        var s = _coverSize;
        MediaCaseArt.DrawCassetteWindow(ctx, s);
        var (left, right) = MediaCaseArt.CassetteReelCentres(s);
        var hubR = MediaCaseArt.CassetteHubRadius(s);
        MediaCaseArt.DrawReel(ctx, left, hubR);
        MediaCaseArt.DrawReel(ctx, right, hubR);
        MediaCaseArt.DrawCassetteGlass(ctx, s);
    }

    /// <summary>Drawn behind the cover: the case body (Panel.Render is sealed).</summary>
    private sealed class BodyLayer : Control
    {
        private readonly AlbumCarouselCard _card;
        public BodyLayer(AlbumCarouselCard card) => _card = card;
        public override void Render(DrawingContext context) => _card.RenderBody(context);
    }

    /// <summary>Drawn above the cover: the jewel case's clear lid, the record sleeve's card
    /// finish, the cassette's window; then the side-card dim, shaped to the card's silhouette
    /// so the transparent corners round a record or a cassette stay transparent.</summary>
    private sealed class OverlayLayer : Control
    {
        private readonly AlbumCarouselCard _card;

        public OverlayLayer(AlbumCarouselCard card) => _card = card;

        /// <summary>What sits above the cover: the jewel case's lid, the sleeve's finish, the cassette's window.</summary>
        internal static void RenderLid(AlbumCarouselCard card, DrawingContext context, Rect rect)
        {
            var s = card._coverSize;
            switch (card._style)
            {
                case AlbumCarouselStyle.JewelCase: MediaCaseArt.DrawJewelLid(context, rect, s); break;
                case AlbumCarouselStyle.Vinyl: MediaCaseArt.DrawSleeveFinish(context, new Rect(0, 0, s, s)); break;
                case AlbumCarouselStyle.Cassette: card.RenderCassetteWindow(context); break;
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
                    var c = MediaCaseArt.VinylDiscCentre(s);
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
