using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Noctis.Plugins.Kawarp;

/// <summary>
/// Kawarp: the current cover, blurred, then continuously domain-warped by a Skia runtime
/// shader (SkSL) so it flows like liquid behind the lyrics, swelling on every beat from the
/// host's beat tap. Settings live in <c>settings.json</c> inside the plugin's data folder.
/// </summary>
public sealed class KawarpPlugin : INoctisPlugin
{
    public PluginInfo Info { get; } = new(
        Id: "dev.noctis.plugins.kawarp",
        Name: "Kawarp",
        Version: "1.0.0",
        Author: "Noctis",
        Description: "Fluid, warped album-art background behind the lyrics. Reacts to the beat. Edit settings.json in the plugin's data folder to tune warp, blur, speed and saturation.");

    public void Initialize(IPluginHost host)
    {
        var settings = KawarpSettings.Load(Path.Combine(host.DataDirectory, "settings.json"));
        host.RegisterVisualLayer(new Layer(host, settings));
        host.Log($"initialized (warp {settings.WarpIntensity}, blur {settings.BlurPasses}, speed {settings.AnimationSpeed}, saturation {settings.Saturation})");
    }

    public void Shutdown() { }

    private sealed class Layer : IVisualLayerProvider
    {
        private readonly IPluginHost _host;
        private readonly KawarpSettings _settings;
        public Layer(IPluginHost host, KawarpSettings settings) { _host = host; _settings = settings; }
        public string Name => "Kawarp background";
        public Control CreateLayer() => new KawarpControl(_host, _settings);
    }
}

/// <summary>User-tunable knobs, same names and ranges as the original browser extension.</summary>
public sealed class KawarpSettings
{
    /// <summary>0–3: how far the artwork is pushed around.</summary>
    public double WarpIntensity { get; set; } = 1.0;
    /// <summary>1–16: box-blur passes on the source; more = dreamier.</summary>
    public int BlurPasses { get; set; } = 6;
    /// <summary>0–3: speed of the flow.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>0–3: colour intensity (1 = as the cover).</summary>
    public double Saturation { get; set; } = 1.3;
    /// <summary>0–1: how dark the result is kept so lyrics stay readable.</summary>
    public double Dim { get; set; } = 0.55;
    /// <summary>True: the warp swells with the beat.</summary>
    public bool BeatReactive { get; set; } = true;

    public KawarpSettings Clamped() => new()
    {
        WarpIntensity = Math.Clamp(WarpIntensity, 0, 3),
        BlurPasses = Math.Clamp(BlurPasses, 1, 16),
        AnimationSpeed = Math.Clamp(AnimationSpeed, 0, 3),
        Saturation = Math.Clamp(Saturation, 0, 3),
        Dim = Math.Clamp(Dim, 0, 1),
        BeatReactive = BeatReactive,
    };

    /// <summary>Reads the file, or writes the defaults so the user has something to edit.</summary>
    public static KawarpSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<KawarpSettings>(File.ReadAllText(path)) ?? new KawarpSettings()).Clamped();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var defaults = new KawarpSettings();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            return defaults;
        }
        catch { return new KawarpSettings(); }
    }
}

/// <summary>The SkSL program and the CPU-side prep it needs. Kept static so the effect compiles once.</summary>
public static class KawarpShader
{
    /// <summary>
    /// Domain warp: two layers of drifting sines displace the sample position; the cover is
    /// sampled with mirrored tiling so the edges never show. Then saturation and dimming.
    /// Written against SkiaSharp 2.88's SkSL dialect (<c>uniform shader</c> + <c>sample()</c>).
    /// </summary>
    public const string Source = """
        uniform shader art;
        uniform float2 iResolution;
        uniform float2 iArtSize;
        uniform float iTime;
        uniform float iWarp;
        uniform float iSaturation;
        uniform float iDim;
        uniform float iBeat;

        half4 main(float2 fragCoord) {
            float2 uv = fragCoord / iResolution;
            float2 q = uv * 2.0 - 1.0;
            float t = iTime;
            float2 d = float2(sin(q.y * 3.1 + t * 0.70) + sin(q.x * 2.3 - t * 0.50),
                              cos(q.x * 2.7 + t * 0.60) + cos(q.y * 1.9 - t * 0.40));
            d += 0.5 * float2(sin((q.x + q.y) * 4.0 + t * 1.30), cos((q.x - q.y) * 3.5 - t * 1.10));
            float amount = 0.12 * iWarp * (1.0 + 0.45 * iBeat);
            float2 w = uv + d * amount;
            // Slow drift so a still image never looks frozen even at zero warp.
            w += float2(0.03 * sin(t * 0.21), 0.03 * cos(t * 0.17));
            half4 c = sample(art, w * iArtSize);
            half l = dot(c.rgb, half3(0.299, 0.587, 0.114));
            c.rgb = mix(half3(l), c.rgb, half(iSaturation));
            c.rgb *= half(iDim);
            return half4(c.rgb, 1.0);
        }
        """;

