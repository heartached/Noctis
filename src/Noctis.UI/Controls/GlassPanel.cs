using System;
using System.Collections.Concurrent;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using SkiaSharp;

namespace Noctis.Controls;

/// <summary>How a <see cref="GlassPanel"/>'s rim light (<see cref="GlassPanel.Specular"/>) falls.</summary>
public enum GlassSpecularStyle
{
    /// <summary>A line all the way round, lit from the top-left (the default).</summary>
    Diagonal,

    /// <summary>Apple's iOS 26 menus: a soft catch-light along the top edge that fades out down the
    /// sides, fainter again along the bottom, added to what lies beneath so it takes its colour.
    /// With no <see cref="GlassPanel.EdgeBrush"/> the glass then has no outline of its own.</summary>
    TopCatch,
}

/// <summary>
/// A surface that frosts whatever the app has already drawn beneath it when Liquid
/// Glass is on, and paints a plain rounded <see cref="Background"/> when it is off.
///
/// Off is byte-identical to a Border with the same Background/CornerRadius, so hosts
/// that leave the toggle off see no change. On, the panel draws three layers: a
/// backdrop blur of the surface pixels under its bounds (a Skia snapshot re-drawn
/// through a Gaussian blur, clipped to the rounded rect), a tint of the theme surface
/// colour, and a 1px light inner edge. Popups and separate windows cannot be frosted
/// this way — the snapshot only sees the surface this control renders into.
///
/// Opt-in, for the phone's iOS 26 look: a refracting rim (<see cref="Refraction"/>, drawn
/// through <see cref="GlassLens"/>), a rim light (<see cref="Specular"/>) and an outer
/// <see cref="BoxShadow"/>. All default off, leaving the layers above unchanged.
///
/// Follows <see cref="AppGlass"/> by default; set <see cref="UseAppGlass"/> false to
/// drive <see cref="IsGlassActive"/> directly (tests, previews).
/// </summary>
public class GlassPanel : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<GlassPanel>();

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<GlassPanel>();

    public static readonly StyledProperty<bool> UseAppGlassProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(UseAppGlass), true);

    public static readonly StyledProperty<bool> IsGlassActiveProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(IsGlassActive));

    /// <summary>Tint colour over the blur. Null: the Background's colour when it is a
    /// solid brush, else the theme surface tint.</summary>
    public static readonly StyledProperty<Color?> GlassTintProperty =
        AvaloniaProperty.Register<GlassPanel, Color?>(nameof(GlassTint));

    /// <summary>Tint opacity over the blur. Null: the Background brush's own opacity
    /// (so a user opacity slider bound to the brush keeps working).</summary>
    public static readonly StyledProperty<double?> GlassTintOpacityProperty =
        AvaloniaProperty.Register<GlassPanel, double?>(nameof(GlassTintOpacity));

    /// <summary>Blur radius in logical pixels; scaled to device pixels at render time.
    /// 0 (or less) skips the backdrop snapshot entirely: the panel is then a plain
    /// translucent tint over whatever is beneath — no per-frame surface copy, so cheap
    /// enough for many small hosts (the far Cover Flow cards).</summary>
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BlurRadius), 18);

    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<GlassPanel, IBrush?>(nameof(EdgeBrush),
            new ImmutableSolidColorBrush(Colors.White, 0.22));

    public static readonly StyledProperty<double> EdgeThicknessProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(EdgeThickness), 1);

    /// <summary>
    /// 0..1 fade applied INSIDE the panel's own drawing (blur paint alpha, tint and edge
    /// opacity, plain fill opacity). Use this to animate a glass surface in or out instead
    /// of Opacity on the panel or an ancestor: the GPU backend does not apply an opacity
    /// layer to the custom Skia blur, so an Opacity fade leaves the frost at full strength
    /// while everything around it disappears.
    /// </summary>
    public static readonly StyledProperty<double> FadeProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Fade), 1.0);

    // ---- Opt-in Liquid Glass lens (iOS 26). Every default leaves the panel exactly as it
    // drew before these existed; the desktop never sets them. See GlassLens.

    /// <summary>How far in from the edge, in logical px, the rim refracts what lies beneath
    /// (the lens band). 0: no rim lens. Needs <see cref="RefractionAmount"/> too.</summary>
    public static readonly StyledProperty<double> RefractionProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Refraction));

    /// <summary>How far inward, in logical px, the outermost rim pixel looks: content near the
    /// edge is magnified and, at the very edge, folds into a thin mirrored band.</summary>
    public static readonly StyledProperty<double> RefractionAmountProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(RefractionAmount));

    /// <summary>0..1: the rim lens splits red and blue apart, and the rim light runs through
    /// the spectrum (a moving droplet's rainbow edge).</summary>
    public static readonly StyledProperty<double> DispersionProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Dispersion));

    /// <summary>Zoom of the whole backdrop about the panel centre; 1 = none.</summary>
    public static readonly StyledProperty<double> MagnificationProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Magnification), 1.0);

    /// <summary>Saturation of the frosted backdrop (Apple's vibrancy); 1 = unchanged.</summary>
    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Saturation), 1.0);

    /// <summary>The darkest the backdrop may show through (0..1 luminance, default 0): light glass
    /// lifts black content so dark text on it keeps its contrast. See <see cref="GlassLensFrame"/>.</summary>
    public static readonly StyledProperty<double> BackdropMinLuminanceProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropMinLuminance));

    /// <summary>The brightest the backdrop may show through (0..1 luminance, default 1): dark glass
    /// caps a white cover so light text on it stays readable.</summary>
    public static readonly StyledProperty<double> BackdropMaxLuminanceProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropMaxLuminance), 1.0);

    /// <summary>0..1 strength of the rim light drawn over the tint: a crisp line brightest on
    /// the top-left rim and again on the bottom-right, with a soft inner glow.</summary>
    public static readonly StyledProperty<double> SpecularProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Specular));

    /// <summary>How the rim light falls round the outline; see <see cref="GlassSpecularStyle"/>.</summary>
    public static readonly StyledProperty<GlassSpecularStyle> SpecularStyleProperty =
        AvaloniaProperty.Register<GlassPanel, GlassSpecularStyle>(nameof(SpecularStyle));

    /// <summary>How the backdrop's luminance maps into [<see cref="BackdropMinLuminance"/>,
    /// <see cref="BackdropMaxLuminance"/>]: 1 (default) a straight line; above 1 it climbs steeply
    /// from the dark end and eases into the top, so the glass shows most content bright and clear and
    /// holds only the brightest down. See <see cref="GlassLensFrame.ToneCurve"/>.</summary>
    public static readonly StyledProperty<double> BackdropToneCurveProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropToneCurve), 1.0);

    /// <summary>0..1: how content darker than the glass's floor is lifted. 0 (default) scales its
    /// colour up; 1 lays a white veil over it, as frosted white glass does, so a dark colour behind
    /// light glass reads as a soft tint instead of a vivid blotch. See <see cref="GlassLensFrame.ToneLift"/>.</summary>
    public static readonly StyledProperty<double> BackdropLiftProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropLift));

    /// <summary>0..1: how much of the glass is the scattered light of its surroundings (Apple: "its
    /// color is informed by surrounding content") — the backdrop blurred four times as wide as
    /// <see cref="BlurRadius"/>, brought to the glass's luminance and mixed in. Dark content behind
    /// the glass then takes the colour of what lies around it instead of a grey or white veil, and a
    /// plain backdrop keeps its own hue and saturation; <see cref="BackdropLift"/>'s veil only comes
    /// in as the lift grows. 0 (default): off. See <see cref="GlassLensFrame.Spill"/>.</summary>
    public static readonly StyledProperty<double> BackdropSpillProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropSpill));

    /// <summary>0..1: the rim's shadow on the face, a soft dark band over the lens band
    /// (<see cref="Refraction"/>), deepest under the top edge, gone along the bottom. 0 (default): none.</summary>
    public static readonly StyledProperty<double> BackdropShadeProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BackdropShade));

    /// <summary>
    /// Set by a host whose layers beneath the panel only ever change as a whole: any change
    /// there repaints the panel's entire bounds (the pop-up card over its full-window blurred
    /// snapshot and dim). A frame that repaints only part of the panel then cannot have changed
    /// what lies beneath it, so it asks for no full repaint afterwards. Without it, the GPU
    /// backend asks after every partial frame (a list scrolling inside the card): its surface
    /// copy is a texture that cannot be read back to rule a change out.
    /// </summary>
    public static readonly StyledProperty<bool> BeneathRepaintsWholeProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(BeneathRepaintsWhole));

    /// <summary>
    /// 0..1: how much of the frost is the lite frost instead of the full glass, for a panel in motion
    /// (the phone menus' pop: PopMenu). At 1 the lens's expensive passes are skipped (no spill blur,
    /// no lens shader) and the backdrop is one blur through a colour table that follows the lens's
    /// tone range, saturation and lift (<see cref="GlassLens.LiteFilter"/>); in between the two are
    /// cross-faded from one snapshot. On an S23 the full glass cost frames over 25 ms through most of
    /// the 340 ms pop (11-14). 0 (default): the full glass only, exactly as before; the desktop never
    /// sets it. Only matters with a lens (<see cref="HasLens"/>): the plain frost is already one blur.
    /// </summary>
    public static readonly StyledProperty<double> LiteProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Lite));

    /// <summary>Shadows cast outside the panel, as on a <see cref="Border"/>; none by default.</summary>
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<GlassPanel>();

    /// <summary>
    /// The phone's Settings › Appearance › Reduce Glass (beta testers 11-22: "in some menus it just
    /// feels laggy"), inherited down the tree from where it is set (the phone's ShellView). On, the
    /// panel is not glass whatever else it is set to: it reads nothing beneath it (no backdrop copy,
    /// blur, lens, spill, lite frost or reach op; any copy it held is released) and draws no rim
    /// light, just its <see cref="BoxShadow"/>, a plain fill (<see cref="PlainBackground"/>, else
    /// <see cref="Background"/>) and its edge (<see cref="EdgeBrush"/>). Off (the default, and all
    /// the desktop ever has) changes nothing.
    /// </summary>
    public static readonly AttachedProperty<bool> ReduceGlassProperty =
        AvaloniaProperty.RegisterAttached<GlassPanel, Visual, bool>("ReduceGlass", inherits: true);

    /// <summary>The fill under <see cref="ReduceGlass"/>; null: <see cref="Background"/>. Glass
    /// tints are see-through for the frost beneath them, so a surface whose text needs a solid face
    /// sets one here.</summary>
    public static readonly StyledProperty<IBrush?> PlainBackgroundProperty =
        AvaloniaProperty.Register<GlassPanel, IBrush?>(nameof(PlainBackground));

    public static bool GetReduceGlass(Visual element) => element.GetValue(ReduceGlassProperty);
    public static void SetReduceGlass(Visual element, bool value) => element.SetValue(ReduceGlassProperty, value);

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(BackgroundProperty, CornerRadiusProperty, UseAppGlassProperty,
            IsGlassActiveProperty, GlassTintProperty, GlassTintOpacityProperty, BlurRadiusProperty,
            EdgeBrushProperty, EdgeThicknessProperty, FadeProperty,
            RefractionProperty, RefractionAmountProperty, DispersionProperty, MagnificationProperty,
            SaturationProperty, SpecularProperty, BoxShadowProperty, BackdropMinLuminanceProperty, BackdropMaxLuminanceProperty,
            SpecularStyleProperty, BackdropToneCurveProperty, BackdropLiftProperty, BackdropSpillProperty, BackdropShadeProperty,
            BeneathRepaintsWholeProperty, LiteProperty, ReduceGlassProperty, PlainBackgroundProperty);
        AffectsArrange<GlassPanel>(BlurRadiusProperty);
    }

    public double Refraction { get => GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double RefractionAmount { get => GetValue(RefractionAmountProperty); set => SetValue(RefractionAmountProperty, value); }
    public double Dispersion { get => GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    public double Magnification { get => GetValue(MagnificationProperty); set => SetValue(MagnificationProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Specular { get => GetValue(SpecularProperty); set => SetValue(SpecularProperty, value); }
    public GlassSpecularStyle SpecularStyle { get => GetValue(SpecularStyleProperty); set => SetValue(SpecularStyleProperty, value); }
    public double BackdropToneCurve { get => GetValue(BackdropToneCurveProperty); set => SetValue(BackdropToneCurveProperty, value); }
    public double BackdropLift { get => GetValue(BackdropLiftProperty); set => SetValue(BackdropLiftProperty, value); }
    public double BackdropMinLuminance { get => GetValue(BackdropMinLuminanceProperty); set => SetValue(BackdropMinLuminanceProperty, value); }
    public double BackdropMaxLuminance { get => GetValue(BackdropMaxLuminanceProperty); set => SetValue(BackdropMaxLuminanceProperty, value); }
    public double BackdropSpill { get => GetValue(BackdropSpillProperty); set => SetValue(BackdropSpillProperty, value); }
    public double BackdropShade { get => GetValue(BackdropShadeProperty); set => SetValue(BackdropShadeProperty, value); }
    public BoxShadows BoxShadow { get => GetValue(BoxShadowProperty); set => SetValue(BoxShadowProperty, value); }
    public bool BeneathRepaintsWhole { get => GetValue(BeneathRepaintsWholeProperty); set => SetValue(BeneathRepaintsWholeProperty, value); }
    public double Lite { get => GetValue(LiteProperty); set => SetValue(LiteProperty, value); }
    public bool ReduceGlass { get => GetValue(ReduceGlassProperty); set => SetValue(ReduceGlassProperty, value); }
    public IBrush? PlainBackground { get => GetValue(PlainBackgroundProperty); set => SetValue(PlainBackgroundProperty, value); }

    /// <summary>Any lens setting is on: the backdrop is drawn through <see cref="GlassLens"/>.</summary>
    internal bool HasLens =>
        (Refraction > 0 && RefractionAmount != 0) || Math.Abs(Magnification - 1) > 1e-6 || Math.Abs(Saturation - 1) > 1e-6
        || BackdropMinLuminance > 0 || BackdropMaxLuminance < 1 || BackdropSpill > 0 || (BackdropShade > 0 && Refraction > 0);

    /// <summary>The panel reads what lies beneath it: a blur, a lens, or both.</summary>
    internal bool NeedsBackdrop => BlurRadius > 0 || HasLens;

    public double Fade { get => GetValue(FadeProperty); set => SetValue(FadeProperty, value); }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool UseAppGlass { get => GetValue(UseAppGlassProperty); set => SetValue(UseAppGlassProperty, value); }
    public bool IsGlassActive { get => GetValue(IsGlassActiveProperty); set => SetValue(IsGlassActiveProperty, value); }
    public Color? GlassTint { get => GetValue(GlassTintProperty); set => SetValue(GlassTintProperty, value); }
    public double? GlassTintOpacity { get => GetValue(GlassTintOpacityProperty); set => SetValue(GlassTintOpacityProperty, value); }
    public double BlurRadius { get => GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public IBrush? EdgeBrush { get => GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }
    public double EdgeThickness { get => GetValue(EdgeThicknessProperty); set => SetValue(EdgeThicknessProperty, value); }

    /// <summary>The state this panel renders with right now (never glass under <see cref="ReduceGlass"/>).</summary>
    public bool EffectiveGlassActive => !ReduceGlass && (UseAppGlass ? AppGlass.IsActive : IsGlassActive);

    private EventHandler? _glassChanged, _backdropRetired;

    /// <summary>
    /// What lies under this panel, kept between frames so a repaint of part of the panel
    /// frosts exactly like a repaint of all of it (see <see cref="GlassBackdropCache"/>).
    /// This replaced a per-frame pump that invalidated the whole panel on every frame
    /// while glass was on: it kept the app rendering at the display rate with nothing
    /// changing, and the compositor's single dirty rect joined the sidebar rail and the
    /// island into one box over most of the window, repainted every frame.
    /// </summary>
    private GlassBackdropCache? _backdrop;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _glassChanged = (_, _) =>
        {
            if (!EffectiveGlassActive) ReleaseBackdrop();
            InvalidateVisual();
            _reach?.InvalidateVisual();
        };
        AppGlass.Changed += _glassChanged;
        // A released GPU copy can only be freed on the render thread: repaint so this
        // panel's next frame carries the op that frees it (see Render).
        _backdropRetired = (_, _) => InvalidateVisual();
        GlassBackdropCache.Retired += _backdropRetired;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_glassChanged != null) AppGlass.Changed -= _glassChanged;
        if (_backdropRetired != null) GlassBackdropCache.Retired -= _backdropRetired;
        _glassChanged = _backdropRetired = null;
        ReleaseBackdrop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UseAppGlassProperty || change.Property == IsGlassActiveProperty || change.Property == ReduceGlassProperty
            || change.Property == BlurRadiusProperty
            || change.Property == RefractionProperty || change.Property == RefractionAmountProperty
            || change.Property == MagnificationProperty || change.Property == SaturationProperty
            || change.Property == BackdropMinLuminanceProperty || change.Property == BackdropMaxLuminanceProperty
            || change.Property == BackdropSpillProperty || change.Property == BackdropShadeProperty)
        {
            if (!EffectiveGlassActive || !NeedsBackdrop) ReleaseBackdrop();
            _reach?.InvalidateVisual();
        }
    }

    private void ReleaseBackdrop()
    {
        _backdrop?.Release();
        _backdrop = null;
    }

    /// <summary>
    /// Laid over the ring the blur reads around the panel. The compositor replays a visual's
    /// drawing only where its layout bounds meet the frame's dirty rect, so a change right
    /// beside the panel (content scrolling past the rail's edge, a tile next to the island)
    /// never reached the frost: see <see cref="GlassReach"/>.
    /// </summary>
    private readonly GlassReach _reach;

    /// <summary>Set by Render: this panel's current drawing frosts. Read on the render thread.</summary>
    private volatile bool _frosting;
    private int _repaintPosted;

    public GlassPanel()
    {
        _reach = new GlassReach(this);
        VisualChildren.Add(_reach);
    }

    /// <summary>The backdrop copy this panel frosts with, if any; read by its reach op on the render thread.</summary>
    internal GlassBackdropCache? Backdrop => _backdrop;

    /// <summary>How far past each edge the blur reads, in layout units (3σ, rounded up).</summary>
    internal double Reach => BlurRadius > 0 ? Math.Ceiling(3 * BlurRadius) + 1 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        _reach.Measure(Size.Infinity);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var r = Reach;
        _reach.Arrange(new Rect(-r, -r, size.Width + 2 * r, size.Height + 2 * r));
        return size;
    }

    /// <summary>Repaint the whole panel next frame. Any thread; one request in flight.</summary>
    internal void RequestFullRepaint()
    {
        if (Interlocked.Exchange(ref _repaintPosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _repaintPosted, 0);
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    /// <summary>Render thread: a frame repainted pixels beside the panel that its blur reads.</summary>
    internal void OnReachRepainted()
    {
        if (_frosting) RequestFullRepaint();
    }

    /// <summary>Resolves the tint colour and opacity the on-state paints.</summary>
    public (Color Color, double Opacity) ResolveTint()
    {
        var solid = Background as ISolidColorBrush;
        var color = GlassTint ?? solid?.Color ?? AppGlass.SurfaceTint;
        var opacity = GlassTintOpacity ?? (solid is null ? 0.5 : solid.Opacity * (solid.Color.A / 255.0));
        // The tint colour's own alpha is folded into the opacity above.
        return (Color.FromRgb(color.R, color.G, color.B), Math.Clamp(opacity, 0, 1));
    }

    /// <summary>
    /// What a repaint of this panel alone dirties, in layout units: the compositor repaints a
    /// visual's whole subtree (measured on the emulator), so besides the glass that is the
    /// rim's edge, the reach ring (<see cref="GlassReach"/>) and the <see cref="BoxShadow"/>.
    /// </summary>
    internal Rect RepaintFootprint(Rect rect)
    {
        var footprint = rect.Inflate(Math.Max(Reach, 1));
        var shadows = BoxShadow;
        return shadows.Count > 0 ? footprint.Union(shadows.TransformBounds(rect)) : footprint;
    }

    /// <summary>The lens this frame draws with (logical units), or null for the plain frost.</summary>
    internal GlassLensSettings? LensSettings() => HasLens
        ? new GlassLensSettings(Math.Max(Refraction, 0), RefractionAmount, Math.Clamp(Dispersion, 0, 1),
            Math.Max(Magnification, 0.01), Math.Max(Saturation, 0),
            Math.Clamp(BackdropMinLuminance, 0, 1), Math.Clamp(BackdropMaxLuminance, 0, 1), Math.Max(BackdropToneCurve, 0.01),
            Math.Clamp(BackdropLift, 0, 1), Math.Clamp(BackdropSpill, 0, 1), Math.Clamp(BackdropShade, 0, 1))
        : null;

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var fade = Math.Clamp(Fade, 0, 1);
        _frosting = EffectiveGlassActive && NeedsBackdrop && fade > 0 && rect.Width > 0 && rect.Height > 0;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var rrect = new RoundedRect(rect, CornerRadius);

        if (GlassBackdropCache.HasRetired)
            context.Custom(new GlassRetiredDrainOp(rect));

        if (fade <= 0) return;

        var shadows = BoxShadow;
        if (ReduceGlass)
        {
            // Reduce Glass: shadow, plain fill and edge; nothing read from beneath, no rim light.
            using (fade < 1 ? context.PushOpacity(fade) : default)
            {
                var fill = PlainBackground ?? Background;
                if (fill != null || shadows.Count > 0)
                    context.DrawRectangle(fill, null, rrect, shadows);
                if (EdgeBrush != null && EdgeThickness > 0)
                {
                    var half = EdgeThickness / 2;
                    context.DrawRectangle(null, new ImmutablePen(EdgeBrush.ToImmutable(), EdgeThickness), rrect.Deflate(half, half));
                }
            }
            return;
        }
        if (!EffectiveGlassActive)
        {
            if (Background == null && shadows.Count == 0) return;
            using (fade < 1 ? context.PushOpacity(fade) : default)
                context.DrawRectangle(Background, null, rrect, shadows);
            return;
        }

        // Cast before the backdrop is read, so every frame (partial or whole) frosts the same
        // pixels around the panel. Avalonia clips an outer shadow out of the shape itself, so
        // nothing darkens the glass from beneath. It widens what a repaint of the panel dirties:
        // see RepaintFootprint.
        if (shadows.Count > 0)
            using (fade < 1 ? context.PushOpacity(fade) : default)
                context.DrawRectangle(null, null, rrect, shadows);

        // The fade is folded into every layer here rather than applied as Opacity: see Fade.
        if (NeedsBackdrop)
            context.Custom(new GlassBackdropOp(rect, CornerRadius, BlurRadius, fade,
                _backdrop ??= new GlassBackdropCache(RequestFullRepaint), LensSettings(), RepaintFootprint(rect), BeneathRepaintsWhole, Math.Clamp(Lite, 0, 1)));

        var (tint, opacity) = ResolveTint();
        opacity *= fade;
        if (opacity > 0)
            context.DrawRectangle(new ImmutableSolidColorBrush(tint, opacity), null, rrect);

        var specular = Math.Clamp(Specular, 0, 1) * fade;
        if (specular > 0)
            context.Custom(new GlassRimOp(rect, CornerRadius, specular, Math.Clamp(Dispersion, 0, 1), SpecularStyle));

        if (EdgeBrush != null && EdgeThickness > 0)
        {
            var pen = new ImmutablePen(EdgeBrush.ToImmutable(), EdgeThickness);
            var half = EdgeThickness / 2;
            using (fade < 1 ? context.PushOpacity(fade) : default)
                context.DrawRectangle(null, pen, rrect.Deflate(half, half));
        }
    }
}


/// <summary>
/// The Skia half of the frost, kept free of Avalonia so a raster SKSurface can test it.
/// </summary>
public static class GlassBlur
{
    /// <summary>Hard cap on the blur sigma in device pixels: bounds the per-frame cost on
    /// large panels (a 1000×720 sheet at 125% scaling).</summary>
    public const float MaxSigma = 30f;

    /// <summary>
    /// Re-draws the pixels of <paramref name="surface"/> under <paramref name="deviceRect"/>
    /// back onto its canvas through a Gaussian blur, clipped to <paramref name="clip"/>.
    /// Coordinates are device pixels. Returns false when the surface cannot be snapshotted.
    /// </summary>
    public static bool Draw(SKCanvas canvas, SKSurface surface, SKRect deviceRect, SKRoundRect clip, float sigma, float alpha = 1f)
    {
        using var path = new SKPath();
        path.AddRoundRect(clip);
        return Draw(canvas, surface, deviceRect, path, sigma, alpha);
    }

    /// <summary>
    /// Same as the rounded-rect overload, clipping to an arbitrary device-space
    /// <paramref name="clip"/> path — the host's rounded rect run through its full render
    /// matrix, so a panel under a rotate/perspective transform (a tilted Cover Flow card)
    /// frosts exactly its own tilted outline instead of an axis-aligned box around it.
    /// </summary>
    public static bool Draw(SKCanvas canvas, SKSurface surface, SKRect deviceRect, SKPath clip, float sigma, float alpha = 1f,
        GlassLensFrame? lens = null)
    {
        sigma = ClampSigma(sigma);
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return true;
        using var source = Snapshot(surface, SourceRect(deviceRect, sigma), out var at);
        if (source is null) return false;
        DrawFrosted(canvas, source, at, clip, sigma, alpha, lens, surface.Context);
        return true;
    }

    /// <summary>The plain blur, or the lens over it when <paramref name="lens"/> is set and the
    /// shader runs here (the blur alone otherwise).</summary>
    internal static void DrawFrosted(SKCanvas canvas, SKImage source, SKRectI at, SKPath clip, float sigma, float alpha,
        GlassLensFrame? lens, GRRecordingContext? context, float lite = 0)
    {
        if (lens is { } frame && lite > 0)
        {
            // The lite frost under the full glass's share (GlassPanel.Lite), both read from this one
            // snapshot. Each replaces what it covers by its alpha (ClearForFrost), so the lite one at
            // a*l / (1 - a*(1 - l)) and the full one over it at a*(1 - l) leave
            // beneath*(1 - a) + lite*a*l + full*a*(1 - l): a straight cross-fade.
            lite = Math.Min(lite, 1f);
            var full = alpha * (1 - lite);
            using (var filter = GlassLens.LiteFilter(frame))
                DrawBlurred(canvas, source, at, clip, sigma, full < 1 ? alpha * lite / (1 - full) : 0, filter);
            Interlocked.Increment(ref GlassLens.LitePasses);
            if (full <= 0) return;
            if (GlassLens.Draw(canvas, source, at, clip, sigma, full, frame, context)) return;
            DrawBlurred(canvas, source, at, clip, sigma, full);
            return;
        }
        if (lens is { } f && GlassLens.Draw(canvas, source, at, clip, sigma, alpha, f, context)) return;
        DrawBlurred(canvas, source, at, clip, sigma, alpha);
    }

    internal static float ClampSigma(float sigma) => Math.Clamp(sigma, 0.5f, MaxSigma);

    /// <summary>
    /// The device pixels the blur reads for a panel. A blur sampled only from the exact rect
    /// collapses toward its edges (the frost-band lesson), so it is fed 3σ of surrounding
    /// pixels and the clip trims.
    /// </summary>
    internal static SKRectI SourceRect(SKRect deviceRect, float sigma)
    {
        var bleed = (float)Math.Ceiling(sigma * 3);
        return ToGrid(RoundOut(SKRect.Inflate(deviceRect, bleed, bleed)));
    }

    /// <summary>
    /// The device-pixel grid the backdrop copies are sized to (<see cref="ToGrid"/>, <see cref="GridSize"/>).
    /// A panel that scales or moves every frame (a menu's pop) asked the GPU for a texture of a new
    /// exact size on every frame for each copy (the snapshot, the clean copy, the frost and the
    /// spill), none of which Skia's texture cache could hand back; on the grid the size stays put
    /// for most frames and the cache reuses the last one.
    /// </summary>
    internal const int Grid = 32;

    /// <summary><paramref name="r"/> grown out to the <see cref="Grid"/>.</summary>
    internal static SKRectI ToGrid(SKRectI r) => new(
        FloorTo(r.Left), FloorTo(r.Top), -FloorTo(-r.Right), -FloorTo(-r.Bottom));

    /// <summary>A copy's size rounded up to the <see cref="Grid"/>.</summary>
    internal static SKSizeI GridSize(int width, int height) => new(-FloorTo(-width), -FloorTo(-height));

    private static int FloorTo(int v) => (int)Math.Floor(v / (double)Grid) * Grid;

    internal static SKRectI RoundOut(SKRect r) => new(
        (int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    /// <summary>
    /// Copies the part of <paramref name="rect"/> that lies on the surface (Skia trims the
    /// rect to it); <paramref name="at"/> is where that copy sits. Only this region is
    /// copied, not the whole surface.
    /// </summary>
    internal static SKImage? Snapshot(SKSurface surface, SKRectI rect, out SKRectI at)
    {
        at = SKRectI.Empty;
        var image = surface.Snapshot(rect);
        if (image is null) return null;
        at = SKRectI.Create(Math.Max(rect.Left, 0), Math.Max(rect.Top, 0), image.Width, image.Height);
        return image;
    }

    /// <summary>Draws <paramref name="source"/>, whose pixels sit at device rect
    /// <paramref name="at"/>, through the blur, clipped to the device path <paramref name="clip"/>.</summary>
    internal static void DrawBlurred(SKCanvas canvas, SKImage source, SKRectI at, SKPath clip, float sigma, float alpha,
        SKColorFilter? colorFilter = null)
    {
        canvas.Save();
        canvas.SetMatrix(SKMatrix.Identity);
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        var dst = SKRect.Create(at.Left, at.Top, at.Width, at.Height);
        using var paint = new SKPaint
        {
            ImageFilter = filter,
            FilterQuality = SKFilterQuality.Low,
            Color = new SKColor(255, 255, 255, AlphaByte(alpha)),
            BlendMode = SKBlendMode.Plus,
            ColorFilter = colorFilter,
        };
        ClearForFrost(canvas, dst, alpha);
        canvas.DrawImage(source, SKRect.Create(source.Width, source.Height), dst, paint);
        canvas.Restore();
    }

    internal static byte AlphaByte(float alpha) => (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255);

    /// <summary>
    /// The first half of drawing the frost so it REPLACES the pixels it blurs instead of
    /// lying over them (GitHub #133): scales what is under <paramref name="rect"/> (within the
    /// canvas clip) by 1 - <paramref name="alpha"/>; the frost then goes on with
    /// <see cref="SKBlendMode.Plus"/> at <paramref name="alpha"/>, so the result is
    /// lerp(beneath, frost, alpha) — at full strength the frost alone.
    ///
    /// A plain source-over draw only hid what was beneath where the frost came out opaque.
    /// With Liquid Glass the window itself is translucent (the page surfaces paint at ~35%
    /// so the OS blur shows through), so the frost of a page area is translucent too, and the
    /// un-blurred page under it stayed visible through it: big text on the bare page (the
    /// Home section headings) read crisply through the island, while covers and other opaque
    /// content were hidden by their own opaque frost and looked blurred.
    /// </summary>
    internal static void ClearForFrost(SKCanvas canvas, SKRect rect, float alpha)
    {
        using var clear = new SKPaint { Color = new SKColor(0, 0, 0, AlphaByte(alpha)), BlendMode = SKBlendMode.DstOut };
        canvas.DrawRect(rect, clear);
    }
}

/// <summary>
/// What lies under one <see cref="GlassPanel"/>, un-frosted, kept between frames on the
/// render thread.
///
/// The compositor repaints only a frame's dirty rect. When that rect covers part of a panel
/// (a hovered button, the island's scrolling title or playing bars, a tile hover reaching
/// under the island) the panel's pixels outside it still hold last frame's frosted, tinted
/// result, and a blur that read them frosted them a second time: the rect showed up as a
/// lighter or darker box. The backdrop op runs before the panel draws its tint, edge and
/// children, so the pixels the frame has just repainted under the panel are clean: they
/// refresh this copy. Pixels the frame did not repaint have not changed since they were
/// last painted, so the copy still holds them. The blur reads the surface around the panel
/// and this copy inside it, so a partial frame frosts exactly like a full one, and nothing
/// has to repaint the whole panel every frame.
///
/// Threading: <see cref="Draw"/> and <see cref="MarkStale"/> run on the render thread,
/// <see cref="Release"/> on the UI thread. A GPU-backed copy belongs to the render thread's
/// context and is only freed there: a release parks it until a render-thread op drains it.
/// </summary>
internal sealed class GlassBackdropCache
{
    private static readonly ConcurrentQueue<SKSurface> s_retired = new();
    private static int s_retiredCount;

    /// <summary>Every unreleased copy, so a reach op can tell a neighbour's own repaint apart
    /// from content changing beside its panel (<see cref="IsAnotherPanelsOwnRepaint"/>).</summary>
    private static readonly ConcurrentDictionary<GlassBackdropCache, byte> s_live = new();

    /// <summary>Raised on the UI thread when a released GPU copy waits for the render thread.</summary>
    public static event EventHandler? Retired;

    public static bool HasRetired => Volatile.Read(ref s_retiredCount) > 0;

    private readonly object _gate = new();
    private readonly Action? _requestFullRepaint;
    private readonly SKRegion _captured = new();
    private SKSurface? _clean;
    private IntPtr _context;
    private SKRectI _rect;
    private SKSizeI _cleanSize;
    private bool _suspect, _repaintQueued, _released;

    /// <param name="requestFullRepaint">Asks for the whole panel to be repainted next frame;
    /// called from the render thread. Needed only when a frame could not be frosted exactly
    /// (the copy was lost while the panel sat in an opacity layer, or it was remade mid-way).</param>
    public GlassBackdropCache(Action? requestFullRepaint = null)
    {
        _requestFullRepaint = requestFullRepaint;
        s_live[this] = 0;
    }

    private SKRectI _ownRect;
    private IntPtr _ownSurface;

    /// <summary>
    /// Render thread: <paramref name="deviceRect"/> on <paramref name="surface"/> is what a
    /// repaint of this panel alone dirties (<see cref="GlassPanel.RepaintFootprint"/>).
    /// </summary>
    public void NoteOwnRect(SKRectI deviceRect, IntPtr surface)
    {
        lock (_gate)
        {
            _ownRect = deviceRect;
            _ownSurface = surface;
        }
    }

    /// <summary>
    /// Render thread: the frame's dirty <paramref name="clip"/> lies wholly inside what a
    /// repaint of another live glass panel on <paramref name="surface"/> dirties — that panel
    /// repainting itself (its follow-up after a partial frame, a lifting droplet), not content
    /// changing. Glass stacked close together (the mini player 10 dp above the tab capsule)
    /// otherwise re-frosted each other in turn, every frame, forever: A's repaint, shadow and
    /// all, overlapped B, whose follow-up overlapped A. Content scrolling beside or under a
    /// panel dirties more than a neighbour's footprint and still reaches it.
    /// </summary>
    public static bool IsAnotherPanelsOwnRepaint(SKRectI clip, IntPtr surface, GlassBackdropCache? self)
    {
        if (clip.IsEmpty) return false;
        foreach (var entry in s_live)
        {
            var other = entry.Key;
            if (ReferenceEquals(other, self)) continue;
            SKRectI rect;
            IntPtr on;
            lock (other._gate)
            {
                rect = other._ownRect;
                on = other._ownSurface;
            }
            if (on == surface && !rect.IsEmpty && rect.Contains(clip)) return true;
        }
        return false;
    }

    /// <summary>
    /// Device pixels the compositor's dirty rect runs past what actually changed: Avalonia 12
    /// inflates every dirty rect by 1 px for antialiasing and rounds it out to whole pixels
    /// (SingleDirtyRectTracker / MultiDirtyRectTracker / RegionDirtyRectTracker: Inflate(1),
    /// then FromRectUnscaled). A change inside a panel therefore reports up to this far beyond
    /// it, and those pixels are repainted exactly as they were.
    /// </summary>
    internal const int DirtySlack = 2;

    /// <summary>A dirty rect <paramref name="repainted"/> reaches past <paramref name="panel"/> by
    /// more than <see cref="DirtySlack"/>: something beside the panel changed.</summary>
    internal static bool SpillsPast(SKRectI repainted, SKRectI panel)
    {
        if (repainted.IsEmpty) return false;
        var allowed = panel;
        allowed.Inflate(DirtySlack, DirtySlack);
        return !allowed.Contains(repainted);
    }

    /// <summary>Tests: behave as on the GPU backend, where the surface copy is a texture that
    /// <see cref="SKImage.PeekPixels()"/> cannot read, so a change beneath can never be ruled out.</summary>
    internal static bool UnreadableForTests;

    /// <summary>True while pixels under the panel may still hold frost the copy does not cover.</summary>
    internal bool IsSuspect { get { lock (_gate) return _suspect; } }

    /// <summary>
    /// Frosts the panel whose device-space outline is <paramref name="clip"/> onto
    /// <paramref name="canvas"/>, reading <paramref name="surface"/>. The canvas clip is the
    /// part of the frame being repainted. <paramref name="wholePanelRepainted"/>: this frame
    /// repaints the panel's entire bounds (the first frame of new panel render data does),
    /// so no stale frost of it is left anywhere. Returns false when the surface cannot be read.
    /// <paramref name="lens"/>: draw the frost through the Liquid Glass lens. It samples only
    /// the panel's own pixels (inward), all of which the composed copy holds clean, so a
    /// partial frame stays exact; a change beneath moves its output further than the blur's,
    /// which the full repaint this already asks for covers.
    /// <paramref name="beneathRepaintsWhole"/>: see <see cref="GlassPanel.BeneathRepaintsWhole"/>.
    /// </summary>
    public bool Draw(SKCanvas canvas, SKSurface surface, SKPath clip, float sigma, float alpha, bool wholePanelRepainted,
        GlassLensFrame? lens = null, bool beneathRepaintsWhole = false, float lite = 0)
    {
        DrainRetired();
        sigma = GlassBlur.ClampSigma(sigma);
        alpha = Math.Clamp(alpha, 0f, 1f);
        var panelBounds = clip.Bounds;
        // Before taking this copy's lock: the check takes each other copy's.
        var neighbourRepaint = !wholePanelRepainted && IsAnotherPanelsOwnRepaint(canvas.DeviceClipBounds, surface.Handle, this);
        using var source = GlassBlur.Snapshot(surface, GlassBlur.SourceRect(panelBounds, sigma), out var at);
        if (source is null) return false;
        var panel = SKRectI.Intersect(GlassBlur.RoundOut(panelBounds), at);
        // What the blur reads that this frame repainted, and the part of it under the panel.
        var repainted = SKRectI.Intersect(at, canvas.DeviceClipBounds);
        var fresh = SKRectI.Intersect(panel, repainted);

        SKImage? composed = null;
        var askRepaint = false;
        lock (_gate)
        {
            if (!_released)
            {
                var whole = wholePanelRepainted || (fresh == panel && !panel.IsEmpty);
                // A partial frame frosts exactly inside the repainted rect. But a change BENEATH
                // the panel, or in the pixels around it the blur reads, also moves the frost up to
                // 3σ past that rect, where this frame repaints nothing: repaint the whole panel
                // next frame. Children draw above the frost and never feed it, so a frame that only
                // repainted them (the island's title, a hovered button) needs no second pass; nor
                // does another glass panel repainting itself over or beside this one. Nor does the
                // pixel or two the compositor adds round every dirty rect (DirtySlack): a list
                // scrolling edge to edge inside the panel reaches just past it on every step.
                var followUp = !whole && !neighbourRepaint
                    && (SpillsPast(repainted, panel) || (!beneathRepaintsWhole && BeneathChanged(source, at, panel, fresh)));
                Capture(source, at, panel, fresh, surface.Context, whole);
                if ((followUp || _suspect) && !_repaintQueued && _requestFullRepaint != null)
                    askRepaint = _repaintQueued = true;
                if (!fresh.IsEmpty && alpha > 0f && fresh != panel)
                    composed = Compose(source, at, surface.Context);
            }
        }
        if (askRepaint) _requestFullRepaint!();
        try
        {
            if (!fresh.IsEmpty && alpha > 0f)
                GlassBlur.DrawFrosted(canvas, composed ?? source, at, clip, sigma, alpha, lens, surface.Context, lite);
        }
        finally { composed?.Dispose(); }
        return true;
    }

    /// <summary>
    /// Whether the clean pixels this frame repainted under the panel differ from the copy:
    /// content beneath changed, not just a child drawn above the frost. Unknown counts as
    /// changed — a GPU copy cannot be compared without reading it back. Under the gate,
    /// before <see cref="Capture"/> overwrites the copy.
    /// </summary>
    private bool BeneathChanged(SKImage source, SKRectI at, SKRectI panel, SKRectI fresh)
    {
        if (fresh.IsEmpty) return false;
        if (_clean == null || _rect != panel || !_captured.Contains(fresh)) return true;
        if (UnreadableForTests) return true;
        using var now = source.PeekPixels();
        using var before = _clean.PeekPixels();
        if (now == null || before == null || now.ColorType != before.ColorType) return true;
        var bpp = now.BytesPerPixel;
        var length = fresh.Width * bpp;
        var a = now.GetPixelSpan();
        var b = before.GetPixelSpan();
        for (var y = fresh.Top; y < fresh.Bottom; y++)
        {
            var rowNow = a.Slice((y - at.Top) * now.RowBytes + (fresh.Left - at.Left) * bpp, length);
            var rowBefore = b.Slice((y - _rect.Top) * before.RowBytes + (fresh.Left - _rect.Left) * bpp, length);
            if (!rowNow.SequenceEqual(rowBefore)) return true;
        }
        return false;
    }

    /// <summary>Copies what the frame repainted under the panel into the clean copy, remaking
    /// the copy when the panel moved, resized or the GPU context changed. Under the gate.</summary>
    private void Capture(SKImage source, SKRectI at, SKRectI panel, SKRectI fresh, GRRecordingContext? context, bool wholePanelRepainted)
    {
        var contextHandle = context?.Handle ?? IntPtr.Zero;
        if (_clean == null || _rect != panel || _context != contextHandle)
        {
            _captured.SetEmpty();
            // Remade outside a whole-panel repaint: frost from before may sit outside the copy.
            _suspect = true;
            // Sized to the grid (GlassBlur.Grid), and kept while the panel still fits it: a panel
            // scaling every frame (a menu's pop) no longer makes a new GPU texture every frame.
            var size = GlassBlur.GridSize(panel.Width, panel.Height);
            if (_clean == null || _context != contextHandle || _cleanSize != size)
            {
                FreeClean(onRenderThread: true);
                if (!panel.IsEmpty)
                {
                    var info = new SKImageInfo(size.Width, size.Height, source.ColorType, source.AlphaType, source.ColorSpace);
                    _clean = context != null ? SKSurface.Create(context, false, info) : SKSurface.Create(info);
                    _cleanSize = size;
                    _context = contextHandle;
                }
            }
            if (!panel.IsEmpty) _rect = panel;
        }
        if (wholePanelRepainted)
        {
            // Every pixel of the panel this frame could not repaint lies outside its visible
            // area and never carried its frost.
            _captured.SetEmpty();
            _suspect = false;
            _repaintQueued = false;
        }
        if (_clean == null || fresh.IsEmpty) return;
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        _clean.Canvas.DrawImage(source,
            SKRect.Create(fresh.Left - at.Left, fresh.Top - at.Top, fresh.Width, fresh.Height),
            SKRect.Create(fresh.Left - panel.Left, fresh.Top - panel.Top, fresh.Width, fresh.Height), paint);
        _captured.Op(fresh, SKRegionOperation.Union);
    }

    /// <summary>The blur source with every captured pixel of the panel taken from the clean
    /// copy instead of the surface (which holds last frame's frost there). Under the gate.</summary>
    private SKImage? Compose(SKImage source, SKRectI at, GRRecordingContext? context)
    {
        if (_clean == null || _captured.IsEmpty) return null;
        var info = new SKImageInfo(at.Width, at.Height, source.ColorType, source.AlphaType, source.ColorSpace);
        using var scratch = context != null ? SKSurface.Create(context, false, info) : SKSurface.Create(info);
        if (scratch == null) return null;
        var canvas = scratch.Canvas;
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(source, 0, 0, paint);
        // Regions clip in device space, whatever the matrix: move it to the scratch's origin.
        using var captured = new SKRegion(_captured);
        captured.Translate(-at.Left, -at.Top);
        canvas.ClipRegion(captured);
        using var clean = _clean.Snapshot();
        canvas.DrawImage(clean, _rect.Left - at.Left, _rect.Top - at.Top, paint);
        return scratch.Snapshot();
    }

    /// <summary>The op drew into an intermediate layer instead of the surface this frame: the
    /// copy missed what changed under the panel. Render thread.</summary>
    public void MarkStale()
    {
        lock (_gate)
        {
            if (_released) return;
            _captured.SetEmpty();
            _suspect = true;
        }
    }

    /// <summary>The panel stopped frosting or left the tree. UI thread.</summary>
    public void Release()
    {
        bool parked;
        lock (_gate)
        {
            if (_released) return;
            _released = true;
            s_live.TryRemove(this, out _);
            parked = FreeClean(onRenderThread: false);
            _captured.Dispose();
        }
        if (parked) Retired?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Returns true when a GPU copy was parked for the render thread. Under the gate.</summary>
    private bool FreeClean(bool onRenderThread)
    {
        var clean = _clean;
        _clean = null;
        if (clean == null) return false;
        if (onRenderThread || _context == IntPtr.Zero)
        {
            clean.Dispose();
            return false;
        }
        s_retired.Enqueue(clean);
        Interlocked.Increment(ref s_retiredCount);
        return true;
    }

    /// <summary>Frees parked GPU copies. Render thread only.</summary>
    public static void DrainRetired()
    {
        while (s_retired.TryDequeue(out var surface))
        {
            Interlocked.Decrement(ref s_retiredCount);
            surface.Dispose();
        }
    }
}

/// <summary>Render-thread op: everything it needs is captured by value at construction.</summary>
internal sealed class GlassBackdropOp : ICustomDrawOperation
{
    private readonly CornerRadius _corners;
    private readonly double _blurRadius, _fade;
    private readonly GlassBackdropCache? _cache;
    private readonly GlassLensSettings? _lens;
    private readonly Rect _footprint;
    private readonly double _lite;
    private readonly bool _beneathRepaintsWhole;
    private bool _drawn;

    public GlassBackdropOp(Rect bounds, CornerRadius corners, double blurRadius, double fade = 1, GlassBackdropCache? cache = null,
        GlassLensSettings? lens = null, Rect? footprint = null, bool beneathRepaintsWhole = false, double lite = 0)
    {
        _lite = lite;
        Bounds = bounds;
        _corners = corners;
        _blurRadius = blurRadius;
        _fade = fade;
        _cache = cache;
        _lens = lens;
        _footprint = footprint ?? bounds.Inflate(1);
        _beneathRepaintsWhole = beneathRepaintsWhole;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) =>
        other is GlassBackdropOp o && o.Bounds == Bounds && o._corners == _corners && o._blurRadius == _blurRadius && o._fade == _fade
        && ReferenceEquals(o._cache, _cache) && o._lens == _lens && o._footprint == _footprint
        && o._beneathRepaintsWhole == _beneathRepaintsWhole && o._lite == _lite;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        using var api = lease.Lease();
        var canvas = api.SkCanvas;
        var surface = api.SkSurface;
        // No surface (rendering into an intermediate layer): the tint alone carries the look.
        if (surface is null)
        {
            _cache?.MarkStale();
            return;
        }

        // Build the rounded rect in LOCAL space and push it through the full matrix
        // (DPI, scale, and any rotate/perspective the host sits under) so the clip is
        // the panel's true outline on screen; the blur source is that path's bounds.
        var m = canvas.TotalMatrix;
        var local = new SKRoundRect();
        local.SetRectRadii(new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom), new[]
        {
            new SKPoint((float)_corners.TopLeft, (float)_corners.TopLeft),
            new SKPoint((float)_corners.TopRight, (float)_corners.TopRight),
            new SKPoint((float)_corners.BottomRight, (float)_corners.BottomRight),
            new SKPoint((float)_corners.BottomLeft, (float)_corners.BottomLeft),
        });
        using var path = new SKPath();
        path.AddRoundRect(local);
        path.Transform(m);
        var dev = path.Bounds;
        var scale = Math.Max(Math.Abs(m.ScaleX), Math.Abs(m.ScaleY));
        if (scale < 0.01f) scale = 1f;
        var sigma = (float)(_blurRadius * scale);
        var lens = LensFrame(m);

        if (_cache is null)
        {
            GlassBlur.Draw(canvas, surface, dev, path, sigma, (float)_fade, lens);
            return;
        }
        // What a repaint of the panel alone dirties on screen. The compositor's dirty rect for it
        // runs a few device pixels past the shadow's nominal bounds (measured: 1-3 px at 2.625x),
        // so allow a layout pixel either way plus rounding.
        var own = GlassBlur.RoundOut(m.MapRect(new SKRect((float)_footprint.X, (float)_footprint.Y, (float)_footprint.Right, (float)_footprint.Bottom)));
        var slack = 2 + (int)Math.Ceiling(2 * scale);
        own.Inflate(slack, slack);
        _cache.NoteOwnRect(own, surface.Handle);
        // New render data repaints the panel's whole bounds in the frame that brings it, so
        // this op's first draw leaves no stale frost of the panel anywhere.
        var first = !_drawn;
        _drawn = true;
        _cache.Draw(canvas, surface, path, sigma, (float)_fade, wholePanelRepainted: first, lens, _beneathRepaintsWhole, (float)_lite);
    }

    /// <summary>The lens in device px under <paramref name="m"/>; null without one, or when the
    /// panel is rotated, skewed or in perspective (the lens is axis-aligned: plain frost then).</summary>
    private GlassLensFrame? LensFrame(SKMatrix m)
    {
        if (_lens is not { } l) return null;
        var local = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);
        var radii = new GlassCornerRadii((float)_corners.TopLeft, (float)_corners.TopRight, (float)_corners.BottomRight, (float)_corners.BottomLeft);
        if (!GlassLens.TryMapToDevice(m, local, radii, out var device, out var deviceRadii, out var scale)) return null;
        // The spill reads the backdrop four times as wide as the frost (at least 16 logical px).
        var spillSigma = Math.Max(_blurRadius * 4, 16) * scale;
        return new GlassLensFrame(device, deviceRadii, (float)(l.Band * scale), (float)(l.Bend * scale),
            (float)l.Dispersion, (float)l.Zoom, (float)l.Saturation, (float)l.ToneMin, (float)l.ToneMax, (float)l.ToneCurve, (float)l.ToneLift,
            (float)l.Spill, (float)spillSigma, (float)l.Shade);
    }
}

/// <summary>Frees released GPU backdrop copies on the render thread; a <see cref="GlassPanel"/>
/// carries it in its render data while any are waiting.</summary>
internal sealed class GlassRetiredDrainOp : ICustomDrawOperation
{
    public GlassRetiredDrainOp(Rect bounds) => Bounds = bounds;
    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => other is GlassRetiredDrainOp o && o.Bounds == Bounds;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        // Leased so the render thread's GPU context is current while the copies are freed.
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        using var api = lease.Lease();
        GlassBackdropCache.DrainRetired();
    }
}

