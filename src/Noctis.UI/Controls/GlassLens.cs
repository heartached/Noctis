using System;
using SkiaSharp;

namespace Noctis.Controls;

/// <summary>
/// One frame of a <see cref="GlassPanel"/>'s opt-in Liquid Glass lens, in DEVICE pixels: the
/// panel's on-screen rect and corner radii plus how its rim bends what lies beneath.
/// Built on the render thread from the panel's logical settings and its render matrix.
/// </summary>
/// <param name="Bounds">The panel's device rect (axis-aligned: the lens is skipped under
/// rotation, skew or perspective).</param>
/// <param name="Radii">Corner radii in device px: top-left, top-right, bottom-right, bottom-left.</param>
/// <param name="Band">How far in from the edge the rim bends light (0: no rim lens).</param>
/// <param name="Bend">How far inward the outermost rim pixel looks, in device px.</param>
/// <param name="Dispersion">0..1: the rim splits red and blue apart (chromatic aberration).</param>
/// <param name="Zoom">1 = none; above 1 magnifies the whole backdrop about the panel centre.</param>
/// <param name="Saturation">1 = none; above 1 makes the backdrop more vivid (vibrancy).</param>
/// <param name="ToneMin">The backdrop's luminance range is squeezed into
/// [<paramref name="ToneMin"/>, <paramref name="ToneMax"/>] (0..1, default the full range), so
/// text on the glass keeps its contrast over any content: dark glass caps how bright a white
/// cover can show through, light glass lifts how dark a black one can.</param>
/// <param name="ToneMax">See <paramref name="ToneMin"/>.</param>
/// <param name="ToneCurve">1 = the luminance maps onto that range in a straight line. Above 1 the
/// map climbs steeply from the dark end and eases into the top (<c>1 - (1 - l)^curve</c>): most
/// content shows through clear and bright, and only the brightest is held down for the text.</param>
/// <param name="ToneLift">0..1: how a colour is lifted to a brighter luminance. 0 scales it up
/// (vivid; a dark colour lifted far turns garish and runs out of range), 1 lays a white veil over
/// it (frosted white glass: the colour keeps its own strength, never brighter).</param>
/// <param name="Spill">0..1: the share of the glass that is the scattered light of its surroundings —
/// the backdrop blurred at <paramref name="SpillSigma"/>, brought to the same luminance and mixed
/// in. Dark content then takes the colour of what lies around it instead of a grey or white veil,
/// and a plain backdrop keeps its own hue and saturation. With it the veil only comes in as the lift
/// grows (a small lift keeps the colour, as Apple's dark glass does). 0: off.</param>
/// <param name="SpillSigma">The wide blur the spill reads, in device px.</param>
/// <param name="Shade">0..1: the rim's shadow on the face, a soft dark band over the lens band
/// (<paramref name="Band"/>) deepest under the top edge, which faces the light, and gone along the
/// bottom. 0: none.</param>
public readonly record struct GlassLensFrame(
    SKRect Bounds, GlassCornerRadii Radii, float Band, float Bend, float Dispersion, float Zoom, float Saturation,
    float ToneMin = 0, float ToneMax = 1, float ToneCurve = 1, float ToneLift = 0,
    float Spill = 0, float SpillSigma = 0, float Shade = 0);

/// <summary>A <see cref="GlassPanel"/>'s lens settings in logical px, captured by value for the
/// render thread; <see cref="GlassBackdropOp"/> scales them to device px.</summary>
internal readonly record struct GlassLensSettings(double Band, double Bend, double Dispersion, double Zoom, double Saturation,
    double ToneMin = 0, double ToneMax = 1, double ToneCurve = 1, double ToneLift = 0, double Spill = 0, double Shade = 0);

/// <summary>A rounded rect's four corner radii in device px, passed to the shaders as one float4.</summary>
public readonly record struct GlassCornerRadii(float TopLeft, float TopRight, float BottomRight, float BottomLeft);

