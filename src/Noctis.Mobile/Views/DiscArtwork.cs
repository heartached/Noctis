using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using SkiaSharp;

namespace Noctis.Mobile.Views;

/// <summary>
/// Now Playing's record and CD (Settings › Appearance › Now Playing Artwork; tester 11-04: "now
/// playing artwork (especially vinyls, CDs) would go HARD").
/// <para>
/// Cheap by construction. The disc is painted once per cover, style and size into a bitmap at the
/// screen's own pixel size (<see cref="DiscPainter"/>), and so is its light (the sheen, which stays
/// put while the record turns under it, as a lamp's would). Turning is a composition visual's
/// (<see cref="DiscSpinHandler"/>): on the render thread it advances an angle and draws the two
/// bitmaps, the disc rotated, nothing re-painted, nothing allocated, no layout. The UI thread only
/// sends it messages: play, pause, awake, asleep, a new pair of bitmaps.
/// </para>
/// <para>
/// Pause eases the record to a stop and Play eases it back up from where it stopped
/// (<see cref="DiscSpin"/>); hidden (the page closed, under the lyrics, another style) or with the
/// app in the background (screen off), it stops asking for frames at once and keeps its angle.
/// </para>
/// </summary>
public sealed class DiscArtwork : Control
{
    public static readonly StyledProperty<NowPlayingArtworkStyle> KindProperty =
        AvaloniaProperty.Register<DiscArtwork, NowPlayingArtworkStyle>(nameof(Kind), NowPlayingArtworkStyle.Vinyl);

    /// <summary>The cover to print on the disc (Now Playing's decoded artwork).</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<DiscArtwork, IImage?>(nameof(Source));

    /// <summary>Playing: the disc turns (eased up and down).</summary>
    public static readonly StyledProperty<bool> IsSpinningProperty =
        AvaloniaProperty.Register<DiscArtwork, bool>(nameof(IsSpinning));

    /// <summary>The page is on screen and the app in front (NowPlayingPage). Off, no frames.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DiscArtwork, bool>(nameof(IsActive), true);

