using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Noctis.Plugins.AlbumRoulette.Roulette;

namespace Noctis.Plugins.AlbumRoulette.Views;

/// <summary>
/// The stage: a large cover on a soft accent glow, with the reel's neighbours peeking above and
/// below while it spins. Frames come from <see cref="TopLevel.RequestAnimationFrame"/> only while
/// something moves, and stop while the window is hidden or minimized (the spin waits there).
/// A frame steps <see cref="ReelMotion"/> and draws covers that are already decoded at the size
/// they are drawn (<see cref="CoverCache"/>), with immutable brushes: it allocates nothing.
/// </summary>
public sealed class ReelCanvas : Control
{
    /// <summary>Slot spacing, as a share of the cover size.</summary>
    public const double Spacing = 0.80;

    private readonly Bitmap?[] _images = new Bitmap?[ReelMotion.MaxSlots];
    private readonly Rect[] _sources = new Rect[ReelMotion.MaxSlots];
    private readonly IImmutableBrush?[] _placeholders = new IImmutableBrush?[ReelMotion.MaxSlots];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<TimeSpan> _onFrame;
    private readonly DispatcherTimer _idle;
    private TimeSpan _last;
    private bool _framePending;
    private bool _attached;
    private RouletteTheme _theme = RouletteTheme.Dark;
    private RenderTargetBitmap? _glow;
    private (int W, int H, RouletteTheme? Theme) _glowKey;
    private WriteableBitmap? _fade;
    private (int H, RouletteTheme? Theme) _fadeKey;

