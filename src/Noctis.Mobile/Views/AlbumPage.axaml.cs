using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class AlbumPage : UserControl
{
    /// <summary>The hero's share of the viewport height when the screen is wider than that
    /// (landscape): a square cover would fill the whole screen.</summary>
    private const double HeroMaxViewportShare = 0.62;

    /// <summary>The fade covers the cover's lower 45%: the title block sits on its last part.</summary>
    private const double HeroFadeShare = 0.45;

    /// <summary>Height of the floating button row below the status bar, and how far the scrim
    /// takes to fade in (<see cref="HeroChrome"/>'s, which the artist page shares).</summary>
    internal const double TopBarHeight = HeroChrome.TopBarHeight, ScrimRun = HeroChrome.ScrimRun;

    /// <summary>The description's folded height in lines (Apple shows two or three).</summary>
    internal const int DescriptionLines = 3;

    /// <summary>The full-bleed cover's chrome: status bar strip, fades, scrim, status bar icons.</summary>
    private readonly HeroChrome _chrome;

    /// <summary>The description's fold (shared with the artist page's biography).</summary>
    private readonly DescriptionFold _description;

    public AlbumPage()
    {
        InitializeComponent();
        _description = new DescriptionFold(DescriptionText, DescriptionClip, DescriptionFolded, DescriptionFoldedHead,
            DescriptionFoldedTail, DescriptionMore, DescriptionLines);
        _chrome = new HeroChrome(this, Hero, HeroFade, TopFade, TopScrim, TopBar, PageBody, "Album", UpdateScrollChrome, _description.SetMoreFade);
        AlbumScroll.SizeChanged += (_, _) => LayoutHero();
        AlbumScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        AlbumTitle.SizeChanged += (_, _) => UpdateScrollChrome();
        DataContextChanged += (_, _) => Attach();
        _chrome.ApplyTint();
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
        if (DataContext is not AlbumPageViewModel vm || VisualRoot == null) return;
        _chrome.Attach(vm.Shell, vm.Tint);
        _description.Measure();
    }

    private void Detach()
    {
        _chrome.Detach();
    }

    private void LayoutHero()
    {
        var width = AlbumScroll.Bounds.Width;
        if (width <= 0) return;
        var height = Math.Min(width, Math.Max(320, AlbumScroll.Bounds.Height * HeroMaxViewportShare));
        Hero.Height = height;
        HeroFade.Height = Math.Round(height * HeroFadeShare);
        _chrome.ApplyFades();   // the fade and the body's ramp follow the hero's size
        UpdateScrollChrome();
    }

    /// <summary>The scroll-driven chrome at the current offset; see <see cref="ScrollChrome"/>.
    /// Direct Opacity sets per scroll event (a repaint, no layout), no transition: a transition
    /// restarted on every frame stalls (repo trap).</summary>
    private void UpdateScrollChrome()
    {
        var titleTop = Hero.Height + TitleBlock.Margin.Top - AlbumScroll.Offset.Y;
        var chrome = ScrollChrome(titleTop, AlbumTitle.Bounds.Height, _chrome.BarBottom);
        _chrome.ApplyScrim(chrome.Scrim, AlbumScroll.Offset.Y + _chrome.BarBottom - Hero.Height);
        AlbumTitle.Opacity = chrome.BigTitle;
        SmallTitle.Opacity = chrome.SmallTitle;
    }

    /// <summary>See <see cref="HeroChrome.ScrollChrome"/>.</summary>
    internal static (double Scrim, double BigTitle, double SmallTitle) ScrollChrome(double titleTop, double titleHeight, double barBottom) =>
        HeroChrome.ScrollChrome(titleTop, titleHeight, barBottom);

    /// <summary>See <see cref="HeroChrome.TopFadeBrush"/>.</summary>
    internal static LinearGradientBrush TopFadeBrush(Color color, double alpha) => HeroChrome.TopFadeBrush(color, alpha);
}

/// <summary>
/// Cuts a line by character with no visible mark: the folded description's last line runs on
/// to the edge under MORE's fade, which stands in for the ellipsis.
/// </summary>
internal sealed class UnmarkedTrimming : TextTrimming
{
    public static readonly UnmarkedTrimming Instance = new();

    public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo) =>
        new TextTrailingCharacterEllipsis("​", createInfo.Width, createInfo.TextRunProperties, createInfo.FlowDirection);
}
