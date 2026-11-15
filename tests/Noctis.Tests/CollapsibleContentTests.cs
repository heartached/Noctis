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
    /// The Glide reveal is opt-in per instance. Only the Settings crossfade-duration block
    /// asks for it; every other collapsible must keep the exact fold it had, so the default
    /// stays Fold and its cubic curve.
    /// </summary>
    [AvaloniaFact]
    public void Glide_IsOptIn_AndTheDefaultFoldIsUnchanged()
    {
        var host = BuildHost();
        Assert.Equal(CollapsibleMotion.Fold, host.Motion);

        host.Reveal = 0.5;
        // Fold ties opacity and slide linearly to the height.
        Assert.Equal(0.5, host.Opacity, 3);
    }

    /// <summary>
    /// Mid-reveal is where the two motions actually differ, so assert there rather than at
    /// the ends, which both land on the same values either way.
    /// </summary>
    [AvaloniaFact]
    public void Glide_LandsOpacityAndSlideAheadOfTheHeight()
    {
        var host = BuildHost();
        host.Motion = CollapsibleMotion.Glide;
        host.Lift = 10;

        host.Reveal = 0.5;
        Layout(host);

        // Height is still the plain fraction of the child — the clip is unchanged.
        Assert.Equal(ChildHeight / 2, host.DesiredSize.Height, 3);

        // Opacity is well past the height it would track under Fold: the body is readable
        // for most of the reveal instead of washing in at half strength.
        Assert.True(host.Opacity > 0.9,
            $"glide opacity at half-open was {host.Opacity:F2}; it should be nearly landed");

        // The slide has all but settled while the panel is still growing.
        var lift = Assert.IsType<Avalonia.Media.TranslateTransform>(host.RenderTransform);
        Assert.True(lift.Y > -3.0 && lift.Y < 0,
            $"glide slide at half-open was {lift.Y:F2}px; it should be settling, not halfway");

        // Both ends still rest exactly where the fold does.
        host.Reveal = 1;
        Assert.Equal(1, host.Opacity, 3);
        Assert.Equal(0, lift.Y, 3);

        host.Reveal = 0;
        Assert.Equal(0, host.Opacity, 3);
        Assert.Equal(-10, lift.Y, 3);
    }

    /// <summary>
    /// Glide retimes the one transition rather than adding a second, and retimes it per
    /// direction: the open is the longer, decelerating curve and the close is shorter and
    /// eases in and out. The curve is swapped BEFORE the target is written, so the toggle
    /// starts on the new curve rather than re-curving a running fold.
    /// </summary>
    [AvaloniaFact]
    public void Glide_RetimesTheSingleTransition_PerDirection()
    {
        var host = BuildHost();
        host.Motion = CollapsibleMotion.Glide;
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

        var transition = Assert.IsType<DoubleTransition>(Assert.Single(host.Transitions!));

        host.IsOpen = false;
        var close = Assert.IsType<Noctis.Helpers.CubicBezierEase>(transition.Easing);
        var closeDuration = transition.Duration;
        // Ease-in-out: symmetric about the midpoint, so it neither snaps shut nor drags.
        Assert.Equal(0.5, close.Ease(0.5), 2);
        Assert.True(close.Ease(0.15) < 0.15, "the close should ease in, not start at speed");

        host.IsOpen = true;
        var open = Assert.IsType<Noctis.Helpers.CubicBezierEase>(transition.Easing);
        Assert.NotSame(close, open);
        Assert.True(transition.Duration > closeDuration,
            $"open {transition.Duration.TotalMilliseconds}ms should outlast close {closeDuration.TotalMilliseconds}ms");
        // Decelerating: most of the travel is done by the midpoint and it never overshoots.
        Assert.True(open.Ease(0.5) > 0.85, $"open at half time was {open.Ease(0.5):F2}; it should be nearly landed");
        foreach (var t in new[] { 0.7, 0.85, 0.95 })
            Assert.InRange(open.Ease(t), open.Ease(t - 0.1), 1.0);
    }

    /// <summary>
    /// End-to-end on the real path: the glide has to reach each rest value and stay there,
    /// same as the fold does.
    /// </summary>
    [AvaloniaFact]
    public void Glide_SettlesAtEachEnd_AndStaysThere()
    {
        var host = BuildHost();
        host.Motion = CollapsibleMotion.Glide;
        host.Lift = 10;
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        Pump(6);

        host.IsOpen = false;
        Pump(36);
        Assert.Equal(0, host.Reveal, 2);
        Assert.False(host.IsVisible);

        Pump(20);
        Assert.Equal(0, host.Reveal, 2);

        host.IsOpen = true;
        Pump(36);
        Assert.Equal(1, host.Reveal, 2);
        Assert.Equal(1, host.Opacity, 2);
        Assert.True(host.IsVisible);

        Pump(20);
        Assert.Equal(1, host.Reveal, 2);
    }

    /// <summary>
    /// Root-cause lock for "it stops the animation on close and just instant closes". A
    /// toggle that lands while the previous transition is still running makes Avalonia drop
    /// that transition BEFORE starting the new one, so for one synchronous notification the
    /// reveal reads as the new base value (0 on a close). The control took that flash as
    /// "fully shut" and turned IsVisible off at the START of the close, so the body vanished
    /// while the transition went on folding an invisible panel. Every case below must keep the
    /// body visible for as long as the reveal is above zero, and the height must move
    /// continuously, never jump.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(CollapsibleMotion.Glide, 8)]   // open nearly landed (the glide's long tail is still running)
    [InlineData(CollapsibleMotion.Glide, 3)]   // quick on-off, mid-open
    [InlineData(CollapsibleMotion.Glide, 40)]  // open fully finished
    [InlineData(CollapsibleMotion.Fold, 4)]
    public void Close_FoldsFromWhereverTheOpenGot_WithoutSnappingShut(CollapsibleMotion motion, int openFrames)
    {
        var (host, frames) = StartShut(motion);

        host.IsOpen = true;
        PumpRecord(host, frames, openFrames);
        var heightAtClose = host.Bounds.Height;

        host.IsOpen = false;
        AssertVisibleWhileRevealed(host, "right after the close toggle");
        var closeStart = frames.Count;
        PumpRecord(host, frames, 36);

        var close = frames.Skip(closeStart).ToList();
        // It folds: several distinct intermediate heights between where it was and nothing.
        var intermediate = close.Count(f => f.Visible && f.Height > 1 && f.Height < heightAtClose - 1);
        Assert.True(intermediate >= 3,
            $"close passed through {intermediate} intermediate heights (from {heightAtClose:F0}px): {Describe(close)}");
        AssertContinuous(close, heightAtClose);
        Assert.Equal(0, host.Reveal, 2);
        Assert.False(host.IsVisible);
    }

    /// <summary>Re-opening mid-close must resume from the current height, not flash to full.</summary>
    [AvaloniaFact]
    public void Reopen_MidClose_ResumesFromTheCurrentHeight()
    {
        var (host, frames) = StartShut(CollapsibleMotion.Glide);
        host.IsOpen = true;
        PumpRecord(host, frames, 40);
        host.IsOpen = false;
        PumpRecord(host, frames, 5);
        var revealMidClose = host.Reveal;
        Assert.InRange(revealMidClose, 0.05, 0.95);

        host.IsOpen = true;
        // The teardown flash would be the new base value, 1.0, at full opacity.
        Assert.True(host.Reveal < 0.99, $"reopen jumped the reveal to {host.Reveal:F3}");
        var reopenStart = frames.Count;
        PumpRecord(host, frames, 40);

        AssertContinuous(frames.Skip(reopenStart).ToList(), frames[reopenStart - 1].Height);
        Assert.Equal(1, host.Reveal, 2);
        Assert.True(host.IsVisible);
    }

    /// <summary>Several flips in a row, then a close: never invisible while revealed, ends shut.</summary>
    [AvaloniaFact]
    public void RapidFlips_NeverHideARevealedBody_AndEndShut()
    {
        var (host, frames) = StartShut(CollapsibleMotion.Glide);
        foreach (var open in new[] { true, false, true, false, true, false })
        {
            host.IsOpen = open;
            AssertVisibleWhileRevealed(host, $"right after IsOpen={open}");
            PumpRecord(host, frames, 2);
        }
        PumpRecord(host, frames, 36);
        Assert.Equal(0, host.Reveal, 2);
        Assert.False(host.IsVisible);
    }

    /// <summary>
    /// The body can change size while it folds (a Settings block with its own toggles). The
    /// close still has to fold the new height down and end shut.
    /// </summary>
    [AvaloniaFact]
    public void Close_StillFolds_WhenTheBodyResizesMidway()
    {
        var (host, frames) = StartShut(CollapsibleMotion.Glide);
        host.IsOpen = true;
        PumpRecord(host, frames, 6);
        host.IsOpen = false;
        PumpRecord(host, frames, 3);
        ((Border)host.Child!).Height = ChildHeight + 80;
        var resized = frames.Count;
        PumpRecord(host, frames, 36);

        var rest = frames.Skip(resized).ToList();
        Assert.True(rest.Count(f => f.Visible && f.Height > 1) >= 2, Describe(rest));
        Assert.All(rest, f => Assert.True(f.Height <= (ChildHeight + 80) * f.Reveal + 1.5, Describe(rest)));
        Assert.False(host.IsVisible);
    }

    private readonly record struct Frame(double Reveal, double Height, bool Visible, bool IsOpen);

    private static (CollapsibleContent host, List<Frame> frames) StartShut(CollapsibleMotion motion)
    {
        // Shut before it is shown, like a Settings toggle that starts off.
        var host = new CollapsibleContent
        {
            Motion = motion,
            Lift = 10,
            IsOpen = false,
            Child = new Border { Width = ChildWidth, Height = ChildHeight }
        };
        var panel = new StackPanel();
        panel.Children.Add(new Border { Height = 40 });
        panel.Children.Add(host);
        new Window { Width = 400, Height = 600, Content = panel }.Show();
        Pump(6);
        Assert.False(host.IsVisible);
        return (host, new List<Frame>());
    }

    private static void PumpRecord(CollapsibleContent host, List<Frame> frames, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var height = host.IsVisible ? host.Bounds.Height : 0;
            frames.Add(new Frame(host.Reveal, height, host.IsVisible, host.IsOpen));
            AssertVisibleWhileRevealed(host, $"frame {frames.Count}");
            Thread.Sleep(16);
        }
    }

    private static void AssertVisibleWhileRevealed(CollapsibleContent host, string when)
    {
        if (host.Reveal > 0.001)
            Assert.True(host.IsVisible,
                $"{when}: body hidden while still {host.Reveal:P0} revealed — the close snapped shut instead of folding");
    }

    /// <summary>
    /// No frame-to-frame step bigger than the steepest the curves produce at ~16ms frames
    /// (the glide close's ease-in-out peaks near 1.6x linear speed: about 30% of the travel
    /// per 30ms frame on a loaded test runner). A snap is a 100% step.
    /// </summary>
    private static void AssertContinuous(List<Frame> frames, double startHeight)
    {
        var previous = startHeight;
        foreach (var f in frames)
        {
            Assert.True(Math.Abs(f.Height - previous) <= ChildHeight * 0.45 + 1,
                $"height jumped {previous:F0} -> {f.Height:F0}: {Describe(frames)}");
            previous = f.Height;
        }
    }

    private static string Describe(IEnumerable<Frame> frames) =>
        string.Join(" ", frames.Select(f => $"{f.Reveal:F2}/{f.Height:F0}{(f.Visible ? "" : "x")}"));

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
