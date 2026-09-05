using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// Live audio visualizer: draws the spectrum from <see cref="SpectrumMeter"/> in one of the
/// <see cref="VisualizerStyle"/> looks. Frame-clock driven via <c>TopLevel.RequestAnimationFrame</c>
/// like the flowing-artwork animator (a DispatcherTimer judders against the compositor);
/// each frame reads the meter, smooths every band (fast attack, slower release) and
/// invalidates the visual. Only draws — never touches layout. The loop parks itself while
/// the control is detached, hidden or <see cref="IsActive"/> is off, and decays to rest
/// when no live audio is flowing (paused, or an engine without a sample tap).
/// </summary>
public sealed class SpectrumVisualizer : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, bool>(nameof(IsActive));

    /// <summary>Stored style name ("Bars", "Mirror", "Wave"); parsed leniently.</summary>
    public static readonly StyledProperty<string?> StyleNameProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, string?>(nameof(StyleName), VisualizerStyles.DefaultSetting);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, IBrush?>(nameof(Fill), Brushes.White);

    /// <summary>Number of frequency bands drawn.</summary>
    public static readonly StyledProperty<int> BandCountProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, int>(nameof(BandCount), 48);

    /// <summary>The current artwork's vibrant colour; null when there is none.</summary>
    public static readonly StyledProperty<Color?> ArtworkColorProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, Color?>(nameof(ArtworkColor));

    /// <summary>Paint with <see cref="ArtworkColor"/> (via <see cref="VisualizerPalette"/>)
    /// instead of <see cref="Fill"/>. Falls back to Fill for grey covers or no artwork.</summary>
    public static readonly StyledProperty<bool> UseArtworkColorProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, bool>(nameof(UseArtworkColor));

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public string? StyleName { get => GetValue(StyleNameProperty); set => SetValue(StyleNameProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public int BandCount { get => GetValue(BandCountProperty); set => SetValue(BandCountProperty, value); }
    public Color? ArtworkColor { get => GetValue(ArtworkColorProperty); set => SetValue(ArtworkColorProperty, value); }
    public bool UseArtworkColor { get => GetValue(UseArtworkColorProperty); set => SetValue(UseArtworkColorProperty, value); }

    // Built once per artwork/style/toggle change, never per frame.
    private LinearGradientBrush? _artworkBrush;

    /// <summary>The brush the next frame paints with (diagnostics/tests).</summary>
    public IBrush? EffectiveFill => _artworkBrush ?? Fill;

    /// <summary>Smoothing time constants (ms): bands jump up quickly and fall away gently.</summary>
    public const double AttackMs = 35;
    public const double ReleaseMs = 220;

    private readonly SpectrumMeter _meter;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float[] _target = Array.Empty<float>();
    private float[] _shown = Array.Empty<float>();
    private double _lastFrameMs;
    private bool _running;
    private DispatcherTimer? _visibilityPoll;

    static SpectrumVisualizer()
    {
        AffectsRender<SpectrumVisualizer>(StyleNameProperty, FillProperty, ArtworkColorProperty, UseArtworkColorProperty);
        StyleNameProperty.Changed.AddClassHandler<SpectrumVisualizer>((c, _) => c.RebuildArtworkBrush());
        ArtworkColorProperty.Changed.AddClassHandler<SpectrumVisualizer>((c, _) => c.RebuildArtworkBrush());
        UseArtworkColorProperty.Changed.AddClassHandler<SpectrumVisualizer>((c, _) => c.RebuildArtworkBrush());
        IsActiveProperty.Changed.AddClassHandler<SpectrumVisualizer>((c, _) => c.OnGateChanged());
        IsVisibleProperty.Changed.AddClassHandler<SpectrumVisualizer>((c, _) => c.OnGateChanged());
    }

    public SpectrumVisualizer() : this(null) { }

    /// <param name="meter">Spectrum source; null uses <see cref="SpectrumMeter.Shared"/>.</param>
    public SpectrumVisualizer(SpectrumMeter? meter)
    {
        _meter = meter ?? SpectrumMeter.Shared;
    }

    /// <summary>The smoothed band levels currently on screen (diagnostics/tests).</summary>
    public ReadOnlySpan<float> ShownBands => _shown;

    /// <summary>True while a frame callback is pending.</summary>
    public bool IsRunning => _running;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnGateChanged();
    }

    private void RebuildArtworkBrush()
    {
        _artworkBrush = UseArtworkColor
            ? VisualizerPalette.Build(ArtworkColor, VisualizerStyles.Parse(StyleName))
            : null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Rest();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnGateChanged()
    {
        if (IsActive && IsVisible) TryStart();
        else Rest();
    }

    private void TryStart()
    {
        if (_running || !IsActive) return;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            StartVisibilityPoll();
            return;
        }
        StopVisibilityPoll();
        _running = true;
        _lastFrameMs = _clock.Elapsed.TotalMilliseconds;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running) return;
        if (!IsActive || !IsVisible)
        {
            _running = false;
            return;
        }
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            _running = false;
            StartVisibilityPoll();
            return;
        }

        var nowMs = _clock.Elapsed.TotalMilliseconds;
        var dtMs = Math.Clamp(nowMs - _lastFrameMs, 0, 100);
        _lastFrameMs = nowMs;

        Step(dtMs);
        InvalidateVisual();
        topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>One animation step: read the meter and smooth toward it. Public for tests.</summary>
    public void Step(double dtMs)
    {
        var count = Math.Max(1, BandCount);
        if (_target.Length != count)
        {
            _target = new float[count];
            _shown = new float[count];
        }

        // Not live → targets are already zero; the release smoothing decays the bars to rest.
        _meter.TryRead(_meter.NowMs, _target);
        Smooth(_shown, _target, dtMs);
    }

    /// <summary>Per-band asymmetric exponential smoothing (attack up, release down).</summary>
    public static void Smooth(float[] shown, float[] target, double dtMs,
        double attackMs = AttackMs, double releaseMs = ReleaseMs)
    {
        var up = (float)(1 - Math.Exp(-dtMs / attackMs));
        var down = (float)(1 - Math.Exp(-dtMs / releaseMs));
        for (var i = 0; i < shown.Length; i++)
        {
            var t = target[i];
            var s = shown[i];
            shown[i] = t > s ? s + (t - s) * up : s + (t - s) * down;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var w = bounds.Width;
        var h = bounds.Height;
        if (w <= 0 || h <= 0 || _shown.Length == 0 || EffectiveFill is not { } brush) return;

        switch (VisualizerStyles.Parse(StyleName))
        {
            case VisualizerStyle.Mirror: DrawMirror(context, brush, w, h); break;
            case VisualizerStyle.Wave: DrawWave(context, brush, w, h); break;
            default: DrawBars(context, brush, w, h); break;
        }
    }

    private void DrawBars(DrawingContext ctx, IBrush brush, double w, double h)
    {
        var n = _shown.Length;
        var slot = w / n;
        var gap = Math.Min(4, slot * 0.28);
        var barW = Math.Max(1, slot - gap);
        var radius = Math.Min(barW / 2, 3);
        var minH = radius * 2;
        for (var i = 0; i < n; i++)
        {
            var level = _shown[i];
            var barH = Math.Max(minH, level * h);
            var x = i * slot + gap / 2;
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(x, h - barH, barW, barH), radius));
        }
    }

    private void DrawMirror(DrawingContext ctx, IBrush brush, double w, double h)
    {
        var n = _shown.Length;
        var slot = w / n;
        var gap = Math.Min(4, slot * 0.28);
        var barW = Math.Max(1, slot - gap);
        var radius = Math.Min(barW / 2, 3);
        var mid = h / 2;
        var minH = radius * 2;
        for (var i = 0; i < n; i++)
        {
            var level = _shown[i];
            var half = Math.Max(minH / 2, level * mid);
            var x = i * slot + gap / 2;
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(x, mid - half, barW, half * 2), radius));
        }
    }

    private void DrawWave(DrawingContext ctx, IBrush brush, double w, double h)
    {
        var n = _shown.Length;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, h), isFilled: true);
            // Smooth curve through the band tops (Catmull-Rom → cubic Bézier).
            Point P(int i)
            {
                i = Math.Clamp(i, 0, n - 1);
                var x = n == 1 ? w / 2 : w * i / (n - 1);
                return new Point(x, h - _shown[i] * h);
            }
            g.LineTo(P(0));
            for (var i = 0; i < n - 1; i++)
            {
                var p0 = P(i - 1); var p1 = P(i); var p2 = P(i + 1); var p3 = P(i + 2);
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                g.CubicBezierTo(c1, c2, p2);
            }
            g.LineTo(new Point(w, h));
            g.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(brush, null, geometry);
    }

    private void Rest()
    {
        _running = false;
        StopVisibilityPoll();
        Array.Clear(_shown);
        InvalidateVisual();
    }

    private void StartVisibilityPoll()
    {
        if (_visibilityPoll != null) return;
        _visibilityPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _visibilityPoll.Tick += (_, _) =>
        {
            if (!IsActive || !IsVisible) { StopVisibilityPoll(); return; }
            if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) != null)
                TryStart();
        };
        _visibilityPoll.Start();
    }

    private void StopVisibilityPoll()
    {
        if (_visibilityPoll == null) return;
        _visibilityPoll.Stop();
        _visibilityPoll = null;
    }
}
