using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The chrome of a page that opens on a full-bleed picture under the status bar (the album
/// cover, the artist photo), Apple Music style; shared by AlbumPage and ArtistPage, which each
/// own the XAML parts and the scroll position it reads. It:
/// <list type="bullet">
/// <item>lifts the page up under the status bar (a negative top margin of the shell's
/// SafeArea.Top) and lifts its host's clip while the page is shown (<see cref="UnclipHost"/>);</item>
/// <item>softens the picture's top with the theme's background (<c>TopFade</c>: white in Light,
/// dark in Dark), so the theme's status bar icons read on any picture;</item>
/// <item>fades the picture's bottom into the page colour (<c>HeroFade</c>, PageTint's hero mode);</item>
/// <item>brings the page colour in behind the status bar and the floating buttons as the page's
/// title scrolls up to them (<c>TopScrim</c>, <see cref="ScrollChrome"/>), and switches the status
/// bar icons to the ones readable on that colour;</item>
/// <item>keeps the tint's WCAG text colours as page resources ("&lt;prefix&gt;Text",
/// "&lt;prefix&gt;SubtleText", "&lt;prefix&gt;Wash", "&lt;prefix&gt;Rule", "&lt;prefix&gt;PillText")
/// for the page's styles.</item>
/// </list>
/// </summary>
internal sealed class HeroChrome
{
    /// <summary>Height of the floating button row below the status bar (6 + 40 + 6), and how far
    /// the scrim takes to fade in as the big title's top comes up to the row's bottom.</summary>
    public const double TopBarHeight = 52, ScrimRun = 80;

    /// <summary>How far below the button row the theme fade over the picture's top runs out, and
    /// its opacity at the very top: enough that the theme's status bar icons read on a black
    /// picture in Light and a white one in Dark (~0.6 still under the bar's bottom edge).</summary>
    private const double TopFadeExtra = 40, TopFadeAlpha = 0.88;

    /// <summary>The scrim is solid to this far above the button row's bottom, then eases out
    /// over <see cref="ScrimTail"/> below it: without a blur, anything showing through behind
    /// the small title (a half-faded Play pill on the device, 2026-10-01) reads as a smudge.</summary>
    private const double ScrimSolidInset = 8, ScrimTail = 24;

    private readonly UserControl _page;
    private readonly Border _heroFade, _topFade, _topScrim;
    private readonly Control _topBar;
    private readonly string _prefix;
    private readonly Action _layoutChanged;
    private readonly Action<Color?> _tintApplied;

    private ShellViewModel? _shell;
    private PageTint? _tint;

    /// <summary>The shell's page host while the page is in it, and its own clip setting.</summary>
    private ContentControl? _host;
    private bool _hostClipWasSet, _hostClip;

    /// <summary>The status bar icons last asked of the shell; null = the theme's own.</summary>
    private bool? _statusIcons;

    /// <param name="layoutChanged">The status bar inset changed: the page re-runs its scroll chrome.</param>
    /// <param name="tintApplied">The page colour (the tint, else the theme's surface) was applied:
    /// the page sets its own fades from it.</param>
    public HeroChrome(UserControl page, Border heroFade, Border topFade, Border topScrim, Control topBar, string resourcePrefix,
        Action layoutChanged, Action<Color?>? tintApplied = null)
    {
        _page = page;
        _heroFade = heroFade;
        _topFade = topFade;
        _topScrim = topScrim;
        _topBar = topBar;
        _prefix = resourcePrefix;
        _layoutChanged = layoutChanged;
        _tintApplied = tintApplied ?? (_ => { });
        page.ActualThemeVariantChanged += (_, _) => ApplyTint();
    }

    public double SafeTop => _shell?.SafeArea.Top ?? 0;

    /// <summary>The floating button row's bottom, in screen y (the page's top is the screen's).</summary>
    public double BarBottom => SafeTop + TopBarHeight;

    public void Attach(ShellViewModel shell, PageTint tint)
    {
        Detach();
        _shell = shell;
        _tint = tint;
        _shell.PropertyChanged += OnShellChanged;
        _tint.PropertyChanged += OnTintChanged;
        UnclipHost();
        ApplySafeArea();
    }

    public void Detach()
    {
        SetStatusIcons(null);
        RestoreHostClip();
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        if (_tint != null) _tint.PropertyChanged -= OnTintChanged;
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

    /// <summary>
    /// ShellView pads the tab content by the status bar; the page takes that strip back with a
    /// negative top margin so the picture runs under the clock, and puts its own buttons and
    /// fades below it instead. The shell is not edited for this: every other page keeps the padding.
    /// </summary>
    private void ApplySafeArea()
    {
        var top = SafeTop;
        _page.Margin = new Thickness(0, -top, 0, 0);
        _topBar.Margin = new Thickness(16, top + 6, 16, 0);
        _topScrim.Height = top + TopBarHeight + ScrimTail;
        _topFade.Height = top + TopBarHeight + TopFadeExtra;
        ApplyTint();
        _layoutChanged();
    }

    /// <summary>
    /// Avalonia 12 clips every TemplatedControl to its bounds by default
    /// (TemplatedControl's ClipToBoundsProperty.OverrideDefaultValue(true)), and the shell shows
    /// pages through a ContentControl laid out below the status bar: the picture's strip above
    /// it was cut off (device run, 2026-10-01: white band, 128 px). The page lifts that clip
    /// on its host while it is the host's content and puts it back when it leaves; the
    /// page itself still clips to its own (taller) bounds.
    /// </summary>
    private void UnclipHost()
    {
        if (_page.Parent is not ContentControl host) return;
        _host = host;
        _hostClipWasSet = host.IsSet(Visual.ClipToBoundsProperty);
        _hostClip = host.ClipToBounds;
        host.ClipToBounds = false;
    }

    private void RestoreHostClip()
    {
        if (_host == null) return;
        if (_hostClipWasSet) _host.ClipToBounds = _hostClip;
        else _host.ClearValue(Visual.ClipToBoundsProperty);
        _host = null;
    }

    /// <summary>The scrim at <paramref name="scrim"/> (0 … 1) and the theme fade handing over to
    /// it, then the status bar icons for what lies under the bar. Direct Opacity sets per scroll
    /// event (a repaint, no layout), no transition: a transition restarted on every frame stalls
    /// (repo trap).</summary>
    public void ApplyScrim(double scrim)
    {
        _topScrim.Opacity = scrim;
        _topFade.Opacity = 1 - scrim;
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
    public static (double Scrim, double BigTitle, double SmallTitle) ScrollChrome(double titleTop, double titleHeight, double barBottom)
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
            && _topScrim.Opacity >= 0.5 && _tint?.TintColor is { } tint)
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
    /// The tint's text colours as page resources, the fade from the picture into the page
    /// colour, the scrim, and the theme fade over the picture's top. Without a tint (no picture,
    /// or its colour not extracted yet) the page colour is the theme's background.
    /// </summary>
    public void ApplyTint()
    {
        var theme = _page.TryFindResource("AppWindowBackgroundBrush", _page.ActualThemeVariant, out var themed)
                    && themed is ISolidColorBrush surface ? surface.Color : (Color?)null;
        // The theme's surface; plain white or black where the theme dictionaries are not loaded.
        _topFade.Background = TopFadeBrush(theme ?? (_page.ActualThemeVariant == ThemeVariant.Dark ? Colors.Black : Colors.White), TopFadeAlpha);
        Color? into = theme;
        if (_tint?.TintColor is { } tint)
        {
            var dark = PageTint.PrefersDarkText(tint);
            var text = dark ? Colors.Black : Colors.White;
            _page.Resources[_prefix + "Text"] = new SolidColorBrush(text);
            _page.Resources[_prefix + "SubtleText"] = new SolidColorBrush(PageTint.SubtleTextOn(tint, text));
            _page.Resources[_prefix + "Wash"] = new SolidColorBrush(text, 0.12);
            _page.Resources[_prefix + "Rule"] = new SolidColorBrush(text, 0.12);
            _page.Resources[_prefix + "PillText"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            into = tint;
        }
        _heroFade.Background = into is { } c ? Fade(c, from: 0, to: 1, rising: true) : null;
        // Solid behind the status bar and the buttons, eased out just below them.
        var solid = _topScrim.Height > 0 ? (SafeTop + TopBarHeight - ScrimSolidInset) / _topScrim.Height : 0;
        _topScrim.Background = into is { } s ? Fade(s, from: solid, to: 1, rising: false) : null;
        _tintApplied(into);
        UpdateStatusBar();
    }

    /// <summary>The theme's background at <paramref name="alpha"/> at the top, easing (smoothstep)
    /// to clear at the bottom: Apple's softened picture top.</summary>
    public static LinearGradientBrush TopFadeBrush(Color color, double alpha)
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
    public static LinearGradientBrush Fade(Color color, double from, double to, bool rising)
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
}
