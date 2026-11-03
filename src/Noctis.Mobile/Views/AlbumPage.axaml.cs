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

    /// <summary>Whether the description is long enough to fold at the current width.</summary>
    private bool _descriptionOverflows;

    public AlbumPage()
    {
        InitializeComponent();
        _chrome = new HeroChrome(this, Hero, HeroFade, TopFade, TopScrim, TopBar, PageBody, "Album", UpdateScrollChrome, SetDescriptionMoreFade);
        AlbumScroll.SizeChanged += (_, _) => LayoutHero();
        AlbumScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        AlbumTitle.SizeChanged += (_, _) => UpdateScrollChrome();
        DescriptionText.SizeChanged += (_, _) => MeasureDescription();
        DescriptionFoldedTail.TextTrimming = UnmarkedTrimming.Instance;
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
        MeasureDescription();
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

    /// <summary>MORE sits on the page colour, fading in over the text it covers.</summary>
    private void SetDescriptionMoreFade(Color? into)
    {
        DescriptionMore.Background = into is { } m
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, m.R, m.G, m.B), 0),
                    new GradientStop(Color.FromArgb(255, m.R, m.G, m.B), 0.42),
                },
            }
            : null;
    }

    // ---- Description --------------------------------------------------------------------------

    /// <summary>
    /// The description's folded height at the width it is laid out at (the text block is arranged
    /// at its full height inside the clip): <see cref="DescriptionLines"/> lines with MORE over the
    /// end of the last, the whole text on the glass sheet on a tap. A text that fits shows whole,
    /// without MORE. Runs on each re-wrap (width, text).
    /// </summary>
    private void MeasureDescription()
    {
        var text = DescriptionText.Bounds.Height;
        if (text <= 0) return;
        var lineHeight = DescriptionText.LineHeight > 0 ? DescriptionText.LineHeight : DescriptionText.FontSize * 1.3;
        var folded = Math.Min(text, lineHeight * DescriptionLines);
        _descriptionOverflows = text > folded + 0.5;
        SplitFoldedDescription();
        DescriptionClip.Height = folded;
        DescriptionFolded.IsVisible = _descriptionOverflows;
        DescriptionText.Opacity = _descriptionOverflows ? 0 : 1;
        DescriptionMore.IsVisible = _descriptionOverflows;
    }

    /// <summary>
    /// The folded stand-in: the lines before the last folded one exactly as the full text wraps
    /// them, then everything after as one unwrapped line cut at the edge, so it runs on under
    /// MORE rather than stopping where the next word wrapped away.
    /// </summary>
    private void SplitFoldedDescription()
    {
        var full = DescriptionText.Text ?? "";
        var lines = DescriptionText.TextLayout.TextLines;
        if (!_descriptionOverflows || lines.Count < DescriptionLines)
        {
            DescriptionFoldedHead.Text = full;
            DescriptionFoldedTail.Text = null;
            return;
        }
        var split = Math.Clamp(lines[DescriptionLines - 1].FirstTextSourceIndex, 0, full.Length);
        DescriptionFoldedHead.Text = full[..split].TrimEnd();
        DescriptionFoldedHead.IsVisible = split > 0;
        DescriptionFoldedTail.Text = full[split..].Replace('\n', ' ');
    }
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
