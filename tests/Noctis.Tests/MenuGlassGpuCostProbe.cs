using System.Diagnostics;
using System.Runtime.InteropServices;
using Noctis.Controls;
using Noctis.Mobile.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Opt-in GPU frame-cost probe for the phone's ⋯ menu pop (owner 11-14: "the 3 dots pop up is
/// still stuttery"). Runs only with NOCTIS_GLASS_PROBE set to an output file. Draws, on a real
/// GPU surface (ANGLE's GLES over D3D11, offscreen: no window), what the phone draws each frame
/// of the pop over the lyrics page at the S23's size: blurred lyric lines, the dim, the card's
/// shadow, the menu glass (GlassBackdropCache + GlassLens, as GlassBackdropOp drives them), its
/// tint, rim light and sheen, then the rows faded in a layer. Each frame is flushed and waited
/// for, so a row is CPU + GPU time per frame. A desktop GPU is far faster than the phone's, so
/// only the ratios between rows carry over.
/// </summary>
public class MenuGlassGpuCostProbe
{
    private const int W = 1080, H = 2340;
    private const float Dpi = 2.8125f;
    // The song menu over the lyrics page, opened up from the ⋯ low on the right (layout units).
    private static readonly SKRect Card = new(122, 360, 372, 790);
    private static readonly SKPoint Origin = new(352, 770);
    private const float Corner = 28;

    private readonly ITestOutputHelper _o;
    public MenuGlassGpuCostProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Probe()
    {
        var outFile = Environment.GetEnvironmentVariable("NOCTIS_GLASS_PROBE");
        if (string.IsNullOrEmpty(outFile)) return;
        // NOCTIS_GLASS_PROBE_RASTER=1: Skia's CPU raster instead (what the headless tests draw with).
        var raster = Environment.GetEnvironmentVariable("NOCTIS_GLASS_PROBE_RASTER") == "1";
        using var gl = raster ? null : AngleContext.Create();
        using var gr = gl is null ? null : GRContext.CreateGl(GRGlInterface.Create(gl.GetProc));
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = gr is null ? SKSurface.Create(info) : SKSurface.Create(gr, true, info);
        Assert.NotNull(surface);
        var lines = new List<string> { $"--- {DateTime.Now:HH:mm:ss} {(raster ? "raster " : "gpu ")}{Environment.GetEnvironmentVariable("NOCTIS_GLASS_PROBE_LABEL")}" };

        foreach (var (name, parts) in new (string, Parts)[]
                 {
                     ("lyrics + dim only", Parts.None),
                     ("+ shadow", Parts.Shadow),
                     ("+ lens only (no blur)", Parts.Shadow | Parts.Glass | Parts.NoBlur),
                     ("+ glass (no spill)", Parts.Shadow | Parts.Glass),
                     ("+ glass + spill", Parts.Shadow | Parts.Glass | Parts.Spill),
                     ("+ rim/tint/sheen", Parts.Shadow | Parts.Glass | Parts.Spill | Parts.Rim),
                     ("full (+ rows)", Parts.All),
                 })
        {
            foreach (var rest in new[] { false, true })
            {
                var (cpu, total, p95, worst) = Run(gr, surface, parts, rest, gl);
                var line = $"{(rest ? "rest" : "pop "),-4} {name,-22} cpu {cpu:F2} ms  cpu+gpu {total:F2} ms  p95 {p95:F2}  worst {worst:F2} ms";
                lines.Add(line);
                _o.WriteLine(line);
            }
        }
        File.AppendAllLines(outFile, lines);
    }

    [Flags]
    private enum Parts { None = 0, Shadow = 1, Glass = 2, Spill = 4, Rim = 8, Rows = 16, All = 31, NoBlur = 32 }

    /// <summary>Eleven pops (or as many frames at rest, the lyrics sweeping under the card); the
    /// first is warm-up. Returns ms per frame: CPU (to flush) and CPU+GPU (to finish), and the worst.</summary>
    private static (double Cpu, double Total, double P95, double Worst) Run(GRContext? gr, SKSurface surface, Parts parts, bool rest, AngleContext? gl)
    {
        var cache = new GlassBackdropCache();
        var spring = new PopSpringEase();
        const int frames = 41;   // 340 ms at 120 Hz
        double cpu = 0, total = 0;
        var all = new List<double>();
        var counted = 0;
        var tick = 0;
        var repaintWhole = true;
        for (var run = 0; run < 11; run++)
        {
            for (var f = 0; f < frames; f++, tick++)
            {
                var t = (double)f / (frames - 1);
                var scale = rest ? 1f : (float)(PopMenu.PopScale + (1 - PopMenu.PopScale) * spring.Ease(t));
                var fade = rest ? 1f : (float)PopMenu.PopFadeEase.Ease(Math.Min(1, t * PopMenu.PopOpenMs / PopMenu.PopFadeMs));
                var sw = Stopwatch.StartNew();
                // At rest the compositor repaints the sweeping line's band (partial) and, the frame
                // after, the whole panel the backdrop cache asked for; popping, every frame is new
                // render data and repaints the card's whole bounds (and the dim the whole screen).
                var whole = !rest || repaintWhole;
                var dirty = whole ? new SKRectI(0, 0, W, H) : new SKRectI(0, (int)(560 * Dpi), W, (int)(620 * Dpi));
                Frame(surface.Canvas, surface, cache, parts, scale, fade, tick, dirty, whole);
                repaintWhole = rest && !repaintWhole;
                surface.Flush();
                gr?.Flush();
                var c = sw.Elapsed.TotalMilliseconds;
                gl?.Finish();
                var ms = sw.Elapsed.TotalMilliseconds;
                if (run == 0) continue;
                cpu += c;
                total += ms;
                all.Add(ms);
                counted++;
            }
        }
        cache.Release();
        GlassBackdropCache.DrainRetired();
        all.Sort();
        return (cpu / counted, total / counted, all[all.Count * 95 / 100], all[^1]);
    }

