using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
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

    /// <summary>Height of the floating button row below the status bar (6 + 40 + 6), and how far
    /// the scrim takes to fade in as the big title's top comes up to the row's bottom.</summary>
    internal const double TopBarHeight = 52, ScrimRun = 80;

    /// <summary>How far below the button row the theme fade over the cover top runs out, and its
    /// opacity at the very top: enough that the theme's status bar icons read on a black cover in
    /// Light and a white one in Dark (~0.6 still under the bar's bottom edge).</summary>
    private const double TopFadeExtra = 40, TopFadeAlpha = 0.88;

    /// <summary>The scrim is solid to this far above the button row's bottom, then eases out
    /// over <see cref="ScrimTail"/> below it: without a blur, anything showing through behind
    /// the small title (a half-faded Play pill on the device, 2026-10-01) reads as a smudge.</summary>
    private const double ScrimSolidInset = 8, ScrimTail = 24;

    /// <summary>The description's folded height in lines (Apple shows two or three), and the
    /// unfold's length and curve (the phone's sheet curve).</summary>
    internal const int DescriptionLines = 3;
    private static readonly TimeSpan DescriptionDuration = TimeSpan.FromMilliseconds(320);
    private static readonly Avalonia.Animation.Easings.Easing DescriptionEase = new CubicBezierEase(0.32, 0.72, 0, 1);

    private AlbumPageViewModel? _vm;
    private ShellViewModel? _shell;
    private PageTint? _tint;

    /// <summary>The shell's page host while this page is in it, and its own clip setting.</summary>
    private ContentControl? _host;
    private bool _hostClipWasSet, _hostClip;

    /// <summary>The status bar icons last asked of the theme host; null = the theme's own.</summary>
    private bool? _statusIcons;

    /// <summary>The description's folded and unfolded clip heights for the current width, and
    /// whether it is long enough to fold at all.</summary>
    private double _descriptionFolded, _descriptionUnfolded;
    private bool _descriptionOverflows;
    private int _descriptionGeneration;

    public AlbumPage()
    {
        InitializeComponent();
        AlbumScroll.SizeChanged += (_, _) => LayoutHero();
        AlbumScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
        AlbumTitle.SizeChanged += (_, _) => UpdateScrollChrome();
        DescriptionText.SizeChanged += (_, _) => MeasureDescription();
        DescriptionLess.SizeChanged += (_, _) => MeasureDescription();
        ActualThemeVariantChanged += (_, _) => ApplyTint();
        DataContextChanged += (_, _) => Attach();
        ApplyTint();
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
        _shell = vm.Shell;
        _tint = vm.Tint;
        _shell.PropertyChanged += OnShellChanged;
        _tint.PropertyChanged += OnTintChanged;
        _vm.PropertyChanged += OnPageChanged;
        UnclipHost();
        ApplySafeArea();
        ApplyTint();
        MeasureDescription();
    }

    private void Detach()
    {
        SetStatusIcons(null);
        RestoreHostClip();
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        if (_tint != null) _tint.PropertyChanged -= OnTintChanged;
        if (_vm != null) _vm.PropertyChanged -= OnPageChanged;
        _descriptionGeneration++;   // drops a running unfold
        _vm = null;
        _shell = null;
        _tint = null;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SafeArea)) ApplySafeArea();
        else if (e.PropertyName is nameof(ShellViewModel.IsNowPlayingOpen) or nameof(ShellViewModel.IsLyricsOpen)
                 or nameof(ShellViewModel.IsQueueOpen)) UpdateStatusBar();
    }

    private void OnTintChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageTint.TintColor) or nameof(PageTint.HasTint) or nameof(PageTint.IsLight)) ApplyTint();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumPageViewModel.IsDescriptionExpanded)) UnfoldDescription(animate: true);
    }

    private double SafeTop => _shell?.SafeArea.Top ?? 0;

    /// <summary>
    /// ShellView pads the tab content by the status bar; the album page takes that strip back
    /// with a negative top margin so the cover runs under the clock, and puts its own buttons
    /// and fades below it instead. The shell is not edited for this: every other page keeps
    /// the padding.
    /// </summary>
    private void ApplySafeArea()
    {
        var top = SafeTop;
        Margin = new Thickness(0, -top, 0, 0);
        TopBar.Margin = new Thickness(16, top + 6, 16, 0);
        TopScrim.Height = top + TopBarHeight + ScrimTail;
        TopFade.Height = top + TopBarHeight + TopFadeExtra;
        ApplyTint();
        UpdateScrollChrome();
    }

    /// <summary>
    /// Avalonia 12 clips every TemplatedControl to its bounds by default
    /// (TemplatedControl's ClipToBoundsProperty.OverrideDefaultValue(true)), and the shell shows
    /// pages through a ContentControl laid out below the status bar: the cover's strip above
    /// it was cut off (device run, 2026-10-01: white band, 128 px). The page lifts that clip
    /// on its host while it is the host's content and puts it back when it leaves; the
    /// page itself still clips to its own (taller) bounds.
    /// </summary>
    private void UnclipHost()
    {
        if (Parent is not ContentControl host) return;
        _host = host;
        _hostClipWasSet = host.IsSet(ClipToBoundsProperty);
        _hostClip = host.ClipToBounds;
        host.ClipToBounds = false;
    }

    private void RestoreHostClip()
    {
        if (_host == null) return;
        if (_hostClipWasSet) _host.ClipToBounds = _hostClip;
        else _host.ClearValue(ClipToBoundsProperty);
        _host = null;
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
        var chrome = ScrollChrome(titleTop, AlbumTitle.Bounds.Height, SafeTop + TopBarHeight);
        TopScrim.Opacity = chrome.Scrim;
        TopFade.Opacity = 1 - chrome.Scrim;
        AlbumTitle.Opacity = chrome.BigTitle;
        SmallTitle.Opacity = chrome.SmallTitle;
        UpdateStatusBar();
    }

    /// <summary>
    /// Apple's scrolled header, from where the big title is: the page colour comes in behind
    /// the status bar and the buttons over the last <see cref="ScrimRun"/> before the title's
    /// top reaches the button row's bottom (<paramref name="barBottom"/>), so nothing runs under
    /// the clock; the big title then fades as it passes under the row, gone once its bottom has,
    /// and the small title between ‹ and … fades in over the second half of that. Positions
    /// are screen y, the page's top being the screen's.
    /// </summary>
    internal static (double Scrim, double BigTitle, double SmallTitle) ScrollChrome(double titleTop, double titleHeight, double barBottom)
    {
        static double Smooth(double t) => t * t * (3 - 2 * t);
        var scrim = Math.Clamp((barBottom + ScrimRun - titleTop) / ScrimRun, 0, 1);
        var under = (barBottom - titleTop) / Math.Max(1, titleHeight);
        var big = 1 - Smooth(Math.Clamp(under, 0, 1));
        var small = Smooth(Math.Clamp((under - 0.5) / 0.5, 0, 1));
        return (scrim, big, small);
    }

    /// <summary>
    /// The status bar icons: the theme's own (null) while the theme fade lies under the bar, the
    /// ones readable on the page colour (the text's contrast rule) once the scrim has taken over;
    /// the theme's again while Now Playing, Lyrics or the Queue covers the page and when the page
    /// leaves. Only a change reaches the platform.
    /// </summary>
    private void UpdateStatusBar()
    {
        bool? dark = null;
        if (_shell is { IsNowPlayingOpen: false, IsLyricsOpen: false, IsQueueOpen: false }
            && TopScrim.Opacity >= 0.5 && _tint?.TintColor is { } tint)
            dark = PageTint.PrefersDarkText(tint);
        SetStatusIcons(dark);
    }

    private void SetStatusIcons(bool? dark)
    {
        if (dark == _statusIcons) return;
        _statusIcons = dark;
        // The shell decides what reaches the platform (its overlays win over the page).
        if (_shell != null) _shell.PageStatusBarIcons = dark;
    }

    /// <summary>
    /// The tint's text colours as page resources (see the XAML's header comment), the fade
    /// from the cover into the page colour, the scrim, the description's MORE backing, and the
    /// theme fade over the cover top. Without a tint (no cover, or its colour not extracted yet)
    /// the page colour is the theme's background.
    /// </summary>
    private void ApplyTint()
    {
        var theme = this.TryFindResource("AppWindowBackgroundBrush", ActualThemeVariant, out var themed)
                    && themed is ISolidColorBrush surface ? surface.Color : (Color?)null;
        // The theme's surface; plain white or black where the theme dictionaries are not loaded.
        TopFade.Background = TopFadeBrush(theme ?? (ActualThemeVariant == ThemeVariant.Dark ? Colors.Black : Colors.White), TopFadeAlpha);
        if (_tint?.TintColor is { } tint)
        {
            var dark = PageTint.PrefersDarkText(tint);
            var text = dark ? Colors.Black : Colors.White;
            Resources["AlbumText"] = new SolidColorBrush(text);
            Resources["AlbumSubtleText"] = new SolidColorBrush(PageTint.SubtleTextOn(tint, text));
            Resources["AlbumWash"] = new SolidColorBrush(text, 0.12);
            Resources["AlbumRule"] = new SolidColorBrush(text, 0.12);
            Resources["AlbumPillText"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            SetFades(tint);
        }
        else SetFades(theme);
        UpdateStatusBar();
    }

    private void SetFades(Color? into)
    {
        HeroFade.Background = into is { } c ? Fade(c, from: 0, to: 1, rising: true) : null;
        // Solid behind the status bar and the buttons, eased out just below them.
        var solid = TopScrim.Height > 0 ? (SafeTop + TopBarHeight - ScrimSolidInset) / TopScrim.Height : 0;
        TopScrim.Background = into is { } s ? Fade(s, from: solid, to: 1, rising: false) : null;
        // MORE sits on the page colour, fading in over the text it covers.
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

    /// <summary>The theme's background at <paramref name="alpha"/> at the top, easing (smoothstep)
    /// to clear at the bottom: Apple's softened cover top.</summary>
    internal static LinearGradientBrush TopFadeBrush(Color color, double alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        const int steps = 8;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var a = alpha * (1 - t * t * (3 - 2 * t));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(a * 255), color.R, color.G, color.B), t));
        }
        return brush;
    }

    /// <summary>A smoothstep ramp of <paramref name="color"/>'s alpha between the relative
    /// offsets <paramref name="from"/> and <paramref name="to"/> (eased at both ends, so neither
    /// end of the fade shows as a line): rising to opaque, or falling from it.</summary>
    private static LinearGradientBrush Fade(Color color, double from, double to, bool rising)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        const int steps = 8;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var eased = t * t * (3 - 2 * t);
            var alpha = rising ? eased : 1 - eased;
            var offset = from + (to - from) * t;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B), offset));
        }
        if (!rising && from > 0)
            brush.GradientStops.Insert(0, new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 0));
        return brush;
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
