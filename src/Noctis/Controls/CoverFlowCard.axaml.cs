using System;
using System.Windows.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Controls;

/// <summary>
/// One Cover Flow carousel card: a rounded glass frame holding a square cover with a
/// frosted caption strip (title + artist) inside the frame, the way iOS cards read.
/// Every slot in the carousel is one of these — the centre card adds an
/// <see cref="Overlay"/> (animated cover, favourite heart), the explicit badge and an
/// <see cref="ArtistCommand"/> that turns the artist caption into a link; side cards
/// get <see cref="Dim"/> and a 3D tilt from the host's RenderTransform.
/// </summary>
public partial class CoverFlowCard : UserControl
{
    public static readonly StyledProperty<string?> ArtworkPathProperty =
        AvaloniaProperty.Register<CoverFlowCard, string?>(nameof(ArtworkPath));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CoverFlowCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ArtistProperty =
        AvaloniaProperty.Register<CoverFlowCard, string?>(nameof(Artist));

    public static readonly StyledProperty<bool> IsExplicitProperty =
        AvaloniaProperty.Register<CoverFlowCard, bool>(nameof(IsExplicit));

    /// <summary>0..1 black wash over the whole card (depth cue for cards away from the centre).</summary>
    public static readonly StyledProperty<double> DimProperty =
        AvaloniaProperty.Register<CoverFlowCard, double>(nameof(Dim));

    /// <summary>Side of the square artwork; the card is this wide plus the frame edge.</summary>
    public static readonly StyledProperty<double> ArtworkSizeProperty =
        AvaloniaProperty.Register<CoverFlowCard, double>(nameof(ArtworkSize), 300);

    public static readonly StyledProperty<CornerRadius> CardCornerRadiusProperty =
        AvaloniaProperty.Register<CoverFlowCard, CornerRadius>(nameof(CardCornerRadius), new CornerRadius(20));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CoverFlowCard, int>(nameof(DecodeWidth), 512);

    /// <summary>Backdrop blur of the card slab when Liquid Glass is on (logical px).
    /// 0 = translucent tint only, no per-frame snapshot: the far cards use that so a
    /// 15-card row costs a handful of blurs, not fifteen.</summary>
    public static readonly StyledProperty<double> CardBlurRadiusProperty =
        AvaloniaProperty.Register<CoverFlowCard, double>(nameof(CardBlurRadius), 18);

    /// <summary>Extra content painted over the artwork (centre card: animated cover, heart).</summary>
    public static readonly StyledProperty<object?> OverlayProperty =
        AvaloniaProperty.Register<CoverFlowCard, object?>(nameof(Overlay));

    /// <summary>When set the artist caption becomes a link that runs this command.</summary>
    public static readonly StyledProperty<ICommand?> ArtistCommandProperty =
        AvaloniaProperty.Register<CoverFlowCard, ICommand?>(nameof(ArtistCommand));

    public static readonly StyledProperty<string?> TitleToolTipProperty =
        AvaloniaProperty.Register<CoverFlowCard, string?>(nameof(TitleToolTip));

    /// <summary>The title link was clicked. The host decides what that means (the carousel
    /// opens the album for the centre card and plays the track for a side card).</summary>
    public event EventHandler? TitleClicked;

    /// <summary>Caption text width cap: the artwork width minus caption padding and badge room.</summary>
    public static readonly DirectProperty<CoverFlowCard, double> TitleMaxWidthProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, double>(nameof(TitleMaxWidth), o => o.TitleMaxWidth);

    public static readonly DirectProperty<CoverFlowCard, double> PlaceholderFontSizeProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, double>(nameof(PlaceholderFontSize), o => o.PlaceholderFontSize);

    /// <summary>Caption type scales with the card: title ≈ 7% (cap 24) and artist ≈ 6.5%
    /// (cap 22) of the cover width — 21/20 on the 300px card, the mockup's near-equal pair.</summary>
    public static readonly DirectProperty<CoverFlowCard, double> TitleFontSizeProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, double>(nameof(TitleFontSize), o => o.TitleFontSize);

