using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Noctis.Views;

public partial class CoverFlowView : UserControl
{
    /// <summary>Below this width the now-playing column no longer fits beside the pile
    /// and drops underneath it, centred.</summary>
    internal const double SideBySideMinWidth = 1040;

    private bool? _isStacked;

    public CoverFlowView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeBlur();
    }

    /// <summary>The stage's parent Grid insets the page by these (Margin="0,24,0,12"); the
    /// pile cancels them with negative margins so its cards are cut by the real page edge.</summary>
    private const double PageInsetTop = 24, PageInsetBottom = 12;

    /// <summary>Widest share of the page the pile square may take beside the text column.</summary>
    private const double PileMaxWidthShare = 0.62;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyStageLayout(e.NewSize.Width, e.NewSize.Height);
    }

    internal void ApplyStageLayout(double width) => ApplyStageLayout(width, double.NaN);

    /// <summary>Wide: pile left, text column right. Narrow: text under the pile.</summary>
    internal void ApplyStageLayout(double width, double height)
    {
        var stacked = width < SideBySideMinWidth;
        ApplyPileSize(width, height, stacked);

        if (stacked == _isStacked) return;
        _isStacked = stacked;

        if (stacked)
        {
            Grid.SetRow(InfoStack, 1);
            Grid.SetColumn(InfoStack, 0);
            Grid.SetRowSpan(InfoStack, 1);
            Grid.SetColumnSpan(InfoStack, 2);
            Grid.SetRowSpan(PileViewbox, 1);
            Grid.SetColumnSpan(PileViewbox, 2);
            PileViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            PileViewbox.Margin = new Thickness(0, -PageInsetTop, 0, 0);
            InfoStack.HorizontalAlignment = HorizontalAlignment.Center;
            InfoStack.Margin = new Thickness(24, 12, 24, 8);
        }
        else
        {
            Grid.SetRow(InfoStack, 0);
            Grid.SetColumn(InfoStack, 1);
            Grid.SetRowSpan(InfoStack, 2);
            Grid.SetColumnSpan(InfoStack, 1);
            Grid.SetRowSpan(PileViewbox, 2);
            Grid.SetColumnSpan(PileViewbox, 1);
            PileViewbox.HorizontalAlignment = HorizontalAlignment.Left;
            PileViewbox.Margin = new Thickness(0, -PageInsetTop, 0, -PageInsetBottom);
            InfoStack.HorizontalAlignment = HorizontalAlignment.Left;
            InfoStack.Margin = new Thickness(48, 0, 40, 0);
        }

        var align = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        TitleMarquee.HorizontalAlignment = align;
        ArtistLink.HorizontalAlignment = align;
        AlbumLink.HorizontalAlignment = align;
        UpNextStack.HorizontalAlignment = align;
    }

    /// <summary>
    /// The 1000×1000 design canvas must map onto a SQUARE so its right edge is the playing
    /// cover's right edge and its top/bottom are the page's. Beside the text the square is
    /// the full page height (insets cancelled), capped to a share of the width on very
    /// wide-and-short windows; stacked, it is whatever width allows.
    /// </summary>
    private void ApplyPileSize(double width, double height, bool stacked)
    {
        if (double.IsNaN(height) || height <= 0 || width <= 0)
        {
            PileViewbox.Width = double.NaN;
            PileViewbox.Height = double.NaN;
            return;
        }

        var side = stacked
            ? Math.Max(200, Math.Min(width - 32, height * 0.6))
            : Math.Max(200, Math.Min(height + PageInsetTop + PageInsetBottom, width * PileMaxWidthShare));
        PileViewbox.Width = side;
        PileViewbox.Height = side;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyThemeBlur();
    }

    private void ApplyThemeBlur()
    {
        var isLight = ActualThemeVariant == ThemeVariant.Light;

        if (BackgroundArt.Effect is BlurEffect blur)
            blur.Radius = isLight ? 20 : 40;

        BackgroundOverlay.Opacity = isLight ? 0.35 : 0.45;
    }
}