/// <summary>
/// How the rim light falls round a <see cref="GlassPanel"/>'s outline. Where the rim's outward
/// normal faces the light (<paramref name="LightX"/>, <paramref name="LightY"/>, a unit vector)
/// the line is at full strength; elsewhere it is <paramref name="Ambient"/>, plus
/// <paramref name="Back"/> on the rim facing away (the light caught again on the far side).
/// <paramref name="Power"/> is how fast the light fades round the corners; the inner glow keeps
/// <paramref name="GlowAmbient"/> of its strength along the rim the light does not face.
/// </summary>
public readonly record struct GlassRimShape(float LightX, float LightY, float Ambient, float Back, float Power, float GlowAmbient)
{
    /// <summary>The default: lit from the top-left, a line all the way round.</summary>
    public static readonly GlassRimShape Diagonal = new(-0.7071f, -0.7071f, 0.22f, 0.5f, 1.5f, 0.35f);

    /// <summary>Apple's iOS 26 menus: a catch-light along the top edge that fades out down the
    /// sides, caught again more faintly along the bottom, and next to nothing on the sides, so
    /// the glass has no outline of its own. The lobe falls off near-linearly with the rim's turn
    /// away from the light (the falloff the open-source renderers measured on iOS); the sides'
    /// trace is a few percent, under what reads as a line (owner 10-14: our menus still showed an
    /// outline down the sides that Apple's do not).</summary>
    public static readonly GlassRimShape TopCatch = new(0, -1, 0.04f, 0.4f, 1.5f, 0.05f);
}

/// <summary>
/// The Liquid Glass lens: Apple's iOS 26 glass does not only frost what lies beneath, its
/// rounded rim refracts it — near the edge the content is magnified and pulled inward, and
/// at the very edge it folds into a thin mirrored band, like looking through the curved rim
/// of a glass lens. This is the same circle-profile rim lens the open-source Compose/Flutter
/// reimplementations use (signed distance of a rounded rect → outward normal → sample the
/// backdrop <c>circle(1 − depth/band) × bend</c> pixels inward), run as a Skia runtime
/// shader over the frosted backdrop.
///
/// The C# twins of the shader's math (<see cref="SdRoundRect"/>, <see cref="Normal"/>,
/// <see cref="SamplePoint"/>) exist so tests can check the shader samples where the math
/// says it should; keep them in step with <see cref="LensSource"/>.
/// </summary>
public static class GlassLens
{
    /// <summary>Shared SkSL: the rounded rect's signed distance and outward normal.</summary>
    private const string RoundRectSdf = """
        float cornerRadius(float2 p, float4 r) {
            return p.x >= 0.0 ? (p.y <= 0.0 ? r.y : r.z) : (p.y <= 0.0 ? r.x : r.w);
        }

        // Negative inside, 0 on the outline, in pixels.
        float sdRoundRect(float2 p, float2 h, float r) {
            float2 q = abs(p) - (h - float2(r));
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        // Outward unit normal; r is a softened radius so the field turns smoothly at corners.
        float2 roundRectNormal(float2 p, float2 h, float r) {
            float2 q = abs(p) - (h - float2(r));
            float2 s = float2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
            if (q.x >= 0.0 || q.y >= 0.0) {
                float2 m = max(q, 0.0);
                float l = length(m);
                return l > 0.0 ? s * m / l : (q.x >= q.y ? float2(s.x, 0.0) : float2(0.0, s.y));
            }
            return q.x >= q.y ? float2(s.x, 0.0) : float2(0.0, s.y);
        }
        """;