    private static SKRuntimeEffect? _effect;
    private static string? _error;

    /// <summary>The compiled effect, or null with <paramref name="error"/> set when the GPU/Skia build rejects it.</summary>
    public static SKRuntimeEffect? Get(out string? error)
    {
        if (_effect is null && _error is null)
        {
            _effect = SKRuntimeEffect.Create(Source, out var err);
            if (_effect is null) _error = string.IsNullOrEmpty(err) ? "unknown shader error" : err;
        }
        error = _error;
        return _effect;
    }

    /// <summary>
    /// Downscales the cover to <paramref name="size"/> px and runs <paramref name="passes"/> separable
    /// box blurs. Done once per artwork on a worker thread; the shader then only samples.
    /// </summary>
    public static SKBitmap PrepareArtwork(SKBitmap source, int size, int passes)
    {
        var small = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(source, new SKRect(0, 0, size, size), paint);
        }
        var px = small.Pixels;
        var tmp = new SKColor[px.Length];
        for (var p = 0; p < passes; p++)
        {
            BoxBlur(px, tmp, size, horizontal: true);
            BoxBlur(tmp, px, size, horizontal: false);
        }
        small.Pixels = px;
        return small;
    }

    /// <summary>3-tap box blur along one axis with mirrored edges (pure; tests).</summary>
    public static void BoxBlur(SKColor[] src, SKColor[] dst, int size, bool horizontal)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            int r = 0, g = 0, b = 0;
            for (var k = -1; k <= 1; k++)
            {
                var xx = horizontal ? Mirror(x + k, size) : x;
                var yy = horizontal ? y : Mirror(y + k, size);
                var c = src[yy * size + xx];
                r += c.Red; g += c.Green; b += c.Blue;
            }
            dst[y * size + x] = new SKColor((byte)(r / 3), (byte)(g / 3), (byte)(b / 3));
        }
    }

    private static int Mirror(int i, int n) => i < 0 ? -i : i >= n ? 2 * n - i - 2 : i;
}

/// <summary>The Avalonia control: owns the prepared artwork, ticks on the frame clock, and hands a Skia draw operation to the renderer.</summary>
internal sealed class KawarpControl : Control
{
    private const int ArtSize = 160;

    private readonly IPluginHost _host;
    private readonly KawarpSettings _settings;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private SharedImage? _art;
    private string? _artPath;
    private int _artGeneration;
    private double _beat;
    private bool _running;

