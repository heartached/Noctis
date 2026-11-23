using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

/// <summary>
/// The phone's one motion system: every page and sheet moves on the same curve (GlideEase,
/// an iOS sheet's cubic-bezier 0.32, 0.72, 0, 1) in one of three timings.
/// <list type="bullet">
/// <item>Sheets (Settings, Now Playing, the Queue) rise from the bottom edge over
/// <see cref="Sheet"/> while the app behind steps back (<see cref="BehindScale"/>) and dims;
/// closing is the same run reversed.</item>
/// <item>Pushes (a list into its page, Settings into Account or a Settings page) slide the new
/// page in from the right over <see cref="Push"/> while the old one drifts
/// <see cref="PushShift"/> of the width left under a shade; Back reverses it.</item>
/// <item>Fades (a tab switch, Lyrics over the player) over <see cref="Fade"/>.</item>
/// </list>
/// Only RenderTransform and Opacity move, so a run costs no layout per frame. A run is two
/// steps — the start values with no transitions, then the targets with transitions attached two
/// frames later — because a code-built Animation cannot drive RenderTransform on Avalonia 12,
/// while a TransformOperationsTransition can, and because the first frame of a page just put on
/// screen is the slow one: a run started inside it had covered most of its course before
/// anything was drawn. A run started on a control that is already moving continues from where
/// it is, so a Back mid-push turns the page around rather than jumping.
/// </summary>
public static class Motion
{
    public static readonly TimeSpan Sheet = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan Push = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(180);

    /// <summary>How far the app behind a sheet steps back, and how dark it goes.</summary>
    public const double BehindScale = 0.94, BehindDim = 0.3;

    /// <summary>The share of the width a page under a push drifts left, and how dark its shade goes.</summary>
    public const double PushShift = 0.3, PushShade = 0.12;

    public static readonly Easing Ease = new GlideEase();

    public static ITransform TranslateX(double x) => TransformOperations.Parse($"translateX({Px(x)}px)");
    public static ITransform TranslateY(double y) => TransformOperations.Parse($"translateY({Px(y)}px)");
    public static ITransform Scale(double s) => TransformOperations.Parse($"scale({Px(s)})");
    public static ITransform Identity => TransformOperations.Parse("translateX(0px)");