    public static readonly DirectProperty<CoverFlowCard, double> ArtistFontSizeProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, double>(nameof(ArtistFontSize), o => o.ArtistFontSize);

    /// <summary>Glass tint opacity of the slab: a frosted slab stays light and see-through
    /// (the blur already dissolves what is behind, as in the reference); a tint-only slab
    /// (blur 0) must be heavier or the neighbours read straight through it.</summary>
    public static readonly DirectProperty<CoverFlowCard, double> CardTintOpacityProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, double>(nameof(CardTintOpacity), o => o.CardTintOpacity);

    /// <summary>Margin of the artwork inside the slab (all sides).</summary>
    public static readonly DirectProperty<CoverFlowCard, Thickness> ArtworkMarginProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, Thickness>(nameof(ArtworkMargin), o => o.ArtworkMargin);

    /// <summary>Artwork corner radius: a touch tighter than the clip radius (inner − 4, so
    /// 14 on the default 20px card), the way the reference cover sits in its slab.</summary>
    public static readonly DirectProperty<CoverFlowCard, CornerRadius> ArtworkCornerRadiusProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, CornerRadius>(nameof(ArtworkCornerRadius), o => o.ArtworkCornerRadius);

    /// <summary>Space between the artwork and the slab edge — the visible glass margin
    /// that makes the card read as a slab with the cover set into it.</summary>
    public const double ArtworkInset = 10;

    /// <summary>Clip radius for the content inside the rim: CardCornerRadius minus RimThickness.</summary>
    public static readonly DirectProperty<CoverFlowCard, CornerRadius> InnerCornerRadiusProperty =
        AvaloniaProperty.RegisterDirect<CoverFlowCard, CornerRadius>(nameof(InnerCornerRadius), o => o.InnerCornerRadius);

    /// <summary>Width of the rim the Frame border draws. 1px at 12% white: barely visible,
    /// just defining the edge. Anything heavier read as a white outline — worst on the side
    /// cards, whose near edge the perspective enlarges (2px/70% then 1.5px/40% both did).
    /// The lit edge is the 1px top highlight inside the clip, not the rim.</summary>
    public const double RimThickness = 1;

    private double _titleMaxWidth = 300 + 2 * ArtworkInset - 28 - 30;
    private double _placeholderFontSize = 300 * 0.23;
    private double _titleFontSize = TitleFor(300);
    private double _artistFontSize = ArtistFor(300);
    private CornerRadius _innerCornerRadius = InnerFor(new CornerRadius(20));
    private CornerRadius _artworkCornerRadius = ArtFor(InnerFor(new CornerRadius(20)));
    private Thickness _artworkMargin = new(ArtworkInset);
    private double _cardTintOpacity = TintFor(18);

    private static double TintFor(double blur) => blur > 0 ? 0.50 : 0.72;

    /// <summary>How much tighter the artwork corners are than the clip radius.</summary>
    public const double ArtworkRadiusStep = 4;

    private static CornerRadius ArtFor(CornerRadius inner) => new(
        System.Math.Max(4, inner.TopLeft - ArtworkRadiusStep), System.Math.Max(4, inner.TopRight - ArtworkRadiusStep),
        System.Math.Max(4, inner.BottomRight - ArtworkRadiusStep), System.Math.Max(4, inner.BottomLeft - ArtworkRadiusStep));

    private static CornerRadius InnerFor(CornerRadius r) => new(
        System.Math.Max(0, r.TopLeft - RimThickness), System.Math.Max(0, r.TopRight - RimThickness),
        System.Math.Max(0, r.BottomRight - RimThickness), System.Math.Max(0, r.BottomLeft - RimThickness));

    private static double TitleFor(double size) => System.Math.Round(System.Math.Clamp(size * 0.07, 14, 24));
    private static double ArtistFor(double size) => System.Math.Round(System.Math.Clamp(size * 0.065, 13, 22));

