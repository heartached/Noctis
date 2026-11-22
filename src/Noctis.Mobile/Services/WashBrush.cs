using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Converters;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Settings › Background › Colour Wash as one brush: Now Playing's own wash of the cover (the
/// PreBlurredArtworkConverter "wash", 128 px, made once per cover) stretched over the page, under a
/// veil, black on a dark page and white on a light one, just thick enough that the cover's
/// brightest (or darkest) spot stays inside <see cref="BackgroundCatalog"/>'s luminance band
/// (<see cref="VeilFor"/>): the theme's text and glass keep the contrast they have on the mesh.
/// Built once; a new cover fades in over the old one (<see cref="Step"/>), so nothing is
/// allocated per frame and every surface using the brush repaints together.
/// </summary>
internal sealed class WashBrush
{
    private static readonly Rect Unit = new(0, 0, 100, 100);

    private readonly ImageDrawing _under = new() { Rect = Unit };
    private readonly ImageDrawing _over = new() { Rect = Unit };
    private readonly DrawingGroup _fadeIn = new();
    private readonly SolidColorBrush _veil = new(Colors.Transparent);
    private Color _veilFrom, _veilTo;

    public WashBrush(bool light)
    {
        Light = light;
        var rect = new RectangleGeometry(Unit);
        var group = new DrawingGroup();
        // An opaque floor first: a cover with see-through parts still paints a solid page.
        group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(light ? Colors.White : Colors.Black), Geometry = rect });
        group.Children.Add(_under);
        _fadeIn.Children.Add(_over);
        group.Children.Add(_fadeIn);
        group.Children.Add(new GeometryDrawing { Brush = _veil, Geometry = rect });
        Brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    public bool Light { get; }

    public DrawingBrush Brush { get; }

    /// <summary>The wash showing (the one fading in, during a fade).</summary>
    internal IImage? Wash => _over.ImageSource;

    /// <summary>The veil over it now.</summary>
    internal Color Veil => _veil.Color;

    /// <summary>Paints <paramref name="wash"/> under <paramref name="veil"/>. With <paramref
    /// name="fade"/> (a new song's cover) the old wash stays under it and <see cref="Step"/> brings
    /// the new one in; without, it shows at once (a Strength step, the first cover).</summary>
    public void Set(IImage wash, Color veil, bool fade)
    {
        var old = _over.ImageSource;
        fade &= old != null && !ReferenceEquals(old, wash);
        _under.ImageSource = fade ? old : wash;
        _veilFrom = fade ? _veil.Color : veil;
        _over.ImageSource = wash;
        _veilTo = veil;
        Step(fade ? 0 : 1);
    }

    /// <summary>The fade at <paramref name="t"/> (0 … 1), eased as BlurredCoverBackdrop's.</summary>
    public void Step(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var eased = t * t * (3 - 2 * t);
        _fadeIn.Opacity = eased;
        _veil.Color = Lerp(_veilFrom, _veilTo, eased);
        if (t >= 1) _under.ImageSource = _over.ImageSource;   // the old cover is let go
        Invalidate();
    }

    private bool _nudge;

    /// <summary>MeshBrush's nudge: a DrawingBrush does not notice its drawing changing, its own
    /// opacity does repaint it (0.2 %, invisible).</summary>
    private void Invalidate() => Brush.Opacity = (_nudge = !_nudge) ? 0.998 : 1;

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    // ── The veil ──

    /// <summary>A wash's brightest spot as a dark page shows it (over black) and its darkest as a
    /// light page shows it (over white).</summary>
    public readonly record struct Extremes(Color Brightest, Color Darkest);

    /// <summary>The wash's <see cref="Extremes"/>, measured once per cover from its 128 px pixels as
    /// the converter made it (<see cref="PreBlurredArtworkConverter.WashTonesOf"/>); null for a
    /// bitmap it did not make (the page then keeps the theme's background).</summary>
    public static Extremes? Measure(IImage? wash) =>
        wash is Bitmap bitmap && PreBlurredArtworkConverter.WashTonesOf(bitmap) is { } tones
            ? new Extremes(tones.Brightest, tones.Darkest)
            : null;

    /// <summary>
    /// The veil over a wash: on a dark page black, thick enough that the brightest spot falls to
    /// <see cref="BackgroundCatalog.DarkMaxLuminance"/> (white text keeps 8:1, as on the mesh); on
    /// a light page white, thick enough that the darkest spot rises to <see
    /// cref="BackgroundCatalog.LightMinLuminance"/> (black text keeps 12:1). Strength then thins the
    /// wash further toward the veil, as it thins the mesh's washes (<see cref="MeshBrush.WashAlpha"/>):
    /// only ever darker on dark, lighter on light. The alpha is rounded up, never under the band.
    /// </summary>
    public static Color VeilFor(Extremes extremes, bool light, double strength)
    {
        // How much of the wash's own colour may show (the rest is the veil).
        var keep = 1.0;
        if (light)
            while (keep > 0 && DominantColorExtractor.GetRelativeLuminance(BackgroundCatalog.Mix(Colors.White, extremes.Darkest, keep)) < BackgroundCatalog.LightMinLuminance)
                keep -= 0.01;
        else
            while (keep > 0 && DominantColorExtractor.GetRelativeLuminance(BackgroundCatalog.Mix(Colors.Black, extremes.Brightest, keep)) > BackgroundCatalog.DarkMaxLuminance)
                keep -= 0.01;
        keep = Math.Max(0, keep) * MeshBrush.WashAlpha(strength);
        var alpha = (byte)Math.Min(255, Math.Ceiling((1 - keep) * 255));
        return light ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);
    }
}