    /// <summary>
    /// The lens. <c>content</c> is the (frosted) backdrop in device pixels; the shader runs in
    /// device pixels too (identity canvas matrix). Outside the rim band only the zoom and the
    /// saturation apply, so the centre stays a clear, lightly frosted view.
    /// </summary>
    public const string LensSource = """
        uniform shader content;
        uniform shader wide;
        uniform float4 bounds;
        uniform float4 radii;
        uniform float band;
        uniform float bend;
        uniform float dispersion;
        uniform float zoom;
        uniform float saturation;
        uniform float2 tone;
        uniform float toneCurve;
        uniform float toneLift;
        uniform float spill;
        uniform float shade;

        """ + RoundRectSdf + """

        // A colour brought to luminance `target` from its own `l`: scaled up (hue and saturation
        // kept) or, lifting under a veil, toward white. With the spill the veil only comes in as
        // the lift grows: scattered light takes over from transmitted light.
        float3 toned(float3 c, float l, float target, float lift) {
            float3 hued = c * (target / max(l, 0.0005));
            if (lift > 0.0 && target > l)
                hued = mix(hued, 1.0 - (1.0 - c) * ((1.0 - target) / max(1.0 - l, 0.0005)), lift);
            // Near black the hue is blur residue (an 8-bit (0,1,2) scaled up to the floor turned
            // bright blue), so it fades to grey there.
            return mix(float3(target), hued, smoothstep(0.0, 0.06, l));
        }

        half4 main(float2 coord) {
            float2 h = (bounds.zw - bounds.xy) * 0.5;
            float2 c = bounds.xy + h;
            float2 p = coord - c;
            float r = min(cornerRadius(p, radii), min(h.x, h.y));
            float2 at = c + p / zoom;
            float2 off = float2(0.0);
            float depth = max(-sdRoundRect(p, h, r), 0.0);
            if (band > 0.0 && depth < band) {
                float t = 1.0 - depth / band;
                float d = (1.0 - sqrt(max(1.0 - t * t, 0.0))) * bend;
                off = -roundRectNormal(p, h, min(r * 1.5, min(h.x, h.y))) * d;
            }
            half4 color;
            if (dispersion > 0.0 && (off.x != 0.0 || off.y != 0.0)) {
                float k = dispersion * 0.35;
                half4 g = content.eval(at + off);
                color = half4(content.eval(at + off * (1.0 + k)).r, g.g, content.eval(at + off * (1.0 - k)).b, g.a);
            } else {
                color = content.eval(at + off);
            }
            if (saturation != 1.0) {
                half l = dot(color.rgb, half3(0.2126, 0.7152, 0.0722));
                color.rgb = clamp(mix(half3(l), color.rgb, half(saturation)), 0.0, color.a);
            }
            if (tone.x > 0.0 || tone.y < 1.0 || spill > 0.0) {
                // Remap luminance 0..1 onto tone.x..tone.y, keeping each colour's hue.
                float l = dot(float3(color.rgb), float3(0.2126, 0.7152, 0.0722));
                float s = toneCurve == 1.0 ? l : 1.0 - pow(clamp(1.0 - l, 0.0, 1.0), toneCurve);
                float target = mix(tone.x, tone.y, s);
                // With the spill the veil only comes in as the lift grows: none up to a lift of
                // 1x, all of it from 5x (light glass over dark ink); a dark menu over a dark page
                // (2x) keeps most of the page's colour.
                float lift = spill > 0.0 ? toneLift * clamp((target / max(l, 0.0005) - 1.0) * 0.25, 0.0, 1.0) : toneLift;
                float3 rgb = toned(float3(color.rgb), l, target, lift);
                if (spill > 0.0) {
                    // The light the glass scatters: its surroundings, at the same luminance.
                    float3 w = float3(wide.eval(at).rgb);
                    float lw = dot(w, float3(0.2126, 0.7152, 0.0722));
                    float3 glow = toned(w, lw, target, toneLift * clamp((target / max(lw, 0.0005) - 1.0) * 0.25, 0.0, 1.0));
                    rgb = mix(rgb, glow, spill);
                }
                color.rgb = half3(clamp(rgb, 0.0, float(color.a)));
            }
            if (shade > 0.0 && band > 0.0 && depth < band) {
                // The raised rim shades the face like a wall lit from the top: deepest under the
                // top edge, half down the sides, nothing along the bottom, easing out over the band.
                float2 n = roundRectNormal(p, h, min(r * 1.5, min(h.x, h.y)));
                float wall = smoothstep(0.0, 1.0, -n.y * 0.5 + 0.5);
                float fall = 1.0 - smoothstep(0.0, band, depth);
                color.rgb *= half(1.0 - shade * wall * fall * fall);
            }
            return color;
        }
        """;