    static CoverFlowCard()
    {
        ArtworkSizeProperty.Changed.AddClassHandler<CoverFlowCard>((c, _) => c.OnArtworkSizeChanged());
        CardCornerRadiusProperty.Changed.AddClassHandler<CoverFlowCard>((c, _) =>
        {
            c.InnerCornerRadius = InnerFor(c.CardCornerRadius);
            c.ArtworkCornerRadius = ArtFor(c.InnerCornerRadius);
        });
        CardBlurRadiusProperty.Changed.AddClassHandler<CoverFlowCard>((c, _) => c.CardTintOpacity = TintFor(c.CardBlurRadius));
    }

    public CoverFlowCard()
    {
        InitializeComponent();
    }

    public string? ArtworkPath { get => GetValue(ArtworkPathProperty); set => SetValue(ArtworkPathProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Artist { get => GetValue(ArtistProperty); set => SetValue(ArtistProperty, value); }
    public bool IsExplicit { get => GetValue(IsExplicitProperty); set => SetValue(IsExplicitProperty, value); }
    public double Dim { get => GetValue(DimProperty); set => SetValue(DimProperty, value); }
    public double ArtworkSize { get => GetValue(ArtworkSizeProperty); set => SetValue(ArtworkSizeProperty, value); }
    public CornerRadius CardCornerRadius { get => GetValue(CardCornerRadiusProperty); set => SetValue(CardCornerRadiusProperty, value); }
    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public double CardBlurRadius { get => GetValue(CardBlurRadiusProperty); set => SetValue(CardBlurRadiusProperty, value); }
    public object? Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public ICommand? ArtistCommand { get => GetValue(ArtistCommandProperty); set => SetValue(ArtistCommandProperty, value); }
    public string? TitleToolTip { get => GetValue(TitleToolTipProperty); set => SetValue(TitleToolTipProperty, value); }

    private void OnTitleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TitleClicked?.Invoke(this, EventArgs.Empty);
    }

    public double TitleMaxWidth
    {
        get => _titleMaxWidth;
        private set => SetAndRaise(TitleMaxWidthProperty, ref _titleMaxWidth, value);
    }

    public double PlaceholderFontSize
    {
        get => _placeholderFontSize;
        private set => SetAndRaise(PlaceholderFontSizeProperty, ref _placeholderFontSize, value);
    }

    public double TitleFontSize
    {
        get => _titleFontSize;
        private set => SetAndRaise(TitleFontSizeProperty, ref _titleFontSize, value);
    }

    public double ArtistFontSize
    {
        get => _artistFontSize;
        private set => SetAndRaise(ArtistFontSizeProperty, ref _artistFontSize, value);
    }

    public CornerRadius InnerCornerRadius
    {
        get => _innerCornerRadius;
        private set => SetAndRaise(InnerCornerRadiusProperty, ref _innerCornerRadius, value);
    }

    public double CardTintOpacity
    {
        get => _cardTintOpacity;
        private set => SetAndRaise(CardTintOpacityProperty, ref _cardTintOpacity, value);
    }

    public Thickness ArtworkMargin
    {
        get => _artworkMargin;
        private set => SetAndRaise(ArtworkMarginProperty, ref _artworkMargin, value);
    }

    public CornerRadius ArtworkCornerRadius
    {
        get => _artworkCornerRadius;
        private set => SetAndRaise(ArtworkCornerRadiusProperty, ref _artworkCornerRadius, value);
    }

    private void OnArtworkSizeChanged()
    {
        var size = ArtworkSize;
        // Caption spans the slab (artwork + inset each side); 14px padding each side, plus
        // room for the explicit badge beside the title.
        TitleMaxWidth = System.Math.Max(40, size + 2 * ArtworkInset - 28 - 30);
        PlaceholderFontSize = System.Math.Max(24, size * 0.23);
        TitleFontSize = TitleFor(size);
        ArtistFontSize = ArtistFor(size);
    }
}
