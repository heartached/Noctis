using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Easing = Avalonia.Animation.Easings.Easing;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Motion for the queue confirmation pill (<see cref="QueueToastViewModel"/>). Transitions
/// only, swapped per direction the way the sidebar search capsule does it: a code-built
/// keyframe Animation can't drive RenderTransform, and SplineEasing is broken, hence
/// <see cref="CubicBezierEase"/>. The view model owns the 2 s hold; this class only moves.
/// </summary>
public partial class QueueToastView : UserControl
{
    internal const int EnterMs = 340;
    internal const int LeaveMs = 200;
    private const int PulseUpMs = 110;

    // Rises from just above the island, a touch small; settles with a hair of overshoot.
    private static readonly TransformOperations PillBelow = TransformOperations.Parse("translateY(14px) scale(0.92)");
    private static readonly TransformOperations PillRest = TransformOperations.Parse("translateY(0px) scale(1)");
    private static readonly TransformOperations PillAway = TransformOperations.Parse("translateY(8px) scale(0.96)");
    private static readonly TransformOperations BadgeSmall = TransformOperations.Parse("scale(0.55)");
    private static readonly TransformOperations BadgeRest = TransformOperations.Parse("scale(1)");
    private static readonly TransformOperations BadgePeak = TransformOperations.Parse("scale(1.16)");

    private static readonly Easing Settle = new CubicBezierEase(0.22, 1, 0.36, 1);        // ease-out-quint
    private static readonly Easing SettleBounce = new CubicBezierEase(0.34, 1.32, 0.64, 1); // slight overshoot
    private static readonly Easing Exit = new CubicBezierEase(0.4, 0, 1, 1);               // ease-in

    private QueueToastViewModel? _vm;
    private int _generation;      // drops a stale collapse after a leave was overtaken
    private int _pulseGeneration; // drops a stale pulse settle after a newer pulse

    public QueueToastView()
    {
        InitializeComponent();
        Pill.RenderTransform = PillBelow;
        Badge.RenderTransform = BadgeSmall;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as QueueToastViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        if (_vm.IsShown) Enter();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(QueueToastViewModel.IsShown))
        {
            if (_vm.IsShown) Enter();
            else Leave();
        }
        // Show bumps ShowCount before IsShown: the first add is handled by Enter above;
        // a repeat while the pill is up (IsShown stays true) only pulses the badge.
        else if (e.PropertyName == nameof(QueueToastViewModel.ShowCount) && _vm.IsShown && Pill.IsVisible)
        {
            Pulse();
        }
    }

    /// <summary>True between Enter and the end of Leave's fade (tests read it).</summary>
    internal bool IsPillOnScreen => Pill.IsVisible;

    /// <summary>True once the tick's stroke-in has been started for the current show.</summary>
    internal bool IsTickDrawing => Tick.Classes.Contains("draw");

    private void Enter()
    {
        ++_generation;
        if (!Pill.IsVisible)
        {
            // Start pose without transitions so every entrance rises from below, then show.
            Pill.Transitions = null;
            Badge.Transitions = null;
            Pill.Opacity = 0;
            Pill.RenderTransform = PillBelow;
            Badge.RenderTransform = BadgeSmall;
            Tick.Classes.Set("draw", false);
            Pill.IsVisible = true;
        }

        Pill.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = Settle },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(EnterMs), Easing = SettleBounce },
        };
        Badge.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(EnterMs),
                Delay = TimeSpan.FromMilliseconds(40), Easing = SettleBounce,
            },
        };
        Pill.Opacity = 1;
        Pill.RenderTransform = PillRest;
        Badge.RenderTransform = BadgeRest;
        // Selector-driven keyframes (StrokeDashOffset is a plain double); FillMode Both
        // keeps the tick hidden through its delay and drawn once it lands.
        Tick.Classes.Set("draw", true);
    }

    private void Leave()
    {
        var generation = ++_generation;
        Pill.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(LeaveMs), Easing = Exit },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(LeaveMs), Easing = Exit },
        };
        Pill.Opacity = 0;
        Pill.RenderTransform = PillAway;
        // Out of the tree's render work once the fade has played (unless a new add
        // brought it back meanwhile, which bumps the generation).
        DispatcherTimer.RunOnce(() =>
        {
            if (generation != _generation) return;
            Pill.IsVisible = false;
            Tick.Classes.Set("draw", false);
        }, TimeSpan.FromMilliseconds(LeaveMs + 40));
    }

    /// <summary>A repeat add while the pill is up: the badge swells and settles (two
    /// transition steps), the text has already swapped through its bindings.</summary>
    private void Pulse()
    {
        var generation = ++_pulseGeneration;
        Badge.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(PulseUpMs), Easing = Settle },
        };
        Badge.RenderTransform = BadgePeak;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation != _pulseGeneration) return;
            Badge.Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = SettleBounce },
            };
            Badge.RenderTransform = BadgeRest;
        }, TimeSpan.FromMilliseconds(PulseUpMs));
    }
}