    public KawarpControl(IPluginHost host, KawarpSettings settings)
    {
        _host = host;
        _settings = settings;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.NowPlaying.TrackChanged += OnTrackChanged;
        LoadArtwork(_host.NowPlaying.Track?.ArtworkPath);
        if (!_running && TopLevel.GetTopLevel(this) is { } top)
        {
            _running = true;
            top.RequestAnimationFrame(OnFrame);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _host.NowPlaying.TrackChanged -= OnTrackChanged;
        _running = false;
        _art?.Release();
        _art = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTrackChanged(object? sender, EventArgs e) => LoadArtwork(_host.NowPlaying.Track?.ArtworkPath);

    private void LoadArtwork(string? path)
    {
        if (path == _artPath) return;
        _artPath = path;
        var generation = ++_artGeneration;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { _art?.Release(); _art = null; InvalidateVisual(); return; }
        var passes = _settings.BlurPasses;
        _ = Task.Run(() =>
        {
            try
            {
                using var decoded = SKBitmap.Decode(path);
                if (decoded is null) return;
                using var prepared = KawarpShader.PrepareArtwork(decoded, ArtSize, passes);
                var image = new SharedImage(SKImage.FromBitmap(prepared));
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _artGeneration) { image.Release(); return; }
                    _art?.Release();
                    _art = image;
                    InvalidateVisual();
                });
            }
            catch (Exception ex) { _host.Log("artwork failed: " + ex.Message); }
        });
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running) return;
        var target = _settings.BeatReactive && _host.Beat.TryRead(out var pulse) ? pulse : 0;
        _beat += (target - _beat) * (target > _beat ? 0.5 : 0.08);
        InvalidateVisual();
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else _running = false;
    }

    public override void Render(DrawingContext context)
    {
        if (_art is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var time = _clock.Elapsed.TotalSeconds * _settings.AnimationSpeed;
        context.Custom(new KawarpDrawOp(new Rect(Bounds.Size), _art, (float)time, _settings, (float)_beat));
    }
}

/// <summary>
/// A Skia image shared between the UI thread (which swaps covers and tears the control down)
/// and the compositor's render thread (which draws queued <see cref="KawarpDrawOp"/>s a frame
/// or two later). Disposing the <see cref="SKImage"/> the moment the UI thread was done with it
/// crashed the process in native <c>sk_image_make_shader</c> whenever a frame was still in
/// flight — so the image now lives until its last holder lets go. Starts with one reference
/// for the creator; every draw op retains it and releases it in its own Dispose.
/// </summary>
public sealed class SharedImage
{
    private SKImage? _image;
    private int _refs = 1;

    public SharedImage(SKImage image) => _image = image;

    /// <summary>The image, or null once every reference is gone.</summary>
    public SKImage? Image => _image;
    public int Width => _image?.Width ?? 0;
    public int Height => _image?.Height ?? 0;
    public bool IsAlive => Volatile.Read(ref _refs) > 0;

    /// <summary>Adds a holder. False (nothing retained) when the image has already been freed.</summary>
    public bool TryRetain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refs);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _refs, current + 1, current) == current) return true;
        }
    }

    /// <summary>Drops a holder; the last one out disposes the Skia image.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        var img = Interlocked.Exchange(ref _image, null);
        img?.Dispose();
    }
}

/// <summary>Draws the shader through Avalonia's Skia lease; a no-op on renderers without one.</summary>
internal sealed class KawarpDrawOp : ICustomDrawOperation
{
    private readonly SharedImage? _art;
    private readonly float _time;
    private readonly KawarpSettings _settings;
    private readonly float _beat;

    public KawarpDrawOp(Rect bounds, SharedImage art, float time, KawarpSettings settings, float beat)
    {
        Bounds = bounds; _time = time; _settings = settings; _beat = beat;
        // Hold the cover for as long as this op can still be rendered; a retain that fails means
        // the UI thread already freed it, and Render then simply draws nothing.
        _art = art.TryRetain() ? art : null;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;

    /// <summary>Called by the compositor once the op is retired — the render thread is done with the image.</summary>
    public void Dispose() => _art?.Release();

    public void Render(ImmediateDrawingContext context)
    {
        var image = _art?.Image;
        if (image is null) return;
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        var effect = KawarpShader.Get(out _);
        if (effect is null) return;

        using var api = lease.Lease();
        var canvas = api.SkCanvas;

        using var artShader = image.ToShader(SKShaderTileMode.Mirror, SKShaderTileMode.Mirror);
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["iResolution"] = new[] { (float)Bounds.Width, (float)Bounds.Height },
            ["iArtSize"] = new[] { (float)image.Width, (float)image.Height },
            ["iTime"] = _time,
            ["iWarp"] = (float)_settings.WarpIntensity,
            ["iSaturation"] = (float)_settings.Saturation,
            ["iDim"] = (float)_settings.Dim,
            ["iBeat"] = _beat,
        };
        var children = new SKRuntimeEffectChildren(effect) { ["art"] = artShader };
        using var shader = effect.ToShader(true, uniforms, children);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom), paint);
    }
}
