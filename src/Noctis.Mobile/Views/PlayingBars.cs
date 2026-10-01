using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

/// <summary>
/// The playing row's equalizer, the desktop's row indicator (src/Noctis/Controls/EqVisualizer,
/// which the phone cannot reference) redrawn here with its numbers: five bars
/// <see cref="BarWidth"/> wide and <see cref="BarSpacing"/> apart in a <see cref="RowHeight"/>
/// row, rounded by half their width, each a sine between <see cref="BarMin"/> and
/// <see cref="BarMax"/> at its own <see cref="Frequencies"/> and <see cref="Phases"/>, sampled
/// every <see cref="FrameInterval"/>; on pause they ease flat (<see cref="FlatHeight"/>) over
/// <see cref="FlattenDuration"/>, cubic ease-out. The desktop's beat and spectrum layer needs its
/// audio sample tap, which the phone has not got, so this is the desktop's no-tap motion.
/// One control draws all five bars: a tick repaints, never re-runs layout.
/// </summary>
public sealed class PlayingBars : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayingBars, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<PlayingBars, IBrush?>(nameof(Fill));

    /// <summary>The desktop EqVisualizer's template and constants (tests hold the two together).</summary>
    public const int BarCount = 5;
    public const double BarWidth = 1.75, BarSpacing = 1.75, RowHeight = 12;
    public const double FlatHeight = 1.75, BarMin = 2.25, BarMax = 10.0;
    /// <summary>The desktop EqBar's corner radius: half a bar's width.</summary>
    public const double Radius = 0.875;
    public static readonly IReadOnlyList<double> Phases = new[] { 0.0, 1.2, 2.4, 0.8, 1.8 };
    public static readonly IReadOnlyList<double> Frequencies = new[] { 1.6, 2.0, 1.4, 1.8, 1.7 };
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan HiddenPollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan FlattenDuration = TimeSpan.FromMilliseconds(420);
    private static readonly Easing FlattenEasing = new CubicEaseOut();

    private readonly double[] _heights = { FlatHeight, FlatHeight, FlatHeight, FlatHeight, FlatHeight };
    private readonly double[] _flattenFrom = new double[BarCount];
    private DispatcherTimer? _timer;
    private DateTime _start, _flattenStart;
    private bool _flattening;

    static PlayingBars()
    {
        AffectsRender<PlayingBars>(FillProperty);
        IsPlayingProperty.Changed.AddClassHandler<PlayingBars>((c, _) => c.OnPlayingChanged());
        // Rows flip IsVisible to show the bars on the player's track only; a hidden one parks.
        IsVisibleProperty.Changed.AddClassHandler<PlayingBars>((c, _) => c.OnVisibleChanged());
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The current bar heights, left to right (tests read them).</summary>
    internal IReadOnlyList<double> BarHeights => _heights;

    /// <summary>A bar's height <paramref name="t"/> seconds into the motion: the desktop's
    /// sine mapped to <see cref="BarMin"/> … <see cref="BarMax"/>.</summary>
    public static double OscillationHeight(double t, int bar)
    {
        var s = Math.Sin(2 * Math.PI * Frequencies[bar] * t + Phases[bar]);
        return BarMin + (BarMax - BarMin) * (s * 0.5 + 0.5);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(BarCount * BarWidth + (BarCount - 1) * BarSpacing, RowHeight);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Recycled rows re-attach without an IsPlaying change; restart, or the bars come back frozen.
        if (IsPlaying && IsVisible) Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _flattening = false;
        _timer?.Stop();
    }

    private void OnPlayingChanged()
    {
        if (IsPlaying)
        {
            _flattening = false;
            if (IsVisible) Start();
        }
        else if (IsVisible && VisualRoot != null)
        {
            Array.Copy(_heights, _flattenFrom, BarCount);
            _flattenStart = DateTime.UtcNow;
            _flattening = true;
            RunTimer(FrameInterval);
        }
        else Park();
    }

    private void OnVisibleChanged()
    {
        if (IsVisible && IsPlaying)
        {
            _flattening = false;
            Start();
        }
        else if (!IsVisible) Park();
    }

    private void Start()
    {
        if (VisualRoot == null) return;
        _start = DateTime.UtcNow;
        RunTimer(FrameInterval);
    }

    /// <summary>Flat and still: nothing to ease out where it cannot be seen.</summary>
    private void Park()
    {
        _flattening = false;
        _timer?.Stop();
        SetAll(FlatHeight);
    }

    private void RunTimer(TimeSpan interval)
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };
            _timer.Tick += OnTick;
        }
        _timer.Interval = interval;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (VisualRoot == null) { _flattening = false; _timer?.Stop(); return; }
        if (!IsEffectivelyVisible)
        {
            // An ancestor hides the row (the page behind an overlay): land flat when there is
            // nothing to show, else poll slowly until it shows again.
            if (_flattening || !IsPlaying) { Park(); return; }
            if (_timer!.Interval != HiddenPollInterval) _timer.Interval = HiddenPollInterval;
            return;
        }
        if (_timer!.Interval != FrameInterval) _timer.Interval = FrameInterval;

        if (_flattening)
        {
            var progress = (DateTime.UtcNow - _flattenStart).TotalMilliseconds / FlattenDuration.TotalMilliseconds;
            if (progress >= 1) { Park(); return; }
            var eased = FlattenEasing.Ease(progress);
            for (var i = 0; i < BarCount; i++) _heights[i] = _flattenFrom[i] + (FlatHeight - _flattenFrom[i]) * eased;
            InvalidateVisual();
            return;
        }

        var t = (DateTime.UtcNow - _start).TotalSeconds;
        for (var i = 0; i < BarCount; i++) _heights[i] = OscillationHeight(t, i);
        InvalidateVisual();
    }

    private void SetAll(double height)
    {
        for (var i = 0; i < BarCount; i++) _heights[i] = height;
        InvalidateVisual();
    }

    /// <summary>
    /// Bar <paramref name="bar"/>'s rect at <paramref name="height"/>, as the desktop's laid-out
    /// bars land: widths and offsets on the pixel grid, the height rounded up to it and centred
    /// in the row (EqBar.BarRectIn). A <paramref name="scale"/> of 0 skips the rounding.
    /// </summary>
    internal static Rect BarRect(int bar, double height, double scale)
    {
        double Round(double v) => scale > 0 ? LayoutHelper.RoundLayoutValue(v, scale) : v;
        var width = Round(BarWidth);
        var x = Round(bar * (width + BarSpacing));
        var h = scale > 0 ? LayoutHelper.RoundLayoutValueUp(height, scale) : height;
        var row = scale > 0 ? LayoutHelper.RoundLayoutValueUp(RowHeight, scale) : RowHeight;
        h = Math.Min(h, row);
        return new Rect(x, Round((row - h) / 2), width, h);
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is not { } fill) return;
        var scale = UseLayoutRounding ? LayoutHelper.GetLayoutScale(this) : 0;
        for (var i = 0; i < BarCount; i++)
        {
            var rect = BarRect(i, _heights[i], scale);
            if (rect.Height <= 0) continue;
            context.DrawRectangle(fill, null, rect, Radius, Radius);
        }
    }
}