    /// <summary>
    /// The rim light drawn over the tint: a crisp line along the outline, brightest where it
    /// faces the light (top-left by default) and again, dimmer, on the opposite rim, plus a
    /// soft inner glow, falling round the rim as <c>light</c> and <c>shape</c> say (see
    /// <see cref="GlassRimShape"/>). <c>iridescence</c> adds a fringe just inside the line that runs through
    /// the spectrum by angle — the rainbow on a moving droplet's rim. Coverage is computed from the distance field, so the
    /// shader needs no clip and its edge is anti-aliased.
    /// </summary>
    public const string RimSource = """
        uniform float4 bounds;
        uniform float4 radii;
        uniform float strength;
        uniform float lineWidth;
        uniform float glowWidth;
        uniform float glow;
        uniform float iridescence;
        uniform float2 light;
        uniform float4 shape;

        """ + RoundRectSdf + """

        half4 main(float2 coord) {
            float2 h = (bounds.zw - bounds.xy) * 0.5;
            float2 c = bounds.xy + h;
            float2 p = coord - c;
            float r = min(cornerRadius(p, radii), min(h.x, h.y));
            float sd = sdRoundRect(p, h, r);
            float cover = clamp(0.5 - sd, 0.0, 1.0);
            if (cover <= 0.0) return half4(0.0);
            float depth = max(-sd, 0.0);
            float2 n = roundRectNormal(p, h, min(r * 1.5, min(h.x, h.y)));
            float facing = dot(n, light);
            float lit = shape.x + (1.0 - shape.x) * pow(max(facing, 0.0), shape.z) + shape.y * pow(max(-facing, 0.0), shape.z);
            float line = 1.0 - smoothstep(lineWidth * 0.5, lineWidth * 1.5, depth);
            float inner = glow * exp(-depth / max(glowWidth, 0.001)) * (shape.w + (1.0 - shape.w) * abs(facing));
            // Premultiplied: the white line and glow, then the spectral fringe just inside it.
            float a = line * lit + inner;
            float3 rgb = float3(a);
            if (iridescence > 0.0) {
                float fringe = smoothstep(lineWidth * 0.5, lineWidth * 1.5, depth)
                             * (1.0 - smoothstep(lineWidth * 2.0, lineWidth * 4.5, depth));
                if (fringe > 0.0) {
                    // Nudged off the centre so atan never sees (0, 0).
                    float turn = atan(p.y, p.x + 0.0001) / 6.2831853 + 0.5;
                    float3 spectrum = 0.5 + 0.5 * cos(6.2831853 * (turn * 2.0 + float3(0.0, 0.33, 0.67)));
                    // Only where the rim faces the light or turns away from it (top-left and
                    // bottom-right), fading out along the sides: a hint of prism, not a neon ring.
                    float f = min(iridescence, 1.0) * fringe * 0.4 * facing * facing;
                    rgb += spectrum * f;
                    a += f;
                }
            }
            float k = strength * cover;
            a = clamp(a * k, 0.0, 1.0);
            return half4(half3(min(rgb * k, float3(a))), half(a));
        }
        """;

    private static SKRuntimeEffect? s_lens, s_rim;
    private static string? s_lensError, s_rimError;

    /// <summary>The compiled lens, or null with <paramref name="error"/> set when Skia rejects it.</summary>
    public static SKRuntimeEffect? Lens(out string? error) => Compile(LensSource, ref s_lens, ref s_lensError, out error);

    /// <summary>The compiled rim light, or null with <paramref name="error"/> set.</summary>
    public static SKRuntimeEffect? Rim(out string? error) => Compile(RimSource, ref s_rim, ref s_rimError, out error);

    private static SKRuntimeEffect? Compile(string source, ref SKRuntimeEffect? effect, ref string? failure, out string? error)
    {
        if (effect is null && failure is null)
        {
            effect = SKRuntimeEffect.CreateShader(source, out var err);
            if (effect is null) failure = string.IsNullOrEmpty(err) ? "unknown shader error" : err;
        }
        error = failure;
        return effect;
    }

