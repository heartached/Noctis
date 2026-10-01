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

    /// <summary>0..1 strength of the rim light drawn over the tint: a crisp line brightest on
    /// the top-left rim and again on the bottom-right, with a soft inner glow.</summary>
    public static readonly StyledProperty<double> SpecularProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Specular));

    /// <summary>Shadows cast outside the panel, as on a <see cref="Border"/>; none by default.</summary>
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<GlassPanel>();

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(BackgroundProperty, CornerRadiusProperty, UseAppGlassProperty,
            IsGlassActiveProperty, GlassTintProperty, GlassTintOpacityProperty, BlurRadiusProperty,
            EdgeBrushProperty, EdgeThicknessProperty, FadeProperty,
            RefractionProperty, RefractionAmountProperty, DispersionProperty, MagnificationProperty,
            SaturationProperty, SpecularProperty, BoxShadowProperty);
        AffectsArrange<GlassPanel>(BlurRadiusProperty);
    }

    public double Refraction { get => GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double RefractionAmount { get => GetValue(RefractionAmountProperty); set => SetValue(RefractionAmountProperty, value); }
    public double Dispersion { get => GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    public double Magnification { get => GetValue(MagnificationProperty); set => SetValue(MagnificationProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Specular { get => GetValue(SpecularProperty); set => SetValue(SpecularProperty, value); }
    public BoxShadows BoxShadow { get => GetValue(BoxShadowProperty); set => SetValue(BoxShadowProperty, value); }

    /// <summary>Any lens setting is on: the backdrop is drawn through <see cref="GlassLens"/>.</summary>
    internal bool HasLens =>
        (Refraction > 0 && RefractionAmount != 0) || Math.Abs(Magnification - 1) > 1e-6 || Math.Abs(Saturation - 1) > 1e-6;

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

    /// <summary>The state this panel renders with right now.</summary>
    public bool EffectiveGlassActive => UseAppGlass ? AppGlass.IsActive : IsGlassActive;

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
        if (change.Property == UseAppGlassProperty || change.Property == IsGlassActiveProperty || change.Property == BlurRadiusProperty
            || change.Property == RefractionProperty || change.Property == RefractionAmountProperty
            || change.Property == MagnificationProperty || change.Property == SaturationProperty)
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

    /// <summary>The lens this frame draws with (logical units), or null for the plain frost.</summary>
    internal GlassLensSettings? LensSettings() => HasLens
        ? new GlassLensSettings(Math.Max(Refraction, 0), RefractionAmount, Math.Clamp(Dispersion, 0, 1),
            Math.Max(Magnification, 0.01), Math.Max(Saturation, 0))
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
        if (!EffectiveGlassActive)
        {
            if (Background == null && shadows.Count == 0) return;
            using (fade < 1 ? context.PushOpacity(fade) : default)
                context.DrawRectangle(Background, null, rrect, shadows);
            return;
        }

        // Cast before the backdrop is read, so every frame (partial or whole) frosts the same
        // pixels around the panel. Avalonia clips an outer shadow out of the shape itself, so
        // nothing darkens the glass from beneath.
        if (shadows.Count > 0)
            using (fade < 1 ? context.PushOpacity(fade) : default)
                context.DrawRectangle(null, null, rrect, shadows);

        // The fade is folded into every layer here rather than applied as Opacity: see Fade.
        if (NeedsBackdrop)
            context.Custom(new GlassBackdropOp(rect, CornerRadius, BlurRadius, fade,
                _backdrop ??= new GlassBackdropCache(RequestFullRepaint), LensSettings()));

        var (tint, opacity) = ResolveTint();
        opacity *= fade;
        if (opacity > 0)
            context.DrawRectangle(new ImmutableSolidColorBrush(tint, opacity), null, rrect);

        var specular = Math.Clamp(Specular, 0, 1) * fade;
        if (specular > 0)
            context.Custom(new GlassRimOp(rect, CornerRadius, specular, Math.Clamp(Dispersion, 0, 1)));

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
        GlassLensFrame? lens, GRRecordingContext? context)
    {
        if (lens is { } frame && GlassLens.Draw(canvas, source, at, clip, sigma, alpha, frame, context)) return;
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
        return RoundOut(SKRect.Inflate(deviceRect, bleed, bleed));
    }

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
    internal static void DrawBlurred(SKCanvas canvas, SKImage source, SKRectI at, SKPath clip, float sigma, float alpha)
    {
        canvas.Save();
        canvas.SetMatrix(SKMatrix.Identity);
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            ImageFilter = filter,
            FilterQuality = SKFilterQuality.Low,
            Color = new SKColor(255, 255, 255, (byte)Math.Round(alpha * 255)),
        };
        canvas.DrawImage(source, SKRect.Create(source.Width, source.Height), SKRect.Create(at.Left, at.Top, at.Width, at.Height), paint);
        canvas.Restore();
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

    /// <summary>Raised on the UI thread when a released GPU copy waits for the render thread.</summary>
    public static event EventHandler? Retired;

    public static bool HasRetired => Volatile.Read(ref s_retiredCount) > 0;

    private readonly object _gate = new();
    private readonly Action? _requestFullRepaint;
    private readonly SKRegion _captured = new();
    private SKSurface? _clean;
    private IntPtr _context;
    private SKRectI _rect;
    private bool _suspect, _repaintQueued, _released;

    /// <param name="requestFullRepaint">Asks for the whole panel to be repainted next frame;
    /// called from the render thread. Needed only when a frame could not be frosted exactly
    /// (the copy was lost while the panel sat in an opacity layer, or it was remade mid-way).</param>
    public GlassBackdropCache(Action? requestFullRepaint = null) => _requestFullRepaint = requestFullRepaint;

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
    /// </summary>
    public bool Draw(SKCanvas canvas, SKSurface surface, SKPath clip, float sigma, float alpha, bool wholePanelRepainted,
        GlassLensFrame? lens = null)
    {
        DrainRetired();
        sigma = GlassBlur.ClampSigma(sigma);
        alpha = Math.Clamp(alpha, 0f, 1f);
        var panelBounds = clip.Bounds;
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
                // repainted them (the island's title, a hovered button) needs no second pass.
                var followUp = !whole && (repainted != fresh || BeneathChanged(source, at, panel, fresh));
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
                GlassBlur.DrawFrosted(canvas, composed ?? source, at, clip, sigma, alpha, lens, surface.Context);
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
            FreeClean(onRenderThread: true);
            _captured.SetEmpty();
            // Remade outside a whole-panel repaint: frost from before may sit outside the copy.
            _suspect = true;
            if (!panel.IsEmpty)
            {
                var info = new SKImageInfo(panel.Width, panel.Height, source.ColorType, source.AlphaType, source.ColorSpace);
                _clean = context != null ? SKSurface.Create(context, false, info) : SKSurface.Create(info);
                _rect = panel;
                _context = contextHandle;
            }
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
    private bool _drawn;

    public GlassBackdropOp(Rect bounds, CornerRadius corners, double blurRadius, double fade = 1, GlassBackdropCache? cache = null,
        GlassLensSettings? lens = null)
    {
        Bounds = bounds;
        _corners = corners;
        _blurRadius = blurRadius;
        _fade = fade;
        _cache = cache;
        _lens = lens;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) =>
        other is GlassBackdropOp o && o.Bounds == Bounds && o._corners == _corners && o._blurRadius == _blurRadius && o._fade == _fade
        && ReferenceEquals(o._cache, _cache) && o._lens == _lens;
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
        // New render data repaints the panel's whole bounds in the frame that brings it, so
        // this op's first draw leaves no stale frost of the panel anywhere.
        var first = !_drawn;
        _drawn = true;
        _cache.Draw(canvas, surface, path, sigma, (float)_fade, wholePanelRepainted: first, lens);
    }

    /// <summary>The lens in device px under <paramref name="m"/>; null without one, or when the
    /// panel is rotated, skewed or in perspective (the lens is axis-aligned: plain frost then).</summary>
    private GlassLensFrame? LensFrame(SKMatrix m)
    {
        if (_lens is not { } l) return null;
        var local = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);
        var radii = new SKPoint4((float)_corners.TopLeft, (float)_corners.TopRight, (float)_corners.BottomRight, (float)_corners.BottomLeft);
        if (!GlassLens.TryMapToDevice(m, local, radii, out var device, out var deviceRadii, out var scale)) return null;
        return new GlassLensFrame(device, deviceRadii, (float)(l.Band * scale), (float)(l.Bend * scale),
            (float)l.Dispersion, (float)l.Zoom, (float)l.Saturation);
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

    private readonly Rect _rect;
    private readonly CornerRadius _corners;
    private readonly double _strength, _iridescence;

    public GlassRimOp(Rect rect, CornerRadius corners, double strength, double iridescence)
    {
        _rect = rect;
        // The anti-aliased outline reaches half a device pixel past the rect.
        Bounds = rect.Inflate(1);
        _corners = corners;
        _strength = strength;
        _iridescence = iridescence;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) =>
        other is GlassRimOp o && o._rect == _rect && o._corners == _corners && o._strength == _strength && o._iridescence == _iridescence;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        using var api = lease.Lease();
        var canvas = api.SkCanvas;
        var local = new SKRect((float)_rect.X, (float)_rect.Y, (float)_rect.Right, (float)_rect.Bottom);
        var radii = new SKPoint4((float)_corners.TopLeft, (float)_corners.TopRight, (float)_corners.BottomRight, (float)_corners.BottomLeft);
        if (!GlassLens.TryMapToDevice(canvas.TotalMatrix, local, radii, out var device, out var deviceRadii, out var scale)) return;
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
        // Inside the panel the backdrop op handles the repaint itself; a repaint covering the
        // whole panel frosted it from what is there now.
        if (panel.Contains(clip) || clip.Contains(panel)) return;
        _owner.OnReachRepainted();
    }
}
