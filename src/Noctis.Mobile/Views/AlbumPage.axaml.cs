using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    /// <summary>The description's folded height in lines (Apple shows two or three), and the
    /// unfold's length and curve (the phone's sheet curve).</summary>
    internal const int DescriptionLines = 3;
    private static readonly TimeSpan DescriptionDuration = TimeSpan.FromMilliseconds(320);
    private static readonly Avalonia.Animation.Easings.Easing DescriptionEase = new CubicBezierEase(0.32, 0.72, 0, 1);

    private AlbumPageViewModel? _vm;

    /// <summary>The full-bleed cover's chrome: status bar strip, fades, scrim, status bar icons.</summary>
    private readonly HeroChrome _chrome;

    /// <summary>The description's folded and unfolded clip heights for the current width, and
    /// whether it is long enough to fold at all.</summary>
    private double _descriptionFolded, _descriptionUnfolded;
    private bool _descriptionOverflows;
    private int _descriptionGeneration;

    public AlbumPage()
    {
        InitializeComponent();
        _chrome = new HeroChrome(this, HeroFade, TopFade, TopScrim, TopBar, "Album", UpdateScrollChrome, SetDescriptionMoreFade);
        AlbumScroll.SizeChanged += (_, _) => LayoutHero();
        AlbumScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        AlbumTitle.SizeChanged += (_, _) => UpdateScrollChrome();
        DescriptionText.SizeChanged += (_, _) => MeasureDescription();
        DescriptionLess.SizeChanged += (_, _) => MeasureDescription();
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
        _vm = vm;
        _vm.PropertyChanged += OnPageChanged;
        _chrome.Attach(vm.Shell, vm.Tint);
        MeasureDescription();
    }

    private void Detach()
    {
        _chrome.Detach();
        if (_vm != null) _vm.PropertyChanged -= OnPageChanged;
        _descriptionGeneration++;   // drops a running unfold
        _vm = null;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumPageViewModel.IsDescriptionExpanded)) UnfoldDescription(animate: true);
    }

    private void LayoutHero()
    {
        var width = AlbumScroll.Bounds.Width;
        if (width <= 0) return;
        var height = Math.Min(width, Math.Max(320, AlbumScroll.Bounds.Height * HeroMaxViewportShare));
        Hero.Height = height;
        HeroFade.Height = Math.Round(height * HeroFadeShare);
        UpdateScrollChrome();
    }

    /// <summary>The scroll-driven chrome at the current offset; see <see cref="ScrollChrome"/>.
    /// Direct Opacity sets per scroll event (a repaint, no layout), no transition: a transition
    /// restarted on every frame stalls (repo trap).</summary>
    private void UpdateScrollChrome()
    {
        var titleTop = Hero.Height + TitleBlock.Margin.Top - AlbumScroll.Offset.Y;
        var chrome = ScrollChrome(titleTop, AlbumTitle.Bounds.Height, _chrome.BarBottom);
        _chrome.ApplyScrim(chrome.Scrim);
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
    /// The description's two heights at the width it is laid out at (the text block is arranged
    /// at its full height inside the clip, so its bounds give the unfolded one): folded,
    /// <see cref="DescriptionLines"/> lines; unfolded, the whole text plus the LESS line. A text
    /// that fits folded shows whole, without MORE or LESS. Runs on each re-wrap (width, text).
    /// </summary>
    private void MeasureDescription()
    {
        var text = DescriptionText.Bounds.Height;
        if (text <= 0) return;
        var lineHeight = DescriptionText.LineHeight > 0 ? DescriptionText.LineHeight : DescriptionText.FontSize * 1.3;
        _descriptionFolded = Math.Min(text, lineHeight * DescriptionLines);
        _descriptionOverflows = text > _descriptionFolded + 0.5;
        _descriptionUnfolded = text + (_descriptionOverflows ? DescriptionLess.Bounds.Height + DescriptionLess.Margin.Top : 0);
        UnfoldDescription(animate: false);
    }

    /// <summary>
    /// Folds or unfolds the description to the page's state: the clip's height eased from where
    /// it is (a tap mid-way turns it round) on the frame clock, MORE fading out and LESS in as it
    /// opens. The height is set per frame only during the 320 ms after a tap, never on scroll.
    /// </summary>
    private void UnfoldDescription(bool animate)
    {
        var generation = ++_descriptionGeneration;
        var open = _vm?.IsDescriptionExpanded == true && _descriptionOverflows;
        var target = open ? _descriptionUnfolded : _descriptionFolded;
        if (target <= 0) return;
        var from = double.IsNaN(DescriptionClip.Height) ? target : DescriptionClip.Height;
        var fromReveal = DescriptionLess.Opacity;
        var toReveal = open ? 1.0 : 0.0;

        void Set(double height, double reveal)
        {
            DescriptionClip.Height = height;
            DescriptionLess.Opacity = _descriptionOverflows ? reveal : 0;
            DescriptionMore.Opacity = _descriptionOverflows ? 1 - reveal : 0;
            DescriptionMore.IsVisible = _descriptionOverflows && reveal < 1;
        }

        if (!animate || TopLevel.GetTopLevel(this) is not { } top || Math.Abs(from - target) < 0.5)
        {
            Set(target, toReveal);
            return;
        }

        // Timed from the tap, not from the first frame: a first frame that changed nothing (t = 0)
        // left the render loop idle under the headless harness, and the unfold never ran.
        var start = Stopwatch.GetTimestamp();
        void Frame(TimeSpan _)
        {
            if (generation != _descriptionGeneration) return;
            var t = Math.Clamp(Stopwatch.GetElapsedTime(start) / DescriptionDuration, 0, 1);
            var e = DescriptionEase.Ease(t);
            Set(from + (target - from) * e, fromReveal + (toReveal - fromReveal) * e);
            if (t < 1) top.RequestAnimationFrame(Frame);
        }
        top.RequestAnimationFrame(Frame);
    }
}
