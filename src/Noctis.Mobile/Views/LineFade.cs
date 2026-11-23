using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Converters;

namespace Noctis.Mobile.Views;

/// <summary>
/// The lyric line's dimming and depth blur as the sung line moves on: the opacity fades over
/// <see cref="FadeMs"/> and the blur eases over <see cref="BlurMs"/>, as the line style's
/// transitions did. S23 (11-23): those transitions ran on the UI thread frame by frame at every
/// line change, a property change, a re-recorded line and (for the blur) a new effect per line on
/// every frame for half a second.
/// <para>The opacity is the compositor's: the line's Opacity is set at once and its composition
/// visual eases to it (an implicit animation on its Opacity), from wherever it stands.</para>
/// <para>The blur cannot be animated by the compositor (Avalonia 12.1.6 keeps a visual's effect
/// internal, with no animatable radius), so it steps: <see cref="BlurSteps"/> radii along the
/// same curve, one timer for every line.</para>
/// A line shown afresh (a row handed the next line, off the visual tree) takes both at once, as
/// the transitions (which do not run off the tree) did.
/// </summary>
public static class LineFade
{
    /// <summary>Turns the compositor fade on for the line's Opacity.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(LineFade));

    /// <summary>The line's depth blur radius (0: no effect).</summary>
    public static readonly AttachedProperty<double> BlurRadiusProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("BlurRadius", typeof(LineFade));

    // The line follows its BlurRadius (its attach handler is in place).
    private static readonly AttachedProperty<bool> BlurHookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("BlurHooked", typeof(LineFade));

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static double GetBlurRadius(Control element) => element.GetValue(BlurRadiusProperty);
    public static void SetBlurRadius(Control element, double value) => element.SetValue(BlurRadiusProperty, value);

    /// <summary>The opacity fade's length (the style's DoubleTransition, CubicEaseInOut).</summary>
    internal const double FadeMs = 500;

    /// <summary>The blur's length (the style's EffectTransition, CubicEaseOut).</summary>
    internal const double BlurMs = 400;

    /// <summary>Stretches of the blur's way, each showing the eased radius at its middle (the
    /// closest one radius comes to the curve over it), then the target: the first at once, the
    /// next every <see cref="BlurMs"/> / BlurSteps. Each the curve's value at its own start
    /// lagged the ease visibly (real Skia, a whole stretch behind).</summary>
    internal const int BlurSteps = 3;

    private static readonly CubicEaseInOut FadeEase = new();
    private static readonly CubicEaseOut BlurEase = new();
    private static readonly BlurRadiusToEffectConverter Effects = new();

    static LineFade()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not true) return;
            control.AttachedToVisualTree += (_, _) => InstallFade(control);
            InstallFade(control);
        });
        BlurRadiusProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            // A line's first value is its look, not a change of it.
            var first = !control.GetValue(BlurHookedProperty);
            if (first)
            {
                control.SetValue(BlurHookedProperty, true);
                control.AttachedToVisualTree += (_, _) => ShowBlur(control, GetBlurRadius(control));
            }
            var to = args.GetNewValue<double>();
            if (first || !control.IsAttachedToVisualTree()) ShowBlur(control, to);
            else StartBlur(control, to);
        });
    }

    // ── Opacity ──

    private static void InstallFade(Control control)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual) return;
        // The value the line shows now: a later sync of the same value is no change, so only a
        // real change runs the fade (a fresh visual would otherwise fade in from 1).
        visual.ImplicitAnimations = null;
        visual.Opacity = (float)control.Opacity;
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertExpressionKeyFrame(1f, "this.FinalValue", FadeEase);
        fade.Duration = TimeSpan.FromMilliseconds(FadeMs);
        var implicitAnimations = visual.Compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Opacity"] = fade;
        visual.ImplicitAnimations = implicitAnimations;
    }

    // ── Blur ──

    private sealed class BlurRun
    {
        public double From, To, StartMs;
        public int Step;
    }

    private static readonly Dictionary<Control, BlurRun> Running = new();
    private static readonly List<Control> Done = new();
    // A pool timer posting back (a DispatcherTimer is never fired by the headless test platform,
    // see Motion.After); it runs only while a blur steps.
    private static Timer? _timer;
    private static bool _timerOn;
    private static readonly Action Step = StepBlurs;

    /// <summary>The radius the line shows (tests).</summary>
    internal static double ShownRadius(Control control) => (control.Effect as IBlurEffect)?.Radius ?? 0;

    private static void ShowBlur(Control control, double radius)
    {
        Running.Remove(control);
        var effect = (IEffect?)Effects.Convert(radius, typeof(IEffect), null, System.Globalization.CultureInfo.InvariantCulture);
        if (!ReferenceEquals(control.Effect, effect)) control.Effect = effect;
    }

    private static void StartBlur(Control control, double to)
    {
        var from = ShownRadius(control);
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (from == to)
        {
            Running.Remove(control);
            return;
        }
        if (!Running.TryGetValue(control, out var run)) Running[control] = run = new BlurRun();
        run.From = from;
        run.To = to;
        run.StartMs = Now();
        run.Step = 0;
        Show(control, RadiusAt(run, 0));
        if (_timerOn) return;
        _timerOn = true;
        _timer ??= new Timer(_ => Dispatcher.UIThread.Post(Step));
        var every = TimeSpan.FromMilliseconds(BlurMs / BlurSteps);
        _timer.Change(every, every);
    }

    private static void StepBlurs()
    {
        var now = Now();
        foreach (var (control, run) in Running)
        {
            // The step due now (a late tick catches up), the curve's value at its time.
            var due = Math.Min(BlurSteps, (int)Math.Floor((now - run.StartMs) / (BlurMs / BlurSteps) + 0.25));
            if (due <= run.Step) continue;
            run.Step = due;
            Show(control, RadiusAt(run, due));
            if (due >= BlurSteps || !control.IsAttachedToVisualTree()) Done.Add(control);
        }
        foreach (var control in Done)
        {
            if (Running.Remove(control, out var run) && !control.IsAttachedToVisualTree()) ShowBlur(control, run.To);
        }
        Done.Clear();
        if (Running.Count > 0 || !_timerOn) return;
        _timerOn = false;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The radius shown over stretch <paramref name="step"/> (the target from the last).</summary>
    private static double RadiusAt(BlurRun run, int step) =>
        step >= BlurSteps ? run.To : run.From + (run.To - run.From) * BlurEase.Ease((step + 0.5) / BlurSteps);

    private static void Show(Control control, double radius)
    {
        var effect = (IEffect?)Effects.Convert(radius, typeof(IEffect), null, System.Globalization.CultureInfo.InvariantCulture);
        if (!ReferenceEquals(control.Effect, effect)) control.Effect = effect;
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
