using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The artist page's chrome: the album page's (<see cref="HeroChrome"/>: the photo under the
/// status bar, its fades, the scroll-edge scrim and the small title), the two shades of the page
/// colour for the round buttons and the cards, and the widths that make the carousel and the Top
/// Songs grid page sideways with the next card or column peeking in.
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

    /// <summary>How far the round buttons' and the cards' fills move from the page colour
    /// (HSL lightness; see <see cref="Shade"/>).</summary>
    internal const double ButtonShade = 0.1, CardShade = 0.07;

    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<ArtistPage, double>(nameof(CardWidth), 320);

    public static readonly StyledProperty<double> TopSongPageWidthProperty =
        AvaloniaProperty.Register<ArtistPage, double>(nameof(TopSongPageWidth), 340);

    private readonly HeroChrome _chrome;
    private ArtistPageViewModel? _vm;

    public ArtistPage()
    {
        InitializeComponent();
        _chrome = new HeroChrome(this, HeroFade, TopFade, TopScrim, TopBar, "Artist", UpdateScrollChrome, ApplyShades);
        ArtistScroll.SizeChanged += (_, _) => LayoutPage();
        ArtistScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        ArtistName.SizeChanged += (_, _) => LayoutTitle();
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
        var cards = _vm?.Cards.Count ?? 0;
        CardWidth = cards > 1 ? width - PageInset - CardGap - CardPeek : width - 2 * PageInset;
        CardList.Margin = new Thickness(PageInset, 0, cards > 1 ? CardGap + CardPeek : PageInset, 0);
        TopSongPageWidth = width - PageInset - ColumnGap - ColumnPeek;
        TopSongPages.Margin = new Thickness(PageInset, 0, ColumnGap + ColumnPeek, 0);
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
        var content = ArtistName.Bounds.Height + ButtonRow.Margin.Top + ButtonRow.Bounds.Height + ButtonRow.Margin.Bottom;
        if (ArtistName.Bounds.Height <= 0) return;
        var top = -Math.Round(content);
        if (Math.Abs(TitleBlock.Margin.Top - top) > 0.5) TitleBlock.Margin = new Thickness(20, top, 20, 0);
        UpdateScrollChrome();
    }

    /// <summary>The scroll-driven chrome at the current offset (<see cref="HeroChrome.ScrollChrome"/>,
    /// the album page's): direct Opacity sets per scroll event, no transition.</summary>
    private void UpdateScrollChrome()
    {
        var titleTop = Hero.Height + TitleBlock.Margin.Top - ArtistScroll.Offset.Y;
        var chrome = HeroChrome.ScrollChrome(titleTop, ArtistName.Bounds.Height, _chrome.BarBottom);
        _chrome.ApplyScrim(chrome.Scrim);
        ArtistName.Opacity = chrome.BigTitle;
        SmallTitle.Opacity = chrome.SmallTitle;
    }

    /// <summary>
    /// The two shades of the page colour: the round buttons a step toward the text colour's side
    /// (lighter on a dark page, deeper on a light one, like Apple's red buttons on a red page),
    /// the cards a lighter step (a deeper one on a near-white page, where lighter would vanish).
    /// Without a tint the styles keep the theme's fills.
    /// </summary>
    private void ApplyShades(Color? _)
    {
        if (DataContext is not ArtistPageViewModel { Tint.TintColor: { } tint }) return;
        var lightPage = PageTint.PrefersDarkText(tint);
        Resources["ArtistButtonFill"] = new SolidColorBrush(Shade(tint, lightPage ? -ButtonShade : ButtonShade));
        Resources["ArtistCardFill"] = new SolidColorBrush(Shade(tint, Lightness(tint) > 0.88 ? -CardShade : CardShade));
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