    private static readonly SKFont LyricFont = new(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 30 * Dpi);
    // Under the card's matrix, so in layout units.
    private static readonly SKFont RowFont = new(SKTypeface.FromFamilyName("Segoe UI"), 17);

    private static void Frame(SKCanvas canvas, SKSurface surface, GlassBackdropCache cache, Parts parts, float scale, float fade,
        int tick, SKRectI dirty, bool whole)
    {
        canvas.Save();
        canvas.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height));
        canvas.Clear(new SKColor(40, 28, 52));
        // The lyrics: eight lines, those off the sung one blurred by their distance, the sung
        // line's sweep moving every frame.
        for (var i = 0; i < 8; i++)
        {
            var y = (180 + i * 90) * Dpi;
            var distance = Math.Abs(i - 4);
            using var blur = distance > 0 ? SKImageFilter.CreateBlur(distance * 1.5f * Dpi, distance * 1.5f * Dpi) : null;
            using var layer = new SKPaint { ImageFilter = blur };
            canvas.SaveLayer(SKRect.Create(0, y - 60 * Dpi, W, 90 * Dpi), layer);
            var sweep = i == 4 ? (tick % 120) / 120f : 0;
            using var ink = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(24 * Dpi, 0), new SKPoint(360 * Dpi, 0),
                    new[] { SKColors.White, SKColors.White.WithAlpha(110), SKColors.White.WithAlpha(110) },
                    new[] { 0f, sweep, 1f }, SKShaderTileMode.Clamp),
            };
            canvas.DrawText("I'm tellin' you all it", 24 * Dpi, y, LyricFont, ink);
            canvas.DrawText("all falls down", 24 * Dpi, y + 40 * Dpi, LyricFont, ink);
            canvas.Restore();
        }
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(0x2E * fade)) })
            canvas.DrawRect(0, 0, W, H, dim);

        // The card: the RenderTransform's scale about the ⋯ button, under the DPI scale.
        var m = SKMatrix.CreateScale(Dpi, Dpi).PreConcat(SKMatrix.CreateScale(scale, scale, Origin.X, Origin.Y));
        canvas.Save();
        canvas.SetMatrix(m);
        var rrect = new SKRoundRect(Card, Corner);
        if ((parts & Parts.Shadow) != 0)
        {
            // Avalonia's BoxShadow (MenuGlassShadow, dark): a 0.8 contact line and a 40 soft drop.
            DrawShadow(canvas, rrect, 0, 0, 0.8f, 0.3f, new SKColor(0, 0, 0, (byte)(0x73 * fade)));
            DrawShadow(canvas, rrect, 0, 12, 40, 0, new SKColor(0, 0, 0, (byte)(0x33 * fade)));
        }
        if ((parts & Parts.Glass) != 0)
        {
            using var path = new SKPath();
            path.AddRoundRect(rrect);
            path.Transform(m);
            var s = Math.Max(Math.Abs(m.ScaleX), Math.Abs(m.ScaleY));
            GlassLens.TryMapToDevice(m, Card, new GlassCornerRadii(Corner, Corner, Corner, Corner), out var device, out var radii, out _);
            var spill = (parts & Parts.Spill) != 0 ? 0.58f : 0f;
            var lens = new GlassLensFrame(device, radii, 14 * s, 10 * s, 0, 1, 1.2f, 0.22f, 0.41f, 2.5f, 1, spill, Math.Max(9 * 4, 16) * s, 0.16f);
            cache.Draw(canvas, surface, path, (parts & Parts.NoBlur) != 0 ? 0 : 9 * s, fade, whole, lens);
        }
        if ((parts & Parts.Rim) != 0)
        {
            using (var tint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(0x0A * fade)) })
                canvas.DrawRoundRect(rrect, tint);
            GlassLens.TryMapToDevice(m, Card, new GlassCornerRadii(Corner, Corner, Corner, Corner), out var device, out var radii, out var s);
            GlassLens.DrawRim(canvas, device, radii, 0.24f * fade, 0.9f * s, 3 * s, 0.3f, 0, GlassRimShape.TopCatch, additive: true);
            using var sheen = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, Card.Top), new SKPoint(0, Card.Top + Card.Height * 0.25f),
                    new[] { new SKColor(255, 255, 255, (byte)(0x0A * fade)), SKColors.Transparent }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(rrect, sheen);
        }
        if ((parts & Parts.Rows) != 0)
        {
            // The rows, faded with Opacity: a layer.
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * fade)) };
            canvas.SaveLayer(Card, layer);
            using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var hair = new SKPaint { Color = new SKColor(255, 255, 255, 0x2E) };
            string[] rows = { "Add to Playlist", "Play Next", "Add to Queue", "Pin", "Go to Album", "Go to Artist", "Lyrics", "Share Song" };
            for (var i = 0; i < rows.Length; i++)
            {
                var y = Card.Top + 80 + i * 44;
                canvas.DrawText(rows[i], Card.Left + 20, y, RowFont, ink);
                canvas.DrawRect(Card.Left + 20, y + 14, Card.Width - 40, 0.5f, hair);
            }
            canvas.Restore();
        }
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>Avalonia.Skia's box shadow: the rounded rect, spread and offset, through a blur
    /// mask of its radius converted to sigma, clipped out of the shape.</summary>
    private static void DrawShadow(SKCanvas canvas, SKRoundRect rrect, float dx, float dy, float blur, float spread, SKColor color)
    {
        var shadow = new SKRoundRect(rrect);
        shadow.Inflate(spread, spread);
        shadow.Offset(dx, dy);
        using var filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur > 0 ? 0.57735f * blur + 0.5f : 0);
        using var paint = new SKPaint { IsAntialias = true, Color = color, MaskFilter = filter };
        canvas.Save();
        canvas.ClipRoundRect(rrect, SKClipOperation.Difference, true);
        canvas.DrawRoundRect(shadow, paint);
        canvas.Restore();
    }

    /// <summary>An offscreen GLES 3 context on ANGLE (Avalonia's av_libglesv2, D3D11): a 1x1
    /// pbuffer, never a window.</summary>
    private sealed class AngleContext : IDisposable
    {
        private readonly IntPtr _lib, _display, _surface, _context;
        private readonly Action _finish;

        private AngleContext(IntPtr lib, IntPtr display, IntPtr surface, IntPtr context)
        {
            _lib = lib;
            _display = display;
            _surface = surface;
            _context = context;
            _finish = Marshal.GetDelegateForFunctionPointer<Action>(GetProc("glFinish"));
        }

        public static AngleContext Create()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "av_libglesv2.dll");
            var lib = NativeLibrary.Load(path);
            // Avalonia's ANGLE build exports EGL as EGL_GetDisplay etc.; GL entry points come from EGL_GetProcAddress.
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, "EGL_" + name[3..]));
            var display = Fn<GetDisplay>("eglGetDisplay")(IntPtr.Zero);
            Assert.True(Fn<Initialize>("eglInitialize")(display, out _, out _));
            Fn<BindApi>("eglBindAPI")(0x30A0);
            var configs = new IntPtr[1];
            Assert.True(Fn<ChooseConfig>("eglChooseConfig")(display,
                new[] { 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3033, 1, 0x3040, 0x40, 0x3038 }, configs, 1, out var n) && n > 0);
            var surface = Fn<CreatePbuffer>("eglCreatePbufferSurface")(display, configs[0], new[] { 0x3057, 1, 0x3056, 1, 0x3038 });
            var context = Fn<CreateContext>("eglCreateContext")(display, configs[0], IntPtr.Zero, new[] { 0x3098, 3, 0x3038 });
            Assert.NotEqual(IntPtr.Zero, context);
            Assert.True(Fn<MakeCurrent>("eglMakeCurrent")(display, surface, surface, context));
            return new AngleContext(lib, display, surface, context);
        }

        public IntPtr GetProc(string name)
        {
            var get = Marshal.GetDelegateForFunctionPointer<GetProcAddress>(NativeLibrary.GetExport(_lib, "EGL_GetProcAddress"));
            var p = get(name);
            return p != IntPtr.Zero ? p : NativeLibrary.TryGetExport(_lib, "GL_" + name[2..], out var e) ? e : IntPtr.Zero;
        }

        public void Finish() => _finish();

        public void Dispose()
        {
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, "EGL_" + name[3..]));
            Fn<MakeCurrent>("eglMakeCurrent")(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Fn<DestroyContext>("eglDestroyContext")(_display, _context);
            Fn<DestroySurface>("eglDestroySurface")(_display, _surface);
        }

        private delegate IntPtr GetDisplay(IntPtr nativeDisplay);
        private delegate bool Initialize(IntPtr display, out int major, out int minor);
        private delegate bool BindApi(int api);
        private delegate bool ChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs, int size, out int count);
        private delegate IntPtr CreatePbuffer(IntPtr display, IntPtr config, int[] attribs);
        private delegate IntPtr CreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribs);
        private delegate bool MakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
        private delegate bool DestroyContext(IntPtr display, IntPtr context);
        private delegate bool DestroySurface(IntPtr display, IntPtr surface);
        private delegate IntPtr GetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    }
}