    // ---- C# twins of the shader math (tests) ------------------------------------------------

    /// <summary>Signed distance from <paramref name="p"/> (relative to the centre) to a rounded
    /// rect of half size (<paramref name="hx"/>, <paramref name="hy"/>): negative inside.</summary>
    public static float SdRoundRect(float px, float py, float hx, float hy, float r)
    {
        var qx = Math.Abs(px) - (hx - r);
        var qy = Math.Abs(py) - (hy - r);
        var mx = Math.Max(qx, 0f);
        var my = Math.Max(qy, 0f);
        return MathF.Sqrt(mx * mx + my * my) + Math.Min(Math.Max(qx, qy), 0f) - r;
    }

    /// <summary>The shader's outward normal at <paramref name="px"/>, <paramref name="py"/>.</summary>
    public static SKPoint Normal(float px, float py, float hx, float hy, float r)
    {
        var qx = Math.Abs(px) - (hx - r);
        var qy = Math.Abs(py) - (hy - r);
        var sx = px < 0 ? -1f : 1f;
        var sy = py < 0 ? -1f : 1f;
        if (qx >= 0 || qy >= 0)
        {
            var mx = Math.Max(qx, 0f);
            var my = Math.Max(qy, 0f);
            var l = MathF.Sqrt(mx * mx + my * my);
            if (l > 0) return new SKPoint(sx * mx / l, sy * my / l);
        }
        return qx >= qy ? new SKPoint(sx, 0) : new SKPoint(0, sy);
    }

    /// <summary>How far inward the rim looks at <paramref name="depth"/> px in from the edge: the
    /// circle profile, 0 at the band's inner end, <paramref name="bend"/> at the outline, its
    /// slope climbing steeply toward the edge (the thin mirrored band of a real glass rim).</summary>
    public static float RimBend(float depth, float band, float bend)
    {
        if (band <= 0 || depth >= band) return 0;
        var t = 1 - Math.Max(depth, 0) / band;
        return (1 - MathF.Sqrt(Math.Max(1 - t * t, 0))) * bend;
    }

    /// <summary>The shader's tone remap of one colour (0..1 channels): luminance 0..1 onto
    /// <paramref name="min"/>..<paramref name="max"/>, hue kept (<paramref name="curve"/>: see
    /// <see cref="GlassLensFrame.ToneCurve"/>; <paramref name="lift"/>: <see cref="GlassLensFrame.ToneLift"/>).</summary>
    public static (float R, float G, float B) Tone(float r, float g, float b, float min, float max, float curve = 1, float lift = 0)
    {
        var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        var s = curve == 1 ? l : 1 - MathF.Pow(Math.Clamp(1 - l, 0, 1), curve);
        var target = min + (max - min) * s;
        var k = target / Math.Max(l, 0.0005f);
        var veil = (1 - target) / Math.Max(1 - l, 0.0005f);
        var t = Math.Clamp(l / 0.06f, 0, 1);
        var hue = t * t * (3 - 2 * t);   // smoothstep: near black the hue is noise, lifted to grey
        float Hued(float c) => lift > 0 && target > l ? c * k + (1 - (1 - c) * veil - c * k) * lift : c * k;
        float Mix(float c) => Math.Clamp(target + (Hued(c) - target) * hue, 0, 1);
        return (Mix(r), Mix(g), Mix(b));
    }

