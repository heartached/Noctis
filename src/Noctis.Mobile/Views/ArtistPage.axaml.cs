using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The artist page's chrome: the album page's (<see cref="HeroChrome"/>: the photo under the
/// status bar, its fades, the scroll-edge scrim and the small title), the two shades of the page
/// colour for the round buttons and the cards, and the widths that make the carousel and the Top
/// Songs grid page sideways with the next card or column peeking in. With the artist's own logo
/// in the name's place it sizes the logo (<see cref="LogoSize"/>), keeps its ink readable on the
/// page with a halo rather than by recolouring it (<see cref="LogoHalo"/>), and gives the Play
/// button the logo's accent when it has one (<see cref="PlayColours"/>).
/// </summary>
public partial class ArtistPage : UserControl
{
    /// <summary>The hero's share of the viewport height when the screen is wider than that
    /// (landscape): a square photo would fill the whole screen.</summary>
    private const double HeroMaxViewportShare = 0.62;

    /// <summary>The fade covers the photo's lower half: the name and the buttons sit on it.</summary>
    internal const double HeroFadeShare = 0.5;

    /// <summary>The side inset of the carousel and the grid, the gap between their pages (no
    /// smaller than the inset, or the previous page's edge shows at the left once snapped), and
    /// how much of the next card or column shows at the right edge.</summary>
    public const double PageInset = 16, CardGap = 16, CardPeek = 44, ColumnGap = 16, ColumnPeek = 30;

    /// <summary>The gap after each Top Songs column, carried by the column itself (its panel
    /// virtualises and has no Spacing).</summary>
    public static readonly Thickness ColumnMargin = new(0, 0, ColumnGap, 0);

    /// <summary>How far the round buttons' and the cards' fills move from the page colour
    /// (HSL lightness; see <see cref="Shade"/>).</summary>
    internal const double ButtonShade = 0.1, CardShade = 0.07;

    /// <summary>The logo's height when its width allows (Apple's artist logos stand ~85–95 pt
    /// tall and ~135–205 pt wide on a 430 pt screen, 2026-10-20 references), the tallest a narrow
    /// logo may stand, and the widest a long one may run: a share of the page, never more than a
    /// fixed width.</summary>
    internal const double LogoHeight = 84, LogoMaxHeight = 104, LogoWidthShare = 0.66, LogoMaxWidth = 300;

    /// <summary>How far a logo may be blown up past its own pixels: TheAudioDB's older logos are
    /// 400 px wide, which drawn 300 dp wide on a 2.6x screen went soft (emulator, 2026-10-20).</summary>
    internal const double LogoMaxUpscale = 1.35;

    /// <summary>Below this share of its ink reading on the page (3:1), a logo gets a halo.</summary>
    internal const double LogoLegibleShare = 0.5;

    /// <summary>The least contrast an accent Play button needs against the page; less, and it
    /// takes the page's text colour as without a logo.</summary>
    internal const double AccentMinContrast = 1.8;

    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<ArtistPage, double>(nameof(CardWidth), 320);

    public static readonly StyledProperty<double> TopSongPageWidthProperty =
        AvaloniaProperty.Register<ArtistPage, double>(nameof(TopSongPageWidth), 340);

    private readonly HeroChrome _chrome;
    private ArtistPageViewModel? _vm;

    public ArtistPage()
    {
        InitializeComponent();
        _chrome = new HeroChrome(this, Hero, HeroFade, TopFade, TopScrim, TopBar, PageBody, "Artist", UpdateScrollChrome, ApplyShades);
        ArtistScroll.SizeChanged += (_, _) => LayoutPage();
        ArtistScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        TitleArt.SizeChanged += (_, _) => LayoutTitle();
        ButtonRow.SizeChanged += (_, _) => LayoutTitle();
        DataContextChanged += (_, _) => Attach();
        _chrome.ApplyTint();
    }

    /// <summary>A carousel card's width: the page less its insets, and less the next card's
    /// peek when there is a next card.</summary>
    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>A Top Songs column's width: the page less its inset and the next column's peek.</summary>
    public double TopSongPageWidth
    {
        get => GetValue(TopSongPageWidthProperty);
        set => SetValue(TopSongPageWidthProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not ArtistPageViewModel vm || VisualRoot == null) return;
        _vm = vm;
        _vm.PropertyChanged += OnPageChanged;
        _chrome.Attach(vm.Shell, vm.Tint);
        LayoutPage();
    }

    private void Detach()
    {
        _chrome.Detach();
        if (_vm != null) _vm.PropertyChanged -= OnPageChanged;
        _vm = null;
    }

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtistPageViewModel.Cards)) LayoutPage();
        else if (e.PropertyName == nameof(ArtistPageViewModel.Logo))
        {
            LayoutLogo();
            ApplyShades(null);
        }
    }

    /// <summary>
    /// The logo's size on a page <paramref name="pageWidth"/> wide: <see cref="LogoHeight"/> tall
    /// with its width following its shape, a long logo narrowed to the page's
    /// <see cref="LogoWidthShare"/> (at most <see cref="LogoMaxWidth"/>) and a narrow one standing
    /// no taller than <see cref="LogoMaxHeight"/>, so a wordmark and a monogram read alike; and
    /// never wider than <paramref name="sharpWidth"/> (its own pixels at
    /// <see cref="LogoMaxUpscale"/>), so a small logo stays sharp.
    /// </summary>
    internal static Size LogoSize(double aspect, double pageWidth, double sharpWidth = double.PositiveInfinity)
    {
        aspect = Math.Clamp(aspect, 0.2, 20);
        var maxWidth = Math.Min(Math.Min(LogoMaxWidth, sharpWidth), Math.Max(120, pageWidth * LogoWidthShare));
        var height = aspect < 1 ? LogoMaxHeight : LogoHeight;
        var width = height * aspect;
        if (width > maxWidth)
        {
            width = maxWidth;
            height = width / aspect;
        }
        return new Size(Math.Round(width), Math.Round(height));
    }

    /// <summary>
    /// What keeps a logo readable on <paramref name="background"/> without changing its colours
    /// (TheAudioDB's terms: logos as they are): when less than <see cref="LogoLegibleShare"/> of
    /// its ink reads there, a soft halo in the opposite tone (dark under a white logo on a light
    /// page, light under a black one on a deep page); otherwise, under light ink, the faint lift
    /// Apple's white logos have over the photo; nothing under dark ink on a light page.
    /// </summary>
    internal static DropShadowEffect? LogoHalo(ArtistLogoInk ink, Color background)
    {
        if (ink.LegibleShare(background) < LogoLegibleShare)
            return new DropShadowEffect
            {
                OffsetX = 0, OffsetY = 0, BlurRadius = 16,
                Color = ink.IsLight ? Colors.Black : Colors.White,
                Opacity = ink.IsLight ? 0.6 : 0.7,
            };
        return ink.IsLight
            ? new DropShadowEffect { OffsetX = 0, OffsetY = 1, BlurRadius = 10, Color = Colors.Black, Opacity = 0.28 }
            : null;
    }

    /// <summary>
    /// Play's fill and glyph: the logo's accent on a page it stands out from (Apple's Doja Cat
    /// page: a blue Play under the blue logo), else the page's text colour (Apple's white Play on
    /// a deep page), the glyph black or white, whichever reads on the fill.
    /// </summary>
    internal static (Color Fill, Color Glyph) PlayColours(Color page, Color text, Color? accent)
    {
        var fill = accent is { } a && PageTint.ContrastRatio(a, page) >= AccentMinContrast ? a : text;
        return (fill, PageTint.PrefersDarkText(fill) ? Colors.Black : Colors.White);
    }

    /// <summary>The logo at <see cref="LogoSize"/> for the page's width, with its halo.</summary>
    private void LayoutLogo()
    {
        if (_vm?.Logo is not { } logo)
        {
            ArtistLogo.Effect = null;
            return;
        }
        var width = ArtistScroll.Bounds.Width > 0 ? ArtistScroll.Bounds.Width : Bounds.Width;
        if (width <= 0) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = LogoSize(logo.Ink.Aspect, width, logo.Ink.Width * LogoMaxUpscale / Math.Max(1, scaling));
        ArtistLogo.Width = size.Width;
        ArtistLogo.Height = size.Height;
        ArtistLogo.Effect = LogoHalo(logo.Ink, LogoBackground());
    }

    /// <summary>What lies behind the logo: the photo's foot (the colour its fade starts from),
    /// else the theme's page.</summary>
    private Color LogoBackground()
    {
        if (_vm?.Tint.CoverColor is { } cover) return cover;
        return this.TryFindResource("AppWindowBackgroundBrush", ActualThemeVariant, out var b) && b is ISolidColorBrush s
            ? s.Color
            : ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Colors.Black : Colors.White;
    }

    /// <summary>
    /// The square hero (the album cover's rule), its fade, and the carousel's and the grid's
    /// page widths for the current width. Each list ends in room for one more peek, so its last
    /// page snaps to the left inset like the others instead of stopping short against the right
    /// edge with the page before it showing at the left.
    /// </summary>
    private void LayoutPage()
    {
        var width = ArtistScroll.Bounds.Width;
        if (width <= 0) return;
        var height = Math.Min(width, Math.Max(320, ArtistScroll.Bounds.Height * HeroMaxViewportShare));
        Hero.Height = height;
        HeroFade.Height = Math.Round(height * HeroFadeShare);
        _chrome.ApplyFades();   // the fade and the body's ramp follow the hero's size
        var cards = _vm?.Cards.Count ?? 0;
        CardWidth = cards > 1 ? width - PageInset - CardGap - CardPeek : width - 2 * PageInset;
        CardList.Margin = new Thickness(PageInset, 0, cards > 1 ? CardGap + CardPeek : PageInset, 0);
        TopSongPageWidth = width - PageInset - ColumnGap - ColumnPeek;
        // The last column's own right margin is the gap; the peek's room comes after it.
        TopSongPages.Margin = new Thickness(PageInset, 0, ColumnPeek, 0);
        LayoutLogo();
        LayoutTitle();
    }

    /// <summary>
    /// The name and the buttons sit on the photo's faded lower part: the block is pulled up by
    /// exactly its content's height (the button row's bottom margin is the gap to the photo's
    /// end), so it takes no room of its own below the hero. Measured from the children, not the
    /// block: a block pulled up further than its height is arranged taller than its content,
    /// and its own size would feed back into the margin.
    /// </summary>
    private void LayoutTitle()
    {
        var content = TitleArt.Bounds.Height + ButtonRow.Margin.Top + ButtonRow.Bounds.Height + ButtonRow.Margin.Bottom;
        if (TitleArt.Bounds.Height <= 0) return;
        var top = -Math.Round(content);
        if (Math.Abs(TitleBlock.Margin.Top - top) > 0.5) TitleBlock.Margin = new Thickness(20, top, 20, 0);
        UpdateScrollChrome();
    }

    /// <summary>The scroll-driven chrome at the current offset (<see cref="HeroChrome.ScrollChrome"/>,
    /// the album page's): direct Opacity sets per scroll event, no transition.</summary>
    private void UpdateScrollChrome()
    {
        var titleTop = Hero.Height + TitleBlock.Margin.Top - ArtistScroll.Offset.Y;
        var chrome = HeroChrome.ScrollChrome(titleTop, TitleArt.Bounds.Height, _chrome.BarBottom);
        _chrome.ApplyScrim(chrome.Scrim, ArtistScroll.Offset.Y + _chrome.BarBottom - Hero.Height);
        TitleArt.Opacity = chrome.BigTitle;
        SmallTitle.Opacity = chrome.SmallTitle;
    }

    /// <summary>
    /// The two shades of the page colour: the round buttons a step toward the text colour's side
    /// (lighter on a dark page, deeper on a light one, like Apple's red buttons on a red page),
    /// the cards a lighter step (a deeper one on a near-white page, where lighter would vanish).
    /// Without a tint the styles keep the theme's fills. Then Play's colours
    /// (<see cref="PlayColours"/>) and the logo's halo, which both follow the page colour.
    /// </summary>
    private void ApplyShades(Color? _)
    {
        var vm = DataContext as ArtistPageViewModel;
        if (vm is { Tint.TintColor: { } tint })
        {
            var lightPage = PageTint.PrefersDarkText(tint);
            Resources["ArtistButtonFill"] = new SolidColorBrush(Shade(tint, lightPage ? -ButtonShade : ButtonShade));
            Resources["ArtistCardFill"] = new SolidColorBrush(Shade(tint, Lightness(tint) > 0.88 ? -CardShade : CardShade));
        }
        var page = vm?.Tint.TintColor ?? LogoBackground();
        var text = vm?.Tint.TintColor is { } t
            ? (PageTint.PrefersDarkText(t) ? Colors.Black : Colors.White)
            : this.TryFindResource("PrimaryTextBrush", ActualThemeVariant, out var p) && p is ISolidColorBrush primary
                ? primary.Color
                : (PageTint.PrefersDarkText(page) ? Colors.Black : Colors.White);
        var (fill, glyph) = PlayColours(page, text, vm?.Logo?.Ink.Accent);
        Resources["ArtistPlayFill"] = new SolidColorBrush(fill);
        Resources["ArtistPlayGlyph"] = new SolidColorBrush(glyph);
        LayoutLogo();
    }

    /// <summary><paramref name="color"/> with its HSL lightness moved by <paramref name="amount"/>
    /// (clamped), hue and saturation kept: a deeper or lighter shade of the same colour.</summary>
    internal static Color Shade(Color color, double amount)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(h, s, Math.Clamp(l + amount, 0, 1));
    }

    internal static double Lightness(Color color) => ToHsl(color).L;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        byte Channel(double t) => (byte)Math.Round(Math.Clamp(Hue(p2, q2, t), 0, 1) * 255);
        return Color.FromRgb(Channel(h + 1.0 / 3), Channel(h), Channel(h - 1.0 / 3));
    }
}
