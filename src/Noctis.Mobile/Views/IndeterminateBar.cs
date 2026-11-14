using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

/// <summary>
/// The phone's "working, no count yet" bar: a <see cref="BarHeight"/> track with a segment
/// <see cref="SegmentFraction"/> of its width sweeping across every <see cref="Period"/>.
/// Replaces Fluent's indeterminate ProgressBar on the phone: on a Galaxy S23 (11-14) that
/// template's 2 s keyframe loop cost three frames of 50-83 ms at every turn, with nothing
/// allocated - the Home/Library scan bar alone made startup stutter every 2 s for the whole
/// scan, and the Account page's sync bar the same while it waited.
/// One control draws track and segment, stepped on the frame clock like <see cref="PlayingBars"/>:
/// a frame repaints, never re-runs layout. Inactive, detached or hidden, no frame is asked for;
/// while an ancestor hides it a <see cref="HiddenPollInterval"/> one-shot checks back.
/// </summary>
public sealed class IndeterminateBar : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<IndeterminateBar, bool>(nameof(IsActive));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<IndeterminateBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<IndeterminateBar, IBrush?>(nameof(Fill));

    public const double BarHeight = 4;
    public const double SegmentFraction = 0.3;
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan HiddenPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Easing SweepEasing = new SineEaseInOut();

    private DateTime _start;
    private double _phase;
    private bool _running, _frameQueued;
    /// <summary>Bumped by every start and stop, so a queued frame or poll from an older run drops out.</summary>
    private int _generation;

    static IndeterminateBar()
    {
        AffectsRender<IndeterminateBar>(TrackProperty, FillProperty);
        IsActiveProperty.Changed.AddClassHandler<IndeterminateBar>((c, _) => c.Restart());
        IsVisibleProperty.Changed.AddClassHandler<IndeterminateBar>((c, _) => c.Restart());
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Frames are being asked for (tests check it idles).</summary>
    internal bool IsRunning => _running;

    /// <summary>The segment's left edge and width in a <paramref name="width"/> wide track at
    /// <paramref name="phase"/> (0..1 of a <see cref="Period"/>): it enters from the left and
    /// leaves at the right, clipped to the track.</summary>
    internal static (double X, double Width) Segment(double width, double phase)
    {
        var segment = width * SegmentFraction;
        var eased = SweepEasing.Ease(Math.Clamp(phase, 0, 1));
        var left = -segment + (width + segment) * eased;
        var x = Math.Max(0, left);
        var right = Math.Min(width, left + segment);
        return (x, Math.Max(0, right - x));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, BarHeight);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    private void Restart()
    {
        if (IsActive && IsVisible && VisualRoot != null)
        {
            if (_running) return;
            _start = DateTime.UtcNow;
            var generation = ++_generation;
            _running = true;
            QueueFrame(generation);
        }
        else Stop();
    }

    private void Stop()
    {
        _running = false;
        _generation++;
    }

    private void QueueFrame(int generation)
    {
        if (_frameQueued || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameQueued = false;
            if (_running) OnFrame(_generation);
        });
    }

    private void OnFrame(int generation)
    {
        if (VisualRoot == null || !IsActive) { Stop(); return; }
        if (!IsEffectivelyVisible)
        {
            DispatcherTimer.RunOnce(() => { if (generation == _generation && _running) QueueFrame(generation); }, HiddenPollInterval);
            return;
        }
        var elapsed = (DateTime.UtcNow - _start).TotalMilliseconds;
        _phase = elapsed % Period.TotalMilliseconds / Period.TotalMilliseconds;
        InvalidateVisual();
        QueueFrame(generation);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Math.Min(Bounds.Height, BarHeight);
        if (width <= 0 || height <= 0) return;
        var y = (Bounds.Height - height) / 2;
        var radius = height / 2;
        if (Track is { } track) context.DrawRectangle(track, null, new Rect(0, y, width, height), radius, radius);
        if (!IsActive || Fill is not { } fill) return;
        var (x, w) = Segment(width, _phase);
        if (w > 0) context.DrawRectangle(fill, null, new Rect(x, y, w, height), radius, radius);
    }
}