    /// <summary>Where the lens samples the backdrop for device pixel <paramref name="coord"/>
    /// (green channel; red and blue spread by the dispersion).</summary>
    public static SKPoint SamplePoint(SKPoint coord, in GlassLensFrame f)
    {
        var hx = f.Bounds.Width / 2;
        var hy = f.Bounds.Height / 2;
        var cx = f.Bounds.MidX;
        var cy = f.Bounds.MidY;
        var px = coord.X - cx;
        var py = coord.Y - cy;
        var corner = px >= 0 ? (py <= 0 ? f.Radii.TopRight : f.Radii.BottomRight) : (py <= 0 ? f.Radii.TopLeft : f.Radii.BottomLeft);
        var r = Math.Min(corner, Math.Min(hx, hy));
        var at = new SKPoint(cx + px / f.Zoom, cy + py / f.Zoom);
        var depth = Math.Max(-SdRoundRect(px, py, hx, hy, r), 0);
        var d = RimBend(depth, f.Band, f.Bend);
        if (d == 0) return at;
        var n = Normal(px, py, hx, hy, Math.Min(r * 1.5f, Math.Min(hx, hy)));
        return new SKPoint(at.X - n.X * d, at.Y - n.Y * d);
    }

    // ---- Drawing ------------------------------------------------------------------------------