/// <summary>
/// Render-thread op for a <see cref="GlassPanel"/>'s rim light (<see cref="GlassPanel.Specular"/>),
/// drawn over its tint. Sizes are logical px scaled to device px here.
/// </summary>
internal sealed class GlassRimOp : ICustomDrawOperation
{
    /// <summary>The crisp rim line, the soft inner glow's reach and its strength (logical px / 0..1).</summary>
    internal const double LineWidth = 0.9, GlowWidth = 7, Glow = 0.22;

    /// <summary><see cref="GlassSpecularStyle.TopCatch"/>'s glow: tighter and stronger relative to its
    /// line, the way the light fades into Apple's menus over the first few points under the top edge.</summary>
    internal const double CatchGlowWidth = 3, CatchGlow = 0.3;

    private readonly Rect _rect;
    private readonly CornerRadius _corners;
    private readonly double _strength, _iridescence;
    private readonly GlassSpecularStyle _style;

    public GlassRimOp(Rect rect, CornerRadius corners, double strength, double iridescence,
        GlassSpecularStyle style = GlassSpecularStyle.Diagonal)
    {
        _rect = rect;
        // The anti-aliased outline reaches half a device pixel past the rect.
        Bounds = rect.Inflate(1);
        _corners = corners;
        _strength = strength;
        _iridescence = iridescence;
        _style = style;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) =>
        other is GlassRimOp o && o._rect == _rect && o._corners == _corners && o._strength == _strength && o._iridescence == _iridescence
        && o._style == _style;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        using var api = lease.Lease();
        var canvas = api.SkCanvas;
        var local = new SKRect((float)_rect.X, (float)_rect.Y, (float)_rect.Right, (float)_rect.Bottom);
        var radii = new GlassCornerRadii((float)_corners.TopLeft, (float)_corners.TopRight, (float)_corners.BottomRight, (float)_corners.BottomLeft);
        if (!GlassLens.TryMapToDevice(canvas.TotalMatrix, local, radii, out var device, out var deviceRadii, out var scale)) return;
        if (_style == GlassSpecularStyle.TopCatch)
            GlassLens.DrawRim(canvas, device, deviceRadii, (float)_strength, (float)(LineWidth * scale),
                (float)(CatchGlowWidth * scale), (float)CatchGlow, (float)_iridescence, GlassRimShape.TopCatch, additive: true);
        else
            GlassLens.DrawRim(canvas, device, deviceRadii, (float)_strength, (float)(LineWidth * scale),
                (float)(GlowWidth * scale), (float)Glow, (float)_iridescence);
    }
}

