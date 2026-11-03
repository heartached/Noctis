using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>Which transport glyph a <see cref="TransportGlyph"/> draws.</summary>
public enum TransportKind
{
    PlayPause,
    Previous,
    Next,
}

/// <summary>
/// The glyph inside a Previous / Play-Pause / Next button, with Apple Music's press feel
/// (measured frame by frame from an iOS 26 recording, 60 fps):
/// <list type="bullet">
/// <item>Press: a translucent circle fades in behind the glyph (~80 ms) and the glyph settles
/// to 0.85 (~120 ms). Release: the glyph springs back past full size (≈1.045 at ~190 ms) and
/// settles, while the circle fades out (~170 ms, after a 30 ms beat). A quick tap still shows
/// the dip: the spring-back waits until <see cref="MinHold"/> after the press.</item>
/// <item>Previous / Next, when clicked (pointer or keyboard; not media keys, which never touch
/// the button): the double triangle runs one step of a conveyor — the leading triangle shrinks
/// away at its tip (half gone in three frames), the trailing one slides into its place and a
/// new one grows in behind it (220 ms). The glyph stays dipped until the step lands, then
/// springs. Rapid clicks queue up to two more steps; each step ends exactly on the rest
/// glyph.</item>
/// <item>Play / Pause, whenever <see cref="IsPlaying"/> changes (a click, a media key, the
/// end of the queue): the outgoing glyph shrinks to 0.2 and fades (~5 frames); the incoming
/// one appears 70 ms later at 0.25, grows with a small overshoot (≈1.06 at ~230 ms) and fades
/// in (~250 ms) — a morph through a point, not a swap.</item>
/// </list>
/// Drawn in <see cref="Render"/> under pushed matrices on the icons' 24-unit grid (the same
/// geometry as Icons.axaml's PlayIcon / PauseIcon / RewindIcon / FastForwardIcon), so a frame
/// only invalidates this control's visual: no layout, no transforms or brushes allocated, no
/// transitions (which stall under per-frame writes in this repo). Every value is a closed-form
/// <see cref="Spring"/> read at the frame's timestamp, so a late frame skips ahead instead of
/// slowing the motion, and an interrupted motion turns around from its current speed.
/// The circle is drawn outside the control's box (centred on it), so the hosting button's
/// size, hit area and layout are untouched. <see cref="ReduceMotionProperty"/> makes every
/// change instant (the circle still shows while pressed, without fading).
/// </summary>
public sealed class TransportGlyph : Control
{
    public static readonly StyledProperty<TransportKind> KindProperty =
        AvaloniaProperty.Register<TransportGlyph, TransportKind>(nameof(Kind));

    /// <summary>Play / Pause only: true draws Pause.</summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<TransportGlyph, bool>(nameof(IsPlaying));

    /// <summary>Glyph fill; inherits the surrounding text colour like PathIcon does.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<TransportGlyph>();

    /// <summary>The press circle's fill. Null: the glyph's own colour at
    /// <see cref="HighlightAlpha"/>.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<TransportGlyph, IBrush?>(nameof(HighlightBrush));

    /// <summary>The press circle's diameter in px. NaN (default): Apple's proportion — the same
    /// circle for both kinds, 1.24 × a skip glyph's box, 1.12 × a Play/Pause box.</summary>
    public static readonly StyledProperty<double> HighlightSizeProperty =
        AvaloniaProperty.Register<TransportGlyph, double>(nameof(HighlightSize), double.NaN);

    /// <summary>Inherited: true turns every animation into an instant change.</summary>
    public static readonly AttachedProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.RegisterAttached<TransportGlyph, Visual, bool>("ReduceMotion", inherits: true);

    public static bool GetReduceMotion(Visual v) => v.GetValue(ReduceMotionProperty);
    public static void SetReduceMotion(Visual v, bool value) => v.SetValue(ReduceMotionProperty, value);

    public TransportKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public double HighlightSize { get => GetValue(HighlightSizeProperty); set => SetValue(HighlightSizeProperty, value); }

    // ── Measured timings / amounts (see the class summary) ──────────────────────────────
    internal const double PressScale = 0.85;
    internal const double MorphHiddenScale = 0.2;
    internal const double MorphEnterScale = 0.25;
    internal const double MorphEnterDelay = 0.07;
    internal const double SlideSeconds = 0.22;
    internal const double MinHold = 0.12;
    internal const double HighlightAlpha = 0.12;
    private const double Frame = 1.0 / 60;

