using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// A soft edge made of the page's own background: the wash (the blurred cover stretched over
/// the page) and its shade, repainted over a band of the page exactly where they lie and faded
/// from clear to solid, so lines scrolling under it dissolve into the background instead of
/// being cut, the way Apple Music's lyrics fade under the header and the controls.
///
/// The fade is an OpacityMask on this band only. A mask over the whole lyric scroller made
/// every animated frame render the lyric area off-screen (~98% janky frames on the emulator,
/// LyricsPage.axaml); a band this size measured the same as no band (~15-17%). (Thin strips
/// of brush opacity instead of a mask drew the cover misplaced inside each strip.)
/// </summary>
internal sealed class WashFade : Panel
{
    private readonly Border _wash = new() { IsHitTestVisible = false };
    private readonly Border _shade = new() { IsHitTestVisible = false };
    private IImageBrushSource? _art;
    private Color _plain;
    private Rect _band;
    private Size _page;

    public WashFade()
    {
        Children.Add(_wash);
        Children.Add(_shade);
    }

    /// <summary>True: solid at the top, clearing downward (under a header). False: clear at
    /// the top, solid from <see cref="FadeHeight"/> down (behind a bottom bar).</summary>
    public bool SolidAtTop { get; set; }

    /// <summary>How tall the ramp is; the rest of the band is solid.</summary>
    public double FadeHeight { get; set; } = 44;

    /// <summary>What to repaint: the wash bitmap (or, with no cover, the page's plain colour),
    /// the shade at a fraction of the page height, and where this band sits in the page.</summary>
    public void Update(IImageBrushSource? art, Color plain, Func<double, Color> shade, Rect bandInPage, Size page)
    {
        if (bandInPage.Height <= 0 || page.Height <= 0) return;
        if (ReferenceEquals(art, _art) && plain == _plain && bandInPage == _band && page == _page) return;
        _art = art;
        _plain = plain;
        _band = bandInPage;
        _page = page;

        _wash.Background = art != null
            ? new ImageBrush(art)
            {
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(-bandInPage.X, -bandInPage.Y, page.Width, page.Height, RelativeUnit.Absolute),
            }
            : new SolidColorBrush(plain);
        _shade.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(shade(bandInPage.Top / page.Height), 0),
                new GradientStop(shade(bandInPage.Bottom / page.Height), 1),
            },
        };

        var ramp = Math.Clamp(FadeHeight / bandInPage.Height, 0.01, 1);
        var mask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        // A smoothstep-shaped ramp in a few stops, solid past it.
        double[] at = { 0, 0.25, 0.5, 0.75, 1 };
        foreach (var t in at)
        {
            var alpha = t * t * (3 - 2 * t);
            mask.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * (SolidAtTop ? 1 - alpha : alpha)), 0, 0, 0), t * ramp));
        }
        if (ramp < 1) mask.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(SolidAtTop ? 0 : 255), 0, 0, 0), 1));
        OpacityMask = mask;
    }

    /// <summary>The band takes the size it is given and never asks for more.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return default;
    }
}
