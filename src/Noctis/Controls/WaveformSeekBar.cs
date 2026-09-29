using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Services.Waveform;

namespace Noctis.Controls;

/// <summary>
/// Waveform seek bar visual (GitHub #93): the track's waveform as thin mirrored bars, the
/// played part in <see cref="PlayedBrush"/> and the rest in <see cref="UnplayedBrush"/>.
/// Purely a visual — it is not hit-testable and sits under the existing (invisible or
/// line-less) seek Slider, so clicking and dragging still go through the Slider's
/// BeginSeek/EndSeek path unchanged.
///
/// Until <see cref="Waveform"/> arrives it draws the plain seek line (same thickness,
/// rounding and brushes as the bar it replaces). When the data lands, the line fades out
/// while the bars grow out of it (<see cref="RevealDuration"/>, frame-clock driven) — the
/// control's size never changes, so there is no layout jump.
///
/// Time mapping matches PillSliderVisualHelper / the Fluent Slider: the thumb centre
/// travels <see cref="TrackInset"/>..width−TrackInset, so the played/unplayed split sits
/// exactly under the thumb and the bars are laid out over that same span.
/// </summary>
public sealed class WaveformSeekBar : Control
{
    public static readonly StyledProperty<WaveformData?> WaveformProperty =
        AvaloniaProperty.Register<WaveformSeekBar, WaveformData?>(nameof(Waveform));

    /// <summary>Playback position 0..1 (bind the seek Slider's Value so a drag previews).</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(PlayedBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> UnplayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(UnplayedBrush), new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));