    private const double PressOmega = 38, PressZeta = 1;          // ~120 ms ease-out to 0.85
    private const double ReleaseOmega = 17.7, ReleaseZeta = 0.36; // peak ≈1.045 at ~190 ms
    private const double HighlightInOmega = 55, HighlightOutOmega = 26;
    internal const double HighlightHold = 0.03;   // the circle stays a beat after the release
    private const double HideScaleOmega = 48, HideOpacityOmega = 40;   // gone in ~5 frames
    private const double ShowScaleOmega = 17.1, ShowScaleZeta = 0.6;  // peak ≈1.06 at ~230 ms
    private const double ShowOpacityOmega = 18;

    /// <summary>The icons' grid and the skip triangle's step on it.</summary>
    private const double Grid = 24, Step = 9.5, TipX = 21.9, BaseX = 2.5, MidY = 12;

    // Icons.axaml's PlayIcon / PauseIcon / FastForwardIcon (RewindIcon is its mirror; the phone's
    // Ph* twins are the same paths); the triangle is FastForwardIcon's trailing half, the leading
    // one is it moved by Step. TransportGlyphMotionTests keeps them equal to the dictionaries.
    internal const string PlayData = "M5 5.274c0-1.707 1.826-2.792 3.325-1.977l12.362 6.727c1.566.852 1.566 3.1 0 3.952L8.325 20.702C6.826 21.518 5 20.432 5 18.726z";
    internal const string PauseData = "M5.746 3a1.75 1.75 0 0 0-1.75 1.75v14.5c0 .966.784 1.75 1.75 1.75h3.5a1.75 1.75 0 0 0 1.75-1.75V4.75A1.75 1.75 0 0 0 9.246 3zm9 0a1.75 1.75 0 0 0-1.75 1.75v14.5c0 .966.784 1.75 1.75 1.75h3.5a1.75 1.75 0 0 0 1.75-1.75V4.75A1.75 1.75 0 0 0 18.246 3z";
    internal const string ForwardData = "M2.5 6.7Q2.5 5.1 3.9 5.9L11.3 11.1Q12.4 12 11.3 12.9L3.9 18.1Q2.5 18.9 2.5 17.3ZM12 6.7Q12 5.1 13.4 5.9L20.8 11.1Q21.9 12 20.8 12.9L13.4 18.1Q12 18.9 12 17.3Z";
    internal const string TriangleData = "M2.5 6.7Q2.5 5.1 3.9 5.9L11.3 11.1Q12.4 12 11.3 12.9L3.9 18.1Q2.5 18.9 2.5 17.3Z";

    private static Geometry? s_play, s_pause, s_forward, s_triangle;

    /// <summary>Tests drive frames through <see cref="OnFrame"/> on a fixed clock.</summary>
    internal static bool ManualFrames { get; set; }

    private Spring _scale = new(1);
    private Spring _highlight = new(0);
    private Spring _playScale = new(1), _playOpacity = new(1);
    private Spring _pauseScale = new(MorphEnterScale), _pauseOpacity = new(0);

    private double _s = 1, _h, _playS = 1, _playO = 1, _pauseS = MorphEnterScale, _pauseO;
    private double _slideStart = double.NaN, _slideEased, _slideOut, _slideIn;
    private int _slideQueue;
    private double _pressStart = double.NegativeInfinity, _releaseAt = double.NaN;
    private double? _lastFrame;

    private bool _downPending, _upPending, _morphPending, _pressSeen, _frameRequested, _settled;
    private int _slideRequests;

    private Button? _button;
    private IBrush? _derivedHighlight;
    private readonly Action<TimeSpan> _onFrame;

    static TransportGlyph()
    {
        AffectsRender<TransportGlyph>(KindProperty, ForegroundProperty, HighlightBrushProperty, HighlightSizeProperty);
        IsPlayingProperty.Changed.AddClassHandler<TransportGlyph>((g, _) => g.OnPlayingChanged());
        ForegroundProperty.Changed.AddClassHandler<TransportGlyph>((g, _) => g._derivedHighlight = null);
        ReduceMotionProperty.Changed.AddClassHandler<TransportGlyph>((g, _) => g.SettleNow());
    }

