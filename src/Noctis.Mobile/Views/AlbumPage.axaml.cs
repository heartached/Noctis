using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    /// takes to fade in as the cover's end reaches it.</summary>
    private const double TopBarHeight = 52, ScrimRun = 80;

    private AlbumPageViewModel? _vm;
    private ShellViewModel? _shell;
    private PageTint? _tint;

    /// <summary>The shell's page host while this page is in it, and its own clip setting.</summary>
    private ContentControl? _host;
    private bool _hostClipWasSet, _hostClip;

    public AlbumPage()
    {
        InitializeComponent();
        AlbumScroll.SizeChanged += (_, _) => LayoutHero();
        AlbumScroll.ScrollChanged += (_, _) => UpdateScrollChrome();
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
        UnclipHost();
        ApplySafeArea();
        ApplyTint();
    }

    private void Detach()
    {
        RestoreHostClip();
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        if (_tint != null) _tint.PropertyChanged -= OnTintChanged;
        _vm = null;
        _shell = null;
        _tint = null;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SafeArea)) ApplySafeArea();
    }

    private void OnTintChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageTint.TintColor) or nameof(PageTint.HasTint) or nameof(PageTint.IsLight)) ApplyTint();
    }

    /// <summary>
    /// ShellView pads the tab content by the status bar; the album page takes that strip back
    /// with a negative top margin so the cover runs under the clock, and puts its own buttons
    /// and scrim below it instead. The shell is not edited for this: every other page keeps
    /// the padding.
    /// </summary>
    private void ApplySafeArea()
    {
        var top = _shell?.SafeArea.Top ?? 0;
        Margin = new Thickness(0, -top, 0, 0);
        TopBar.Margin = new Thickness(16, top + 6, 16, 0);
        TopScrim.Height = top + TopBarHeight + 12;
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

    /// <summary>Fades the scrim in over the last <see cref="ScrimRun"/> of the cover before its
    /// end passes under the button row. A direct Opacity per scroll event, no transition: a
    /// transition restarted on every frame stalls (repo trap).</summary>
    private void UpdateScrollChrome()
    {
        var top = _shell?.SafeArea.Top ?? 0;
        var coverEnd = Hero.Height - AlbumScroll.Offset.Y;
        var progress = Math.Clamp((top + TopBarHeight + ScrimRun - coverEnd) / ScrimRun, 0, 1);
        TopScrim.Opacity = progress;
    }

    /// <summary>
    /// The tint's text colours as page resources (see the XAML's header comment), the fade
    /// from the cover into the page colour, and the scrim. Without a tint (no cover, or its
    /// colour not extracted yet) the cover fades into the theme's background instead.
    /// </summary>
    private void ApplyTint()
    {
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
            return;
        }
        SetFades(this.TryFindResource("AppWindowBackgroundBrush", ActualThemeVariant, out var themed)
                 && themed is ISolidColorBrush surface ? surface.Color : null);
    }

    private void SetFades(Color? into)
    {
        HeroFade.Background = into is { } c ? Fade(c, from: 0, to: 1, rising: true) : null;
        // Solid behind the status bar, fading out across the button row.
        var solid = TopScrim.Height > 0 ? (_shell?.SafeArea.Top ?? 0) / TopScrim.Height : 0;
        TopScrim.Background = into is { } s ? Fade(s, from: solid, to: 1, rising: false) : null;
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
}