    private static string Px(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Run
    {
        public required Control Control { get; init; }
        public ITransform? To { get; set; }
        public double? Opacity { get; set; }
        public Action? Done { get; set; }
        public bool Started { get; set; }
        public IDisposable? Timer { get; set; }
        /// <summary>Where an entering control jumps to as it starts (see <c>enter</c>).</summary>
        public ITransform? EnterFrom { get; set; }
        public bool Entering { get; set; }
    }

    private static readonly Dictionary<Control, Run> Runs = new();

    /// <summary>Whether <paramref name="control"/> is moving (or about to).</summary>
    public static bool IsRunning(Control control) => Runs.ContainsKey(control);

    /// <summary>Whether anything at all is moving; tests wait on it.</summary>
    public static bool AnyRunning => Runs.Count > 0;

    /// <summary>
    /// Moves <paramref name="control"/> to <paramref name="to"/> and/or <paramref name="opacity"/>
    /// over <paramref name="duration"/>, then calls <paramref name="done"/>. A still control first
    /// takes <paramref name="from"/> / <paramref name="fromOpacity"/> (when given) and starts two
    /// frames later; a moving one turns towards the new target from where it is, and the run it
    /// was on is dropped, its <c>done</c> with it (the new run's <c>done</c> owns the end state).
    /// With <paramref name="enter"/> (a page or sheet just put on screen) the control is laid out
    /// where it will land but stays transparent until the run starts, and only then jumps to
    /// <paramref name="from"/>: a list measured off screen realizes no rows (its effective viewport
    /// is empty, and a render transform runs no layout pass to update it), so a page that slid in
    /// from there showed one row until something laid it out again.
    /// </summary>
    public static void To(Control control, TimeSpan duration, ITransform? to = null, double? opacity = null,
        Action? done = null, ITransform? from = null, double? fromOpacity = null, bool enter = false)
    {
        var moving = Runs.TryGetValue(control, out var old) && old.Started;
        old?.Timer?.Dispose();
        if (old is { Entering: true, Started: false }) control.Opacity = 1;
        var run = new Run { Control = control, To = to, Opacity = opacity, Done = done };
        Runs[control] = run;
        if (moving && Covers(control.Transitions, to != null, opacity != null))
        {
            // The transitions in place carry on from the current (animated) value.
            Start(run, duration, attach: false);
            return;
        }
        control.Transitions = null;
        if (enter && from != null)
        {
            run.Entering = true;
            run.EnterFrom = from;
            control.RenderTransform = null;
            control.Opacity = 0;
        }
        else if (from != null) control.RenderTransform = from;
        if (fromOpacity is { } o) control.Opacity = o;
        void StartOnce()
        {
            if (Runs.TryGetValue(control, out var current) && current == run && !run.Started) Start(run, duration, attach: true);
        }
        AfterNextFrame(control, StartOnce);
        // Should no frame come (the window just went to the background), start anyway.
        run.Timer = After(TimeSpan.FromSeconds(1), StartOnce);
    }

    private static bool Covers(Transitions? transitions, bool transform, bool opacity) =>
        transitions != null
        && (!transform || transitions.Any(t => t is TransformOperationsTransition))
        && (!opacity || transitions.Any(t => t is DoubleTransition d && d.Property == Visual.OpacityProperty));

    private static void Start(Run run, TimeSpan duration, bool attach)
    {
        var c = run.Control;
        run.Started = true;
        run.Timer?.Dispose();
        if (run.Entering)
        {
            c.RenderTransform = run.EnterFrom;
            c.Opacity = 1;
            run.Entering = false;
        }
        if (attach)
        {
            var transitions = new Transitions();
            if (run.To != null) transitions.Add(new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = Ease });
            if (run.Opacity != null) transitions.Add(new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = Ease });
            c.Transitions = transitions;
        }
        if (run.To != null) c.RenderTransform = run.To;
        if (run.Opacity is { } o) c.Opacity = o;
        // A little past the curve's end: the last frames still land before the transitions go.
        run.Timer = After(duration + TimeSpan.FromMilliseconds(60), () => Complete(run));
    }

    private static void Complete(Run run)
    {
        if (!Runs.TryGetValue(run.Control, out var current) || current != run) return;
        Runs.Remove(run.Control);
        run.Timer?.Dispose();
        var c = run.Control;
        // Without transitions the values are their targets at once; later direct sets stay direct.
        c.Transitions = null;
        if (run.Entering) c.Opacity = 1;
        if (run.To != null) c.RenderTransform = run.To;
        if (run.Opacity is { } o) c.Opacity = o;
        run.Done?.Invoke();
    }

    /// <summary>Jumps <paramref name="control"/> to the end of its run and calls its <c>done</c>.</summary>
    public static void Finish(Control control)
    {
        if (Runs.TryGetValue(control, out var run)) Complete(run);
    }

    /// <summary>Drops <paramref name="control"/>'s run where it stands, <c>done</c> uncalled; the
    /// caller sets the end state itself.</summary>
    public static void Stop(Control control)
    {
        if (!Runs.Remove(control, out var run)) return;
        run.Timer?.Dispose();
        control.Transitions = null;
        if (run.Entering) control.Opacity = 1;
    }

    /// <summary>Ends every run at once. Internal for tests: the headless clock moves only when ticked.</summary>
    internal static void FinishAll()
    {
        // A done may start another run (a page retired under a pop): loop until nothing moves.
        for (var guard = 0; guard < 10 && Runs.Count > 0; guard++)
            foreach (var run in Runs.Values.ToList()) Complete(run);
    }

    /// <summary>Test hook: when set, <see cref="After"/> schedules on it instead of a pool timer,
    /// so a test that also steps the animation clock runs every curve on its own time.</summary>
    internal static Func<TimeSpan, Action, IDisposable>? Timers;

    /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="delay"/>,
    /// unless disposed first. A pool timer posting back rather than a DispatcherTimer, which the
    /// headless test platform never fires.</summary>
    private static IDisposable After(TimeSpan delay, Action action)
    {
        if (Timers is { } timers) return timers(delay, action);
        var cancel = new Cancel();
        _ = Task.Delay(delay).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!cancel.IsCancelled) action();
        }), TaskScheduler.Default);
        return cancel;
    }

    private sealed class Cancel : IDisposable
    {
        public bool IsCancelled { get; private set; }
        public void Dispose() => IsCancelled = true;
    }

    /// <summary>Two frames on: the one that lays out and draws the start values, then the next.
    /// Each request also marks the control for redraw: a frame request alone came only once
    /// something else redrew (up to 440 ms on a still screen, headless curves 02_settings_close
    /// and 14_lyrics_close). Off screen (no TopLevel yet), after the dispatcher has done its
    /// layout work.</summary>
    private static void AfterNextFrame(Control control, Action action)
    {
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
            return;
        }
        top.RequestAnimationFrame(_ =>
        {
            top.RequestAnimationFrame(_ => action());
            control.InvalidateVisual();
        });
        control.InvalidateVisual();
    }
}