    public TransportGlyph()
    {
        _onFrame = ts => { _frameRequested = false; OnFrame(ts.TotalSeconds); };
        IsHitTestVisible = false; // the button takes the pointer, as it did with the Path inside
    }

    // ── Read-outs for tests ───────────────────────────────────────────────────────────
    internal double GlyphScale => _s;
    internal double HighlightLevel => _h;
    internal double SlideProgress => _slideEased;
    internal bool IsSliding => !double.IsNaN(_slideStart);
    internal double PlayOpacity => _playO;
    internal double PauseOpacity => _pauseO;
    internal double PlayScale => _playS;
    internal double PauseScale => _pauseS;

    /// <summary>True while anything moves or waits to move.</summary>
    internal bool IsMoving =>
        _downPending || _upPending || _morphPending || _slideRequests > 0 || IsSliding
        || !double.IsNaN(_releaseAt) || !_scale.AtRest || !_highlight.AtRest
        || !_playScale.AtRest || !_playOpacity.AtRest || !_pauseScale.AtRest || !_pauseOpacity.AtRest;

    private bool MotionOff => GetValue(ReduceMotionProperty);

    protected override Size MeasureOverride(Size availableSize) => new(Grid, Grid);

    // ── Hosting button ────────────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _button = this.FindAncestorOfType<Button>();
        if (_button != null)
        {
            _button.PropertyChanged += OnButtonPropertyChanged;
            _button.Click += OnButtonClick;
        }
        Unsettle();
        SettleNow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_button != null)
        {
            _button.PropertyChanged -= OnButtonPropertyChanged;
            _button.Click -= OnButtonClick;
            _button = null;
        }
        SettleNow();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsettle();
    }

    /// <summary>A play state that arrives with a (re)bind is shown, not animated: only changes
    /// after the dispatcher has finished the current work count as live.</summary>
    private void Unsettle()
    {
        _settled = false;
        Dispatcher.UIThread.Post(() => _settled = true, DispatcherPriority.Background);
    }

    private void OnButtonPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Button.IsPressedProperty) return;
        if (e.NewValue is true)
        {
            _pressSeen = true;
            _downPending = true;
        }
        else
        {
            _upPending = true;
        }
        Kick();
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        // Enter on a focused button clicks without a press: give it the same dip.
        if (!_pressSeen)
        {
            _downPending = true;
            _upPending = true;
        }
        _pressSeen = false;
        if (Kind != TransportKind.PlayPause) _slideRequests++;
        Kick();
    }

    private void OnPlayingChanged()
    {
        if (Kind != TransportKind.PlayPause) return;
        if (!_settled)
        {
            SnapMorph();
            InvalidateVisual();
            return;
        }
        _morphPending = true;
        Kick();
    }

    // ── Frame loop ────────────────────────────────────────────────────────────────────

    /// <summary>Starts the frame loop, or applies everything at once when nothing would draw
    /// the frames (reduced motion, off screen, not in a window).</summary>
    private void Kick()
    {
        if (MotionOff)
        {
            SettleNow();
            return;
        }
        if (ManualFrames) return; // the test drives OnFrame itself
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            SettleNow();
            return;
        }
        if (_frameRequested) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_onFrame);
    }

    /// <summary>
    /// Advances every motion to <paramref name="now"/> (seconds, the frame clock) and redraws.
    /// Events since the last frame start one frame back, so the first frame after a press
    /// already shows it moving.
    /// </summary>
    internal void OnFrame(double now)
    {
        var ev = _lastFrame is { } last ? Math.Max(last, now - Frame) : now - Frame;
        ev = Math.Min(ev, now);

        if (_downPending)
        {
            _downPending = false;
            _pressStart = ev;
            _releaseAt = double.NaN;
            _scale.Retarget(ev, PressScale, PressOmega, PressZeta);
            _highlight.Retarget(ev, 1, HighlightInOmega, 1);
        }
        if (_upPending)
        {
            _upPending = false;
            _releaseAt = Math.Max(ev, _pressStart + MinHold);
        }
        for (; _slideRequests > 0; _slideRequests--)
        {
            if (double.IsNaN(_slideStart)) _slideStart = ev;
            else _slideQueue = Math.Min(_slideQueue + 1, 2);
        }
        if (_morphPending)
        {
            _morphPending = false;
            BeginMorph(ev);
        }

        // Slide: chained steps follow on without a gap and each lands on the rest glyph.
        if (!double.IsNaN(_slideStart))
        {
            var u = (now - _slideStart) / SlideSeconds;
            while (u >= 1 && _slideQueue > 0)
            {
                _slideQueue--;
                _slideStart += SlideSeconds;
                u -= 1;
            }
            if (u >= 1)
            {
                // A release waiting on this step springs from the step's end, not from the
                // (earlier) moment the button was let go: that jumped the spring ahead.
                if (!double.IsNaN(_releaseAt)) _releaseAt = Math.Max(_releaseAt, _slideStart + SlideSeconds);
                _slideStart = double.NaN;
                _slideEased = _slideOut = _slideIn = 0;
            }
            else
            {
                u = Math.Max(0, u);
                _slideEased = SlideEase.Ease(u);
                _slideOut = SlideOutEase.Ease(u);
                _slideIn = SlideInEase.Ease(u);
            }
        }

        // Release: not before the minimum hold, nor before the slide step has run.
        if (!double.IsNaN(_releaseAt))
        {
            var at = _releaseAt;
            // Apple keeps the glyph down until the conveyor step has landed, then springs it.
            if (!double.IsNaN(_slideStart)) at = Math.Max(at, _slideStart + SlideSeconds);
            if (now >= at)
            {
                _releaseAt = double.NaN;
                at = Math.Min(at, now);
                _scale.Retarget(at, 1, ReleaseOmega, ReleaseZeta);
                _highlight.Launch(at + HighlightHold, _highlight.Evaluate(at).Value, 0, HighlightOutOmega, 1);
            }
        }

        _s = _scale.Step(now);
        _h = Math.Clamp(_highlight.Step(now, 0.002), 0, 1);
        _playS = _playScale.Step(now);
        _playO = Math.Clamp(_playOpacity.Step(now, 0.002), 0, 1);
        _pauseS = _pauseScale.Step(now);
        _pauseO = Math.Clamp(_pauseOpacity.Step(now, 0.002), 0, 1);

        InvalidateVisual();
        if (IsMoving)
        {
            _lastFrame = now;
            Kick();
        }
        else
        {
            _lastFrame = null;
        }
    }

    private void BeginMorph(double ev)
    {
        ref var inScale = ref IsPlaying ? ref _pauseScale : ref _playScale;
        ref var inOpacity = ref IsPlaying ? ref _pauseOpacity : ref _playOpacity;
        ref var outScale = ref IsPlaying ? ref _playScale : ref _pauseScale;
        ref var outOpacity = ref IsPlaying ? ref _playOpacity : ref _pauseOpacity;

        // An incoming glyph that is (nearly) invisible starts small; one still fading out
        // from a quick double toggle turns around from where it is.
        var (o, _) = inOpacity.Evaluate(ev);
        if (o < 0.05)
        {
            // Apple's morph runs through a point: the incoming glyph appears only as the outgoing
            // one has all but vanished.
            inScale.Launch(ev + MorphEnterDelay, MorphEnterScale, 1, ShowScaleOmega, ShowScaleZeta);
            inOpacity.Launch(ev + MorphEnterDelay, o, 1, ShowOpacityOmega, 1);
        }
        else
        {
            inScale.Retarget(ev, 1, ShowScaleOmega, ShowScaleZeta);
            inOpacity.Retarget(ev, 1, ShowOpacityOmega, 1);
        }
        outScale.Retarget(ev, MorphHiddenScale, HideScaleOmega, 1);
        outOpacity.Retarget(ev, 0, HideOpacityOmega, 1);
    }

    private void SnapMorph()
    {
        var playing = IsPlaying;
        _playScale.Snap(playing ? MorphEnterScale : 1);
        _playOpacity.Snap(playing ? 0 : 1);
        _pauseScale.Snap(playing ? 1 : MorphEnterScale);
        _pauseOpacity.Snap(playing ? 1 : 0);
        _playS = _playScale.Target;
        _playO = _playOpacity.Target;
        _pauseS = _pauseScale.Target;
        _pauseO = _pauseOpacity.Target;
        _morphPending = false;
    }

    /// <summary>Ends every motion on its rest value; the circle shows only while pressed.</summary>
    private void SettleNow()
    {
        _downPending = _upPending = false;
        _slideRequests = 0;
        _slideQueue = 0;
        _slideStart = double.NaN;
        _slideEased = _slideOut = _slideIn = 0;
        _releaseAt = double.NaN;
        _lastFrame = null;
        _scale.Snap(1);
        _s = 1;
        var pressed = _button?.IsPressed == true;
        _highlight.Snap(pressed ? 1 : 0);
        _h = _highlight.Target;
        SnapMorph();
        InvalidateVisual();
    }

    // The conveyor's three pieces on their own curves (fitted to the recording): the trailing
    // triangle slides across ease-in-out, the leading one is half gone within three frames, the
    // new one grows in late.
    private static readonly CubicBezierEase SlideEase = new(0.35, 0, 0.25, 1);
    private static readonly CubicBezierEase SlideOutEase = new(0.2, 0.7, 0.3, 1);
    private static readonly CubicBezierEase SlideInEase = new(0.55, 0, 0.45, 1);

    // ── Drawing ───────────────────────────────────────────────────────────────────────

    private static void EnsureGeometry()
    {
        if (s_play != null) return;
        s_play = StreamGeometry.Parse(PlayData);
        s_pause = StreamGeometry.Parse(PauseData);
        s_forward = StreamGeometry.Parse(ForwardData);
        s_triangle = StreamGeometry.Parse(TriangleData);
    }

    private IBrush? HighlightFill()
    {
        if (HighlightBrush is { } b) return b;
        if (_derivedHighlight != null) return _derivedHighlight;
        var c = Foreground is ISolidColorBrush s ? s.Color : Colors.White;
        return _derivedHighlight = new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(255 * HighlightAlpha), c.R, c.G, c.B));
    }

    private static Matrix ScaleAbout(double s, double cx, double cy) =>
        Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var side = Math.Min(w, h);
        if (side <= 0) return;

        if (_h > 0.001 && HighlightFill() is { } fill)
        {
            var d = double.IsNaN(HighlightSize)
                ? side * (Kind == TransportKind.PlayPause ? 1.12 : 1.24)
                : HighlightSize;
            var r = d / 2 * (0.9 + 0.1 * _h);
            using (context.PushOpacity(_h))
                context.DrawEllipse(fill, null, new Point(w / 2, h / 2), r, r);
        }

        var brush = Foreground;
        if (brush is null) return;
        EnsureGeometry();

        // 24-grid → box (uniform, centred, what a Viewbox around a 24×24 Canvas does), with the
        // press scale about the grid's centre.
        var k = side / Grid;
        var toBox = ScaleAbout(_s, MidY, MidY) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation((w - side) / 2, (h - side) / 2);

        if (Kind == TransportKind.PlayPause)
        {
            DrawPiece(context, s_play!, brush, ScaleAbout(_playS, MidY, MidY) * toBox, _playO);
            DrawPiece(context, s_pause!, brush, ScaleAbout(_pauseS, MidY, MidY) * toBox, _pauseO);
            return;
        }

        // Previous is Next mirrored on the grid.
        if (Kind == TransportKind.Previous)
            toBox = Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(Grid, 0) * toBox;

        if (double.IsNaN(_slideStart))
        {
            DrawPiece(context, s_forward!, brush, toBox, 1);
            return;
        }

        var e = _slideEased;
        var gone = _slideOut;
        var grown = _slideIn;
        // Leading triangle shrinks away towards its tip; trailing slides into its place; a new
        // trailing triangle grows from its base.
        DrawPiece(context, s_triangle!, brush, Matrix.CreateTranslation(Step, 0) * ScaleAbout(1 - gone, TipX, MidY) * toBox, 1 - gone);
        DrawPiece(context, s_triangle!, brush, Matrix.CreateTranslation(Step * e, 0) * toBox, 1);
        DrawPiece(context, s_triangle!, brush, ScaleAbout(grown, BaseX, MidY) * toBox, grown);
    }

    private static void DrawPiece(DrawingContext context, Geometry geometry, IBrush brush, Matrix m, double opacity)
    {
        if (opacity <= 0.001 || Math.Abs(m.M11) < 1e-4) return;
        using (context.PushTransform(m))
        {
            if (opacity >= 0.999)
            {
                context.DrawGeometry(brush, null, geometry);
                return;
            }
            using (context.PushOpacity(opacity))
                context.DrawGeometry(brush, null, geometry);
        }
    }
}