    public ReelCanvas()
    {
        _onFrame = OnFrame;
        _idle = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => CheckIdle());
        ClipToBounds = true;
    }

    public ReelMotion Motion { get; } = new();

    /// <summary>The reel stopped on its pick (UI thread).</summary>
    public event EventHandler? Landed;

    /// <summary>Frames drawn (tests).</summary>
    public int Frames { get; private set; }

    /// <summary>True while a spin waits for the window to be shown again.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Tests: frames come only from <see cref="AdvanceFrame"/>.</summary>
    public bool ManualFrames { get; set; }

    public RouletteTheme Palette
    {
        get => _theme;
        set { _theme = value; _glowKey = default; _fadeKey = default; InvalidateVisual(); }
    }

    /// <summary>Cover size in DIPs for the current bounds.</summary>
    public double CoverSize => CoverSizeFor(Bounds.Size);

    public static double CoverSizeFor(Size size) => Math.Max(40, Math.Min(size.Width * 0.62, size.Height * 0.80));

    /// <summary>What slot <paramref name="slot"/> shows: its cover (null while decoding or when it has none) and the tile behind it.</summary>
    public void SetSlot(int slot, Cover? cover, IImmutableBrush placeholder)
    {
        if ((uint)slot >= ReelMotion.MaxSlots) return;
        _images[slot] = cover?.Image;
        _sources[slot] = cover?.Source ?? default;
        _placeholders[slot] = placeholder;
    }

    /// <summary>Starts drawing frames (a spin started).</summary>
    public void Run()
    {
        _last = _clock.Elapsed;
        IsPaused = false;
        RequestFrame();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _last = _clock.Elapsed;
        if (Motion.IsMoving) RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _idle.Stop();
        _glow?.Dispose();
        _glow = null;
        _glowKey = default;
        _fade?.Dispose();
        _fade = null;
        _fadeKey = default;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Hidden, zero-sized or in a minimized window: nothing to animate.</summary>
    public bool ShouldPause()
        => !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1
           || TopLevel.GetTopLevel(this) is not { } top
           || top is Window { WindowState: WindowState.Minimized };

    private void RequestFrame()
    {
        if (_framePending || !_attached || ManualFrames || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(_onFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        if (!_attached || ManualFrames) return;
        if (ShouldPause())
        {
            IsPaused = true;
            _idle.Start();
            return;
        }
        var now = _clock.Elapsed;
        var dt = (now - _last).TotalSeconds;
        _last = now;
        AdvanceFrame(dt);
        if (Motion.IsMoving) RequestFrame();
    }

    private void CheckIdle()
    {
        if (!_attached) { _idle.Stop(); return; }
        if (ShouldPause()) return;
        _idle.Stop();
        IsPaused = false;
        _last = _clock.Elapsed; // the spin carries on where it stopped
        RequestFrame();
    }

    /// <summary>One frame: steps the reel by <paramref name="dt"/> seconds and asks for a redraw.</summary>
    public void AdvanceFrame(double dt)
    {
        Frames++;
        var landed = Motion.Step(dt);
        InvalidateVisual();
        if (landed) Landed?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context) => Draw(context, Bounds.Size);

    /// <summary>Draws the stage into <paramref name="ctx"/> (also used by tests and the preview).</summary>
    public void Draw(DrawingContext ctx, Size size)
    {
        if (size.Width < 1 || size.Height < 1) return;
        var s = CoverSizeFor(size);
        var cx = size.Width / 2;
        var cy = size.Height / 2;
        var landing = Motion.Landing;
        var landed = !Motion.IsSpinning && Motion.Length > 0;

        // The glow: steady while it spins, a swell when it lands.
        var swell = landed && landing < 1 ? Math.Sin(Math.PI * landing) : 0;
        // It fades out inside the stage (no hard edge where the stage clips). A gradient brush
        // costs Skia a conversion on every draw, so the glow is drawn once into a bitmap and
        // that is drawn, a little bigger when it swells.
        var rx = Math.Min(s * 0.86, cx);
        var ry = Math.Min(s * 0.86, cy);
        if (Glow(rx, ry) is { } glow)
        {
            var grow = 1 + 0.08 * swell;
            ctx.DrawImage(glow, new Rect(0, 0, glow.PixelSize.Width, glow.PixelSize.Height),
                new Rect(cx - rx * grow, cy - ry * grow, 2 * rx * grow, 2 * ry * grow));
        }

        if (Motion.Length == 0) return;
        var offset = Motion.Offset;
        var centre = (int)Math.Round(offset);
        // Far neighbours first, the centre cover last (on top).
        for (var ring = 2; ring >= 1; ring--)
        {
            DrawSlot(ctx, centre - ring, offset, s, cx, cy);
            DrawSlot(ctx, centre + ring, offset, s, cx, cy);
        }
        DrawSlot(ctx, centre, offset, s, cx, cy);
        // The stage's top and bottom edges fade into the page, like the window of a slot reel.
        if (Fade(size.Height) is { } fade)
            ctx.DrawImage(fade, new Rect(0, 0, fade.PixelSize.Width, fade.PixelSize.Height), new Rect(0, 0, size.Width, size.Height));
    }

    /// <summary>A strip of page colour, opaque at the top and bottom edges and clear in the middle
    /// (made when the height or palette changes; drawn stretched over the stage).</summary>
    private WriteableBitmap? Fade(double height)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var h = Math.Max(4, (int)Math.Ceiling(height * scaling));
        if (_fade is not null && _fadeKey.H == h && ReferenceEquals(_fadeKey.Theme, _theme)) return _fade;
        _fade?.Dispose();
        _fade = null;
        try
        {
            var bitmap = new WriteableBitmap(new PixelSize(4, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var page = _theme.PageColor;
            using (var fb = bitmap.Lock())
            {
                var edge = h * 0.2;
                var row = new byte[fb.RowBytes];
                for (var y = 0; y < h; y++)
                {
                    var d = Math.Min(y + 0.5, h - y - 0.5);
                    var t = Math.Clamp(1 - d / edge, 0, 1);
                    var a = t * t * (3 - 2 * t); // smooth
                    for (var x = 0; x < 4; x++)
                    {
                        row[x * 4] = (byte)Math.Round(page.B * a);
                        row[x * 4 + 1] = (byte)Math.Round(page.G * a);
                        row[x * 4 + 2] = (byte)Math.Round(page.R * a);
                        row[x * 4 + 3] = (byte)Math.Round(255 * a);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
                }
            }
            _fade = bitmap;
            _fadeKey = (h, _theme);
        }
        catch (Exception)
        {
            _fadeKey = default;
        }
        return _fade;
    }

    /// <summary>The glow bitmap for this size and palette, made when either changes.</summary>
    private RenderTargetBitmap? Glow(double rx, double ry)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var w = Math.Max(2, (int)Math.Ceiling(2 * rx * scaling));
        var h = Math.Max(2, (int)Math.Ceiling(2 * ry * scaling));
        if (_glow is not null && _glowKey.W == w && _glowKey.H == h && ReferenceEquals(_glowKey.Theme, _theme)) return _glow;
        _glow?.Dispose();
        _glow = null;
        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext())
                ctx.DrawEllipse(_theme.GlowAt(0.9), null, new Point(w / 2.0, h / 2.0), w / 2.0, h / 2.0);
            _glow = bitmap;
            _glowKey = (w, h, _theme);
        }
        catch (Exception)
        {
            _glowKey = default; // no render target here: no glow
        }
        return _glow;
    }

    private void DrawSlot(DrawingContext ctx, int slot, double offset, double s, double cx, double cy)
    {
        if (slot < 0 || slot >= Motion.Length) return;
        var d = slot - offset;
        var ad = Math.Abs(d);
        if (ad > 2.2) return;
        var near = Math.Min(ad, 1.5) / 1.5;
        var scale = 1 - 0.30 * near;
        if (ad < 0.5) scale *= Motion.PopScale;
        var opacity = 1 - 0.78 * Math.Min(ad, 1.4) / 1.4;
        var size = s * scale;
        var y = cy + d * s * Spacing;
        var rect = new Rect(cx - size / 2, y - size / 2, size, size);
        var rounded = new RoundedRect(rect, size * CoverCache.CornerShare);
        // No pushed clip or opacity (each push costs the drawing context an allocation): the
        // covers come with their corners already rounded, and a neighbour fades by a veil of
        // the page colour drawn over it.
        if (_images[slot] is { } image)
            ctx.DrawImage(image, _sources[slot], rect);
        else
        {
            ctx.DrawRectangle(_placeholders[slot] ?? _theme.Placeholders[0], null, rounded);
            ctx.DrawEllipse(_theme.PlaceholderGlyph, null, rect.Center, size * 0.16, size * 0.16);
        }
        ctx.DrawRectangle(null, _theme.CoverEdge, rounded);
        if (opacity < 0.99) ctx.DrawRectangle(_theme.Veil(1 - opacity), null, rounded);
    }
}