/// <summary>
/// An invisible child a <see cref="GlassPanel"/> lays over the ring its blur reads (3σ past
/// each edge). The compositor replays a visual's drawing only where its layout bounds meet
/// the frame's dirty rect, so the panel itself never hears of a repaint right beside it —
/// content scrolling past the rail's edge, a tile next to the island — though its frost
/// reads those pixels. This child spans the ring, so such a frame replays its op, which asks
/// the panel to repaint whole next frame. Not hit-testable, draws nothing.
/// </summary>
internal sealed class GlassReach : Control
{
    private readonly GlassPanel _owner;

    public GlassReach(GlassPanel owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        var reach = _owner.Reach;
        if (reach <= 0 || !_owner.EffectiveGlassActive || Bounds.Width <= 2 * reach || Bounds.Height <= 2 * reach) return;
        context.Custom(new GlassReachOp(new Rect(Bounds.Size), reach, _owner));
    }
}

/// <summary>Render-thread half of <see cref="GlassReach"/>.</summary>
internal sealed class GlassReachOp : ICustomDrawOperation
{
    private readonly double _reach;
    private readonly GlassPanel _owner;

    public GlassReachOp(Rect bounds, double reach, GlassPanel owner)
    {
        Bounds = bounds;
        _reach = reach;
        _owner = owner;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) =>
        other is GlassReachOp o && o.Bounds == Bounds && o._reach == _reach && ReferenceEquals(o._owner, _owner);
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        using var api = lease.Lease();
        var clip = api.SkCanvas.DeviceClipBounds;
        if (clip.IsEmpty) return;
        var r = (float)_reach;
        var panel = GlassBlur.RoundOut(api.SkCanvas.TotalMatrix.MapRect(
            new SKRect(r, r, (float)Bounds.Width - r, (float)Bounds.Height - r)));
        if (ShouldRepaint(clip, panel, api.SkSurface?.Handle ?? IntPtr.Zero, _owner.Backdrop))
            _owner.OnReachRepainted();
    }

    /// <summary>
    /// Whether a frame whose dirty rect is <paramref name="clip"/> (meeting the reach ring of
    /// the panel at device rect <paramref name="panel"/>) changed pixels the panel's blur reads.
    /// </summary>
    internal static bool ShouldRepaint(SKRectI clip, SKRectI panel, IntPtr surface, GlassBackdropCache? self)
    {
        // Inside the panel the backdrop op handles the repaint itself (within the compositor's
        // own slack round a dirty rect); a repaint covering the whole panel frosted it from what
        // is there now.
        if (clip.IsEmpty || !GlassBackdropCache.SpillsPast(clip, panel) || clip.Contains(panel)) return false;
        return !GlassBackdropCache.IsAnotherPanelsOwnRepaint(clip, surface, self);
    }
}