    public NowPlayingArtworkStyle Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsSpinning { get => GetValue(IsSpinningProperty); set => SetValue(IsSpinningProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private CompositionCustomVisual? _visual;
    private DiscSpinHandler? _handler;
    private bool _buildQueued;
    // What the last bitmaps were painted for: a repaint only when one of these changes.
    private IImage? _paintedSource;
    private NowPlayingArtworkStyle _paintedKind;
    private int _paintedPixels;
    // The light depends on the style and size only: kept across covers.
    private SKImage? _sheen;
    private (NowPlayingArtworkStyle Kind, int Pixels) _sheenKey;

    /// <summary>The handler (tests read its spin; null until attached to a composited tree).</summary>
    internal DiscSpinHandler? Handler => _handler;

    /// <summary>How many times the disc has been painted (tests: once per cover, never per frame).</summary>
    internal int Paints { get; private set; }

    /// <summary>The side of the bitmaps last painted, in device pixels (tests: crisp at the density).</summary>
    internal int PaintedPixels => _paintedPixels;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;
        _handler = new DiscSpinHandler();
        _visual = compositor.CreateCustomVisual(_handler);
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _paintedSource = null;
        _paintedPixels = 0;
        SendState();
        QueueBuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_visual != null)
        {
            _visual.SendHandlerMessage(DiscSpinHandler.Release);   // the render thread lets go of the bitmaps
            ElementComposition.SetElementChildVisual(this, null);
        }
        _visual = null;
        _handler = null;
        _sheen = null;
        _paintedSource = null;
        _paintedPixels = 0;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual != null) _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
        QueueBuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSpinningProperty || change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
        {
            SendState();
            if (change.Property == IsVisibleProperty) QueueBuild();
        }
        else if (change.Property == SourceProperty || change.Property == KindProperty)
        {
            QueueBuild();
        }
    }

    /// <summary>Whether the handler may ask for frames: shown, on an open page, the app in front.</summary>
    internal bool Awake => IsVisible && IsActive;

    private void SendState()
    {
        if (_visual == null) return;
        _visual.SendHandlerMessage(IsSpinning ? DiscSpinHandler.Play : DiscSpinHandler.Pause);
        _visual.SendHandlerMessage(Awake ? DiscSpinHandler.Wake : DiscSpinHandler.Sleep);
    }

    /// <summary>A cover, style and size often change together (a new track, a rotation): one paint
    /// after them, not one each.</summary>
    private void QueueBuild()
    {
        if (_buildQueued || _visual == null) return;
        _buildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _buildQueued = false;
            Build();
        }, DispatcherPriority.Render);
    }

    /// <summary>The disc's side in device pixels: its layout size under the Viewbox's scale (the
    /// page's own scale transforms are the pause's, which never enlarge it), at the screen's density.</summary>
    internal int DevicePixels()
    {
        var root = TopLevel.GetTopLevel(this);
        if (root == null || Bounds.Width <= 0) return 0;
        var scale = 1.0;
        for (var v = this.GetVisualParent(); v != null && v != root; v = v.GetVisualParent())
            if (v is Viewbox box && box.Child is Control child && child.Bounds.Width > 0)
                scale *= Math.Min(box.Bounds.Width, box.Bounds.Height) / child.Bounds.Width;
        return (int)Math.Ceiling(Math.Min(Bounds.Width, Bounds.Height) * scale * root.RenderScaling);
    }

    /// <summary>Paints what changed and hands it to the render thread, which owns it from then on
    /// (it disposes the pair it replaces once it no longer draws it).</summary>
    internal void Build()
    {
        if (_visual == null) return;
        if (!IsVisible)
        {
            // Another style: the bitmaps go (a record at 1000 px is 4 MB, twice).
            if (_paintedPixels != 0) _visual.SendHandlerMessage(DiscSpinHandler.Release);
            _paintedPixels = 0;
            _paintedSource = null;
            _sheen = null;
            return;
        }
        var pixels = Math.Min(DevicePixels(), 2048);
        if (pixels < 16) return;
        var kind = Kind == NowPlayingArtworkStyle.CD ? NowPlayingArtworkStyle.CD : NowPlayingArtworkStyle.Vinyl;
        if (pixels == _paintedPixels && kind == _paintedKind && ReferenceEquals(Source, _paintedSource)) return;

        SKImage disc;
        try
        {
            if (_sheen == null || _sheenKey != (kind, pixels))
            {
                _sheen = DiscPainter.PaintLight(kind, pixels);
                _sheenKey = (kind, pixels);
            }
            disc = DiscPainter.PaintDisc(kind, pixels, Source);
        }
        catch (Exception ex)
        {
            // No disc beats a crash: the page keeps its wash and controls.
            Noctis.Services.DebugLog.Write("NowPlaying", $"Disc paint failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        Paints++;
        _paintedPixels = pixels;
        _paintedKind = kind;
        _paintedSource = Source;
        _visual.SendHandlerMessage(new DiscSpinHandler.Frame(disc, _sheen, kind == NowPlayingArtworkStyle.CD));
    }
}

/// <summary>
/// The spin: an angle and a ramp (0 at rest, 1 at full speed) that runs up while playing and down
/// while paused, the speed following it on a smoothstep, so a press never jolts the record: it
/// eases off the mark, lands softly at speed, and coasts to a stop. A press mid-ramp turns the ramp
/// around where it is (the speed is continuous). A value type: stepping it allocates nothing.
/// </summary>
internal struct DiscSpin
{
    /// <summary>33⅓ turns a minute, in radians a second.</summary>
    public const double VinylSpeed = 33.333333 / 60 * 2 * Math.PI;
    /// <summary>The CD's slow turn: 12 a minute (a real one would be a blur).</summary>
    public const double CdSpeed = 12.0 / 60 * 2 * Math.PI;
    /// <summary>Seconds from rest to speed, and from speed to rest (a turntable brakes slower than it starts).</summary>
    public const double SpinUpSeconds = 0.9, SpinDownSeconds = 1.4;

    public double Angle;
    public double Ramp;
    public bool Playing;
    public double MaxSpeed;

    public readonly double Velocity => MaxSpeed * Smooth(Ramp);

    /// <summary>Turning, or still easing up or down: frames needed.</summary>
    public readonly bool Moving => Playing ? true : Ramp > 0;

    public void Step(double seconds)
    {
        if (seconds <= 0) return;
        var before = Velocity;
        Ramp = Playing
            ? Math.Min(1, Ramp + seconds / SpinUpSeconds)
            : Math.Max(0, Ramp - seconds / SpinDownSeconds);
        // The step's mean speed (trapezoid): exact for the ramp's straight stretches, a hair
        // off where the smoothstep bends, and never a jump.
        Angle += (before + Velocity) * 0.5 * seconds;
        const double Turn = 2 * Math.PI;
        if (Angle >= Turn) Angle -= Turn * Math.Floor(Angle / Turn);
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
}

/// <summary>The render thread's half: steps the spin each frame and draws the disc turned and its
/// light over it. Every message is a preallocated object or a new pair of images. Drawn straight
/// on Skia's canvas with a paint of its own: Avalonia's DrawBitmap took ~345 managed bytes a call
/// (measured in MobileDiscArtworkTests), this takes the lease's two small objects.</summary>
internal sealed class DiscSpinHandler : CompositionCustomVisualHandler
{
    internal static readonly object Play = new(), Pause = new(), Wake = new(), Sleep = new(), Release = new();

    /// <summary>A new disc and its light. CD: the light shifts a little as the disc turns.</summary>
    internal sealed record Frame(SKImage Disc, SKImage Light, bool IsCd);

    // Linear filtering: the images are painted at the drawn size, and the paused scale (0.92)
    // only ever shrinks them a little.
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private readonly SKPaint _paint = new() { IsAntialias = true };

    private DiscSpin _spin;
    private Frame? _frame;
    private bool _awake;
    private bool _ticking;
    private TimeSpan _last;

    /// <summary>The spin as of the last frame (tests).</summary>
    internal DiscSpin Spin => _spin;

    /// <summary>Asked for the next frame (tests: false once at rest or asleep).</summary>
    internal bool Ticking => _ticking;

    /// <summary>Frames stepped so far (tests).</summary>
    internal int Frames { get; private set; }

    /// <summary>Tests: count the managed bytes the per-frame work allocates (<see cref="AllocatedBytes"/>).</summary>
    internal static bool CountAllocations;

    /// <summary>Managed bytes allocated inside the frame update and the draw, while counted.</summary>
    internal long AllocatedBytes { get; private set; }

    public override void OnMessage(object message)
    {
        if (ReferenceEquals(message, Play)) _spin.Playing = true;
        else if (ReferenceEquals(message, Pause)) _spin.Playing = false;
        else if (ReferenceEquals(message, Wake)) _awake = true;
        else if (ReferenceEquals(message, Sleep)) _awake = false;
        else if (ReferenceEquals(message, Release)) Replace(null);
        else if (message is Frame frame) Replace(frame);
        Kick();
    }

    private void Replace(Frame? frame)
    {
        var old = _frame;
        _frame = frame;
        _spin.MaxSpeed = frame is { IsCd: true } ? DiscSpin.CdSpeed : DiscSpin.VinylSpeed;
        if (old != null)
        {
            old.Disc.Dispose();
            if (!ReferenceEquals(old.Light, frame?.Light)) old.Light.Dispose();
        }
        Invalidate();
    }

    private void Kick()
    {
        if (_ticking || !_awake || !_spin.Moving || _frame == null) return;
        _ticking = true;
        _last = CompositionNow;
        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnAnimationFrameUpdate()
    {
        if (!CountAllocations)
        {
            Advance();
            return;
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        Advance();
        AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private void Advance()
    {
        _ticking = false;
        if (!_awake || _frame == null) return;   // asleep: the angle and ramp wait as they are
        var now = CompositionNow;
        // A long gap (a dropped run of frames) is taken as one frame's worth, not a lurch.
        var seconds = Math.Min((now - _last).TotalSeconds, 0.05);
        _last = now;
        _spin.Step(seconds);
        Frames++;
        Invalidate();
        if (!_spin.Moving) return;   // at rest: no more frames until Play
        _ticking = true;
        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (!CountAllocations)
        {
            Draw(context);
            return;
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        Draw(context);
        AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private void Draw(ImmediateDrawingContext context)
    {
        if (_frame is not { } frame) return;
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;   // Skia only
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        // The page's own fades (opacity is applied per draw here, not as a layer).
        _paint.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)Math.Round(255 * Math.Clamp(lease.CurrentOpacity, 0, 1)));
        var size = EffectiveSize;
        var rect = new SKRect(0, 0, (float)size.X, (float)size.Y);
        var cx = rect.MidX;
        var cy = rect.MidY;
        canvas.Save();
        canvas.RotateRadians((float)_spin.Angle, cx, cy);
        canvas.DrawImage(frame.Disc, rect, Sampling, _paint);
        canvas.Restore();
        // The lamp stays where it is; on the CD the rainbow swings a few degrees to and fro as
        // the disc turns, as a real one's shimmers with the angle.
        canvas.Save();
        if (frame.IsCd) canvas.RotateRadians((float)(0.11 * Math.Sin(_spin.Angle)), cx, cy);
        canvas.DrawImage(frame.Light, rect, Sampling, _paint);
        canvas.Restore();
    }
}
