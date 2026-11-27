using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// The pill progress bar of Noctis's own downloads (Noctis.Controls.SmoothProgressBar: the updater
/// pill, Lyrics Studio's model and song progress), here because a plugin cannot reference the app.
/// Fully rounded track and fill; the fill glides to each new <see cref="Value"/> on the frame clock
/// (<c>TopLevel.RequestAnimationFrame</c>) with an exponential ease instead of jumping 2% → 5% →
/// 9%, without a Transition restarted by every report (that stalls in Avalonia) and without
/// touching layout: it only draws. A lower Value is a new run and lands at once; the fill never
/// slides backwards. <see cref="IsIndeterminate"/> sweeps a short segment while there is no
/// number yet ("Reading details"). <see cref="ShownPercentChanged"/> reports the drawn fill as a
/// whole percent, so the percent beside the bar counts up with the fill rather than ahead of it.
/// </summary>
internal sealed class PillProgressBar : Control
{
    /// <summary>Progress 0..1.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PillProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<PillProgressBar, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<PillProgressBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<PillProgressBar, IBrush?>(nameof(FillBrush), Brushes.White);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    /// <summary>The drawn fill as a whole percent, raised only when that number changes.</summary>
    public event Action<int>? ShownPercentChanged;

    /// <summary>Time constant of the chase: ~63% of the gap closes in this long (SmoothProgressBar's).</summary>
    internal const double ChaseMs = 160;
    /// <summary>One pass of the indeterminate segment.</summary>
    internal const double SweepMs = 1300;
    private const double SegmentFraction = 0.28;

    static PillProgressBar()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<PillProgressBar>(false);
        AffectsRender<PillProgressBar>(TrackBrushProperty, FillBrushProperty, IsIndeterminateProperty);
    }

    private double _shown;      // drawn fill, 0..1
    private double _sweep;      // indeterminate phase, 0..1
    private bool _running;
    private TimeSpan? _lastFrame;
    private int _shownPercent;

    /// <summary>The fill as drawn now (tests).</summary>
    internal double Shown => _shown;

    /// <summary>The drawn fill as a whole percent.</summary>
    public int ShownPercent => _shownPercent;

    private double Target => double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) : 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            if (Target < _shown)
            {
                // A new download: show it at once, never a slide back.
                SetShown(Target);
                InvalidateVisual();
                return;
            }
            EnsureRunning();
        }
        else if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty)
        {
            EnsureRunning();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false;
    }

    private bool NeedsFrames => IsIndeterminate || Math.Abs(Target - _shown) > 0.0005;

    private void EnsureRunning()
    {
        if (_running || !NeedsFrames) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || !IsEffectivelyVisible)
        {
            // Nothing on screen to animate: land on the value.
            SetShown(Target);
            InvalidateVisual();
            return;
        }
        _running = true;
        _lastFrame = null;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_running) return;
        var dt = _lastFrame is { } last ? (now - last).TotalMilliseconds : 16;
        _lastFrame = now;
        var more = Step(dt);
        InvalidateVisual();
        var topLevel = TopLevel.GetTopLevel(this);
        if (!more || topLevel == null || !IsEffectivelyVisible)
        {
            _running = false;
            if (topLevel == null || !IsEffectivelyVisible) SetShown(Target);
            return;
        }
        topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>One animation step of <paramref name="dtMs"/>; true while there is more to animate. Internal for tests.</summary>
    internal bool Step(double dtMs)
    {
        dtMs = Math.Clamp(dtMs, 0, 100); // a stalled frame must not teleport the fill
        if (IsIndeterminate)
        {
            _sweep = (_sweep + dtMs / SweepMs) % 1;
            return true;
        }
        var target = Target;
        if (target <= _shown)
        {
            SetShown(target);
            return false;
        }
        var next = _shown + (target - _shown) * (1 - Math.Exp(-dtMs / ChaseMs));
        if (target - next < 0.0005)
        {
            SetShown(target);
            return false;
        }
        SetShown(next);
        return true;
    }

    private void SetShown(double value)
    {
        _shown = value;
        var percent = (int)Math.Round(value * 100);
        if (percent == _shownPercent) return;
        _shownPercent = percent;
        ShownPercentChanged?.Invoke(percent);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var radius = height / 2;
        var track = new RoundedRect(new Rect(0, 0, width, height), radius);
        if (TrackBrush is { } trackBrush) context.DrawRectangle(trackBrush, null, track);
        if (FillBrush is not { } fill) return;

        if (IsIndeterminate)
        {
            var segment = width * SegmentFraction;
            var t = _sweep * _sweep * (3 - 2 * _sweep); // smoothstep: eases in and out at the ends
            var x = -segment + (width + segment) * t;
            using (context.PushClip(track))
                context.DrawRectangle(fill, null, new RoundedRect(new Rect(x, 0, segment, height), radius));
            return;
        }

        if (_shown <= 0) return;
        // At least a round dot, so the first bytes read as progress rather than nothing.
        var filled = Math.Min(width, Math.Max(height, _shown * width));
        context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, filled, height), radius));
    }
}
