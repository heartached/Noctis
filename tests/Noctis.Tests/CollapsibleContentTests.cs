using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Home sections fold through <see cref="CollapsibleContent"/>, whose whole point is
/// that the fold is a CLIP: the child is measured and arranged at its natural height on
/// every frame and only the height this control reports shrinks. Animating the child
/// instead would re-wrap each section's ItemsControl rows mid-fold.
/// </summary>
public class CollapsibleContentTests
{
    private const double ChildHeight = 200;
    private const double ChildWidth = 300;

    /// <summary>A child with a fixed natural size, so the measure math is checkable.</summary>
    private static CollapsibleContent BuildHost() => new()
    {
        Child = new Border { Width = ChildWidth, Height = ChildHeight }
    };

    private static void Layout(CollapsibleContent host)
    {
        host.Measure(new Size(ChildWidth, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
    }

    [AvaloniaFact]
    public void Open_MeasuresToTheChildsFullHeight()
    {
        var host = BuildHost();
        Layout(host);

        Assert.Equal(ChildHeight, host.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void MidFold_MeasuresToTheRevealedFraction()
    {
        var host = BuildHost();
        // Set directly rather than via IsOpen: this asserts the measure math at a point
        // the transition would pass through, without waiting on the animation clock.
        host.Reveal = 0.5;
        Layout(host);

        Assert.Equal(ChildHeight / 2, host.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void MidFold_StillArrangesTheChildAtFullHeight()
    {
        var host = BuildHost();
        host.Reveal = 0.25;
        Layout(host);

        // The child keeps its natural box and slides up behind the clip. If this ever
        // reports the folded height, the section is squashing instead of clipping and
        // artwork will distort on the way down.
        Assert.Equal(ChildHeight, host.Child!.Bounds.Height, 3);
        Assert.True(host.ClipToBounds, "the fold relies on the clip to hide the overflow");
    }

    [AvaloniaFact]
    public void Shut_CollapsesToNothingAndStopsMeasuringTheChild()
    {
        var host = BuildHost();
        host.IsOpen = false;

        // Unarmed (never attached to a visual tree), so the reveal snaps rather than
        // animating — a section restored folded from settings must come up folded.
        Assert.Equal(0, host.Reveal, 3);

        // IsVisible off is what keeps a folded section free: the ItemsControl inside is
        // never measured, so its containers stay unrealized and its artwork never loads.
        Assert.False(host.IsVisible);
    }

    [AvaloniaFact]
    public void Reopening_RestoresVisibilityBeforeTheFoldRuns()
    {
        var host = BuildHost();
        host.IsOpen = false;
        Assert.False(host.IsVisible);

        host.IsOpen = true;

        // Visibility has to come back first or there is no measured height to grow into.
        Assert.True(host.IsVisible);
        Assert.Equal(1, host.Reveal, 3);

        Layout(host);
        Assert.Equal(ChildHeight, host.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void EmptyHost_MeasuresToNothing()
    {
        var host = new CollapsibleContent();
        host.Measure(new Size(ChildWidth, double.PositiveInfinity));

        Assert.Equal(0, host.DesiredSize.Height, 3);
    }

    /// <summary>
    /// Root-cause lock for a fold that undid itself. Written with SetCurrentValue, the
    /// reveal target only retargeted the running transition and left the BASE value
    /// unset — so when turning IsVisible off at the end of a close tore the animation
    /// layer down, the property fell back to its registered default of 1.0 and the
    /// section animated straight back open.
    /// </summary>
    [AvaloniaFact]
    public void Folding_WritesARealBaseValue_NotJustATransitionTarget()
    {
        var host = BuildHost();

        host.IsOpen = false;
        var shut = host.GetBaseValue(CollapsibleContent.RevealProperty);
        Assert.True(shut.HasValue, "shut must write a base value or the fold springs back open");
        Assert.Equal(0, shut.Value, 3);

        host.IsOpen = true;
        var open = host.GetBaseValue(CollapsibleContent.RevealProperty);
        Assert.True(open.HasValue);
        Assert.Equal(1, open.Value, 3);
    }

    /// <summary>
    /// Opening and shutting must be the same animation, not two. A curve that eased out
    /// one way and in the other is the same shape reversed in time but reads as two
    /// different motions, so the easing is fixed and symmetric.
    /// </summary>
    [AvaloniaFact]
    public void Fold_UsesOneSymmetricEasing_InBothDirections()
    {
        var host = BuildHost();
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        // Transitions arm at Loaded priority once attached.
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

        var transition = Assert.IsType<DoubleTransition>(Assert.Single(host.Transitions!));
        var easingWhileOpen = transition.Easing;
        Assert.IsType<CubicEaseInOut>(easingWhileOpen);

        host.IsOpen = false;
        Assert.Same(easingWhileOpen, transition.Easing);

        host.IsOpen = true;
        Assert.Same(easingWhileOpen, transition.Easing);
    }

    /// <summary>
    /// End-to-end: the fold runs on the wall clock, so drive real frames and check each
    /// direction reaches its rest value AND stays there.
    /// </summary>
    [AvaloniaFact]
    public void Fold_SettlesAtEachEnd_AndStaysThere()
    {
        var host = BuildHost();
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        Pump(6);

        host.IsOpen = false;
        Pump(30);
        Assert.Equal(0, host.Reveal, 2);
        Assert.False(host.IsVisible);

        // The spring-back showed up only after the close had already landed.
        Pump(20);
        Assert.Equal(0, host.Reveal, 2);

        host.IsOpen = true;
        Pump(30);
        Assert.Equal(1, host.Reveal, 2);
        Assert.True(host.IsVisible);

        Pump(20);
        Assert.Equal(1, host.Reveal, 2);
    }

    /// <summary>
    /// The fold must not jump on its last frame. A Home section is a header plus a
    /// folding body; when the gap between them came from the panel's Spacing, StackPanel
    /// dropped it outright the instant the body's IsVisible went false, and the whole page
    /// below snapped up 15px right as the animation landed — the "stutter as it closes".
    /// The gap now lives on the body as a margin, inside the measured (animated) height.
    /// </summary>
    [AvaloniaFact]
    public void SectionHeight_IsContinuous_ThroughTheLastFrameOfTheFold()
    {
        const double HeaderHeight = 40;
        const double Gap = 14;

        var host = new CollapsibleContent
        {
            Child = new Border { Width = ChildWidth, Height = ChildHeight, Margin = new Thickness(0, Gap, 0, 0) }
        };
        // No Spacing — that is the point.
        var section = new StackPanel();
        section.Children.Add(new Border { Width = ChildWidth, Height = HeaderHeight });
        section.Children.Add(host);

        double SectionHeight()
        {
            section.InvalidateMeasure();
            section.Measure(new Size(ChildWidth, double.PositiveInfinity));
            return section.DesiredSize.Height;
        }

        Assert.Equal(HeaderHeight + Gap + ChildHeight, SectionHeight(), 3);

        host.Reveal = 0.001;
        var lastAnimatedFrame = SectionHeight();

        host.IsVisible = false;
        var shut = SectionHeight();

        Assert.Equal(HeaderHeight, shut, 3);
        Assert.True(lastAnimatedFrame - shut <= 1.0,
            $"the fold jumps {lastAnimatedFrame - shut:F1}px on its last frame; the header gap must fold with the body");
    }

    /// <summary>
    /// Transitions run off the wall clock, so ticks have to be spaced in real time or
    /// the animation sits at its start value forever.
    /// </summary>
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }
}