    /// <summary>
    /// Frosts <paramref name="source"/> (whose pixels sit at device rect <paramref name="at"/>)
    /// and draws it through the lens, clipped to <paramref name="clip"/>, at
    /// <paramref name="alpha"/>. Returns false when the lens cannot run here (shader rejected,
    /// no surface for the frost): the caller then draws the plain blur.
    /// </summary>
    internal static bool Draw(SKCanvas canvas, SKImage source, SKRectI at, SKPath clip, float sigma, float alpha,
        in GlassLensFrame frame, GRRecordingContext? context)
    {
        var effect = Lens(out _);
        if (effect is null) return false;
        // Only the panel's own pixels are ever sampled (the lens looks inward), so the frost
        // pass covers just the panel, not the 3σ ring the blur reads.
        var region = SKRectI.Intersect(GlassBlur.RoundOut(frame.Bounds), at);
        if (region.IsEmpty) return true;

        // A blur of half a pixel or less (the clamp's floor, what BlurRadius 0 asks for) is
        // skipped: the lens then reads the snapshot itself.
        SKImage? frosted = null;
        if (sigma > 0.5f)
        {
            frosted = Frost(source, at, region, sigma, context);
            if (frosted is null) return false;
        }
        // The spill's wide blur of the same pixels; without one the lens reads the content in its place.
        SKImage? wide = null;
        if (frame.Spill > 0 && frame.SpillSigma > 0.5f)
            wide = Frost(source, at, region, frame.SpillSigma, context);
        try
        {
            var image = frosted ?? source;
            var origin = frosted != null ? region.Location : at.Location;
            using var content = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear), SKMatrix.CreateTranslation(origin.X, origin.Y));
            using var spill = wide?.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear), SKMatrix.CreateTranslation(region.Left, region.Top));
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["bounds"] = new[] { frame.Bounds.Left, frame.Bounds.Top, frame.Bounds.Right, frame.Bounds.Bottom },
                ["radii"] = new[] { frame.Radii.TopLeft, frame.Radii.TopRight, frame.Radii.BottomRight, frame.Radii.BottomLeft },
                ["band"] = frame.Band,
                ["bend"] = frame.Bend,
                ["dispersion"] = frame.Dispersion,
                ["zoom"] = Math.Max(frame.Zoom, 0.01f),
                ["saturation"] = frame.Saturation,
                ["tone"] = new[] { Math.Clamp(frame.ToneMin, 0f, 1f), Math.Clamp(frame.ToneMax, 0f, 1f) },
                ["toneCurve"] = Math.Max(frame.ToneCurve, 0.01f),
                ["toneLift"] = Math.Clamp(frame.ToneLift, 0f, 1f),
                ["spill"] = spill is null ? 0f : Math.Clamp(frame.Spill, 0f, 1f),
                ["shade"] = Math.Clamp(frame.Shade, 0f, 1f),
            };
            var children = new SKRuntimeEffectChildren(effect) { ["content"] = content, ["wide"] = spill ?? content };
            using var shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint
            {
                Shader = shader,
                Color = new SKColor(255, 255, 255, GlassBlur.AlphaByte(alpha)),
                // Replaces what lies beneath rather than covering it (see GlassBlur.ClearForFrost).
                BlendMode = SKBlendMode.Plus,
            };
            var rect = SKRect.Create(region.Left, region.Top, region.Width, region.Height);
            canvas.Save();
            canvas.SetMatrix(SKMatrix.Identity);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            GlassBlur.ClearForFrost(canvas, rect, alpha);
            canvas.DrawRect(rect, paint);
            canvas.Restore();
            return true;
        }
        finally
        {
            frosted?.Dispose();
            wide?.Dispose();
        }
    }

    /// <summary>The backdrop blurred, cut to <paramref name="region"/> (device px). On the GPU
    /// the scratch target comes from Skia's texture cache, like the cache's compose pass.</summary>
    private static SKImage? Frost(SKImage source, SKRectI at, SKRectI region, float sigma, GRRecordingContext? context)
    {
        // Sized to the grid, so a panel scaling every frame keeps getting the cache's last texture
        // back (GlassBlur.Grid); the extra edge holds more of the blur and is never sampled.
        var size = GlassBlur.GridSize(region.Width, region.Height);
        var info = new SKImageInfo(size.Width, size.Height, source.ColorType, source.AlphaType, source.ColorSpace);
        using var surface = context != null ? SKSurface.Create(context, false, info) : SKSurface.Create(info);
        if (surface is null) return null;
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        c.DrawImage(source, at.Left - region.Left, at.Top - region.Top, paint);
        return surface.Snapshot();
    }

    /// <summary>Draws the rim light for a panel whose device geometry is <paramref name="bounds"/>
    /// / <paramref name="radii"/>. Sizes in device px; <paramref name="strength"/> 0..1.
    /// <paramref name="shape"/> defaults to <see cref="GlassRimShape.Diagonal"/>; <paramref name="additive"/>
    /// adds the light to what lies beneath (so it takes its colour) instead of laying white over it.</summary>
    internal static void DrawRim(SKCanvas canvas, SKRect bounds, GlassCornerRadii radii, float strength, float lineWidth,
        float glowWidth, float glow, float iridescence, GlassRimShape? shape = null, bool additive = false)
    {
        var effect = Rim(out _);
        if (effect is null || strength <= 0) return;
        var s = shape ?? GlassRimShape.Diagonal;
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["bounds"] = new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
            ["radii"] = new[] { radii.TopLeft, radii.TopRight, radii.BottomRight, radii.BottomLeft },
            ["strength"] = strength,
            ["lineWidth"] = lineWidth,
            ["glowWidth"] = glowWidth,
            ["glow"] = glow,
            ["iridescence"] = iridescence,
            ["light"] = new[] { s.LightX, s.LightY },
            ["shape"] = new[] { s.Ambient, s.Back, s.Power, s.GlowAmbient },
        };
        using var shader = effect.ToShader(uniforms);
        using var paint = new SKPaint { Shader = shader, BlendMode = additive ? SKBlendMode.Plus : SKBlendMode.SrcOver };
        canvas.Save();
        canvas.SetMatrix(SKMatrix.Identity);
        canvas.DrawRect(SKRect.Inflate(bounds, 1, 1), paint);
        canvas.Restore();
    }

    /// <summary>
    /// The device rect and radii of a local rounded rect under <paramref name="m"/>, or false
    /// when the matrix rotates, skews or adds perspective (the lens is axis-aligned).
    /// </summary>
    internal static bool TryMapToDevice(SKMatrix m, SKRect local, GlassCornerRadii localRadii, out SKRect device, out GlassCornerRadii radii, out float scale)
    {
        device = default;
        radii = default;
        scale = 1;
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1) return false;
        device = m.MapRect(local);
        var s = Math.Min(Math.Abs(m.ScaleX), Math.Abs(m.ScaleY));
        if (s < 0.001f) return false;
        scale = Math.Max(Math.Abs(m.ScaleX), Math.Abs(m.ScaleY));
        radii = new GlassCornerRadii(localRadii.TopLeft * s, localRadii.TopRight * s, localRadii.BottomRight * s, localRadii.BottomLeft * s);
        return true;
    }
}