    /// <summary>Half the seek thumb: where 0% and 100% sit from the edges.</summary>
    public static readonly StyledProperty<double> TrackInsetProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(TrackInset), 6);

    /// <summary>Inset of the plain line's ends (the island's line runs inset by half a
    /// thumb; the mini player's Fluent line runs edge to edge).</summary>
    public static readonly StyledProperty<double> LineInsetProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(LineInset));

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(LineThickness), 3);

    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(BarWidth), 2);

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(BarGap), 1);

    /// <summary>Height of a bar over silence (a dotted line through quiet passages).</summary>
    public static readonly StyledProperty<double> MinBarHeightProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(MinBarHeight), 1.5);

    /// <summary>Tallest bar; NaN = the control's height.</summary>
    public static readonly StyledProperty<double> MaxBarHeightProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(MaxBarHeight), double.NaN);

    public WaveformData? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? PlayedBrush { get => GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public IBrush? UnplayedBrush { get => GetValue(UnplayedBrushProperty); set => SetValue(UnplayedBrushProperty, value); }
    public double TrackInset { get => GetValue(TrackInsetProperty); set => SetValue(TrackInsetProperty, value); }
    public double LineInset { get => GetValue(LineInsetProperty); set => SetValue(LineInsetProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
    public double MinBarHeight { get => GetValue(MinBarHeightProperty); set => SetValue(MinBarHeightProperty, value); }
    public double MaxBarHeight { get => GetValue(MaxBarHeightProperty); set => SetValue(MaxBarHeightProperty, value); }

    /// <summary>Line → waveform cross-fade length.</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(420);

    static WaveformSeekBar()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<WaveformSeekBar>(false);
        AffectsRender<WaveformSeekBar>(ValueProperty, PlayedBrushProperty, UnplayedBrushProperty,
            TrackInsetProperty, LineInsetProperty, LineThicknessProperty, BarWidthProperty, BarGapProperty,
            MinBarHeightProperty, MaxBarHeightProperty);
        WaveformProperty.Changed.AddClassHandler<WaveformSeekBar>((c, e) => c.OnWaveformChanged(e.NewValue as WaveformData));
    }

    private double _reveal = 1;          // 0 = plain line, 1 = full waveform
    private TimeSpan? _revealStart;
    private bool _animating;
    private float[] _levels = Array.Empty<float>();
    private WaveformData? _levelsFor;

    /// <summary>Line → waveform progress, 0..1 (tests).</summary>
    internal double RevealProgress => Waveform == null ? 0 : _reveal;

    /// <summary>Number of bars the current width lays out (tests).</summary>
    internal int BarCount => CountBars(Bounds.Width);

    private void OnWaveformChanged(WaveformData? data)
    {
        _levelsFor = null;
        if (data == null)
        {
            // Not ready (or a new track): back to the plain line at once — never the old
            // track's shape under the new title.
            _reveal = 0;
            _animating = false;
            InvalidateVisual();
            return;
        }
        _reveal = 0;
        _revealStart = null;
        StartReveal();
    }

    private void StartReveal()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || !IsEffectivelyVisible)
        {
            // Nothing on screen to animate: land on the finished state.
            _reveal = 1;
            _animating = false;
            InvalidateVisual();
            return;
        }
        if (_animating) return;
        _animating = true;
        topLevel.RequestAnimationFrame(OnRevealFrame);
    }

    private void OnRevealFrame(TimeSpan now)
    {
        if (!_animating) return;
        _revealStart ??= now;
        var t = (now - _revealStart.Value).TotalMilliseconds / RevealDuration.TotalMilliseconds;
        _reveal = Math.Clamp(t, 0, 1);
        InvalidateVisual();
        if (_reveal >= 1)
        {
            _animating = false;
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            _reveal = 1;
            _animating = false;
            return;
        }
        topLevel.RequestAnimationFrame(OnRevealFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A pending frame callback must not keep running for a detached bar.
        if (_animating)
        {
            _animating = false;
            _reveal = 1;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private int CountBars(double width)
    {
        var (barW, gap) = SnappedBar();
        var span = Math.Max(0, width - 2 * TrackInset);
        return Math.Max(0, (int)Math.Floor((span + gap) / (barW + gap)));
    }

    private double RenderScale
    {
        get
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return scale > 0 ? scale : 1.0;
        }
    }

    /// <summary>
    /// Bar width and gap rounded to whole device pixels (at least one each), so every
    /// bar and every gap is the same crisp width at 125% / 150% scaling instead of
    /// alternating anti-aliased widths.
    /// </summary>
    private (double BarWidth, double Gap) SnappedBar()
    {
        var scale = RenderScale;
        var bar = Math.Max(1, Math.Round(BarWidth * scale)) / scale;
        var gap = Math.Max(1, Math.Round(BarGap * scale)) / scale;
        return (bar, gap);
    }

    /// <summary>Bar heights for the current layout (0 = silence, 1 = loudest), reused
    /// until the data or bar count changes (position ticks only move the split).</summary>
    private float[] LevelsFor(WaveformData data, int bars)
    {
        if (!ReferenceEquals(_levelsFor, data) || _levels.Length != bars)
        {
            _levels = new float[bars];
            data.ResampleLevels(_levels);
            _levelsFor = data;
        }
        return _levels;
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var played = PlayedBrush;
        var unplayed = UnplayedBrush;
        var inset = TrackInset;
        var span = Math.Max(0, w - 2 * inset);
        var value = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) : 0;
        var split = inset + value * span;
        var cy = h / 2;
        var data = Waveform;
        var reveal = data == null ? 0 : _reveal;

        if (reveal < 1)
        {
            // The plain seek line: remainder full length, played part on top of it.
            var th = LineThickness;
            var left = LineInset;
            var right = w - LineInset;
            var radius = th / 2;
            using (context.PushOpacity(1 - reveal))
            {
                if (right > left)
                    context.DrawRectangle(unplayed, null, new RoundedRect(new Rect(left, cy - th / 2, right - left, th), radius));
                if (split > left)
                    context.DrawRectangle(played, null, new RoundedRect(new Rect(left, cy - th / 2, split - left, th), radius));
            }
        }

        if (reveal <= 0 || data == null) return;

        var bars = CountBars(w);
        if (bars == 0) return;
        var levels = LevelsFor(data, bars);
        var (barW, gap) = SnappedBar();
        var pitch = barW + gap;
        var used = bars * pitch - gap;
        var scale = RenderScale;
        var x0 = Math.Round((inset + (span - used) / 2) * scale) / scale;
        var maxH = double.IsNaN(MaxBarHeight) ? h : Math.Min(h, MaxBarHeight);
        var minH = Math.Min(MinBarHeight, maxH);
        var grow = EaseOutCubic(reveal);
        var from = Math.Min(LineThickness, maxH);
        var radius2 = barW / 2;

        using (context.PushOpacity(reveal))
        {
            for (var i = 0; i < bars; i++)
            {
                var target = minH + levels[i] * (maxH - minH);
                var bh = from + (target - from) * grow;
                var x = x0 + i * pitch;
                var rect = new RoundedRect(new Rect(x, cy - bh / 2, barW, bh), Math.Min(radius2, bh / 2));
                if (x + barW <= split)
                {
                    context.DrawRectangle(played, null, rect);
                }
                else if (x >= split)
                {
                    context.DrawRectangle(unplayed, null, rect);
                }
                else
                {
                    // The bar under the playhead: split colour at the exact position.
                    using (context.PushClip(new Rect(x, 0, split - x, h)))
                        context.DrawRectangle(played, null, rect);
                    using (context.PushClip(new Rect(split, 0, x + barW - split, h)))
                        context.DrawRectangle(unplayed, null, rect);
                }
            }
        }
    }
}
