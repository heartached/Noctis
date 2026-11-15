using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Noctis.Mobile.Views;

/// <summary>
/// The wash's dither (<see cref="NowPlayingPage.Dither"/>: the same 64 px tile in the same 64 dp
/// cells), drawn from one repeating Skia shader made once per process. As a tiled ImageBrush the
/// lyrics page paid for it on every frame its lines moved: Avalonia draws a tiled image brush
/// through a new intermediate surface, image and shader each time it is drawn (headless S23 rig,
/// 11-15: 3.4 KB a frame and half the finalizable Skia objects of a playing lyrics page). Now
/// Playing, which seldom repaints, keeps the brush.
/// </summary>
public sealed class DitherLayer : Control
{
    private static SKPaint? _paint;
    private static IImmutableBrush? _fallback;
    private DitherOp? _op;

    /// <summary>The tile as a repeating shader, 1 px to 1 dp from the layer's top left, as the
    /// brush's DestinationRect places it.</summary>
    private static SKPaint Paint()
    {
        if (_paint != null) return _paint;
        var size = NowPlayingPage.DitherSize;
        var pixels = NowPlayingPage.DitherPixels();
        using var image = SKImage.FromPixelCopy(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
        var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Linear));
        return _paint = new SKPaint { Shader = shader };
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (_op == null || _op.Bounds != bounds) _op = new DitherOp(bounds);
        context.Custom(_op);
    }

    private sealed class DitherOp : ICustomDrawOperation
    {
        public DitherOp(Rect bounds) => Bounds = bounds;
        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                // Not Skia (headless tests): the brush itself.
                _fallback ??= (IImmutableBrush)((ImageBrush)NowPlayingPage.Dither).ToImmutable();
                context.FillRectangle(_fallback, Bounds);
                return;
            }
            using var lease = feature.Lease();
            lease.SkCanvas.DrawRect(Bounds.ToSKRect(), Paint());
        }
    }
}
