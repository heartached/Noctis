using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// An idle phone screen asks Avalonia for no frames. Every animation on the global clock, even
/// one paused because its control is hidden, keeps MediaContext rendering every 16 ms (and on
/// Android, with the dispatcher's two clocks apart, that 16 ms timer spun the UI thread at a
/// full core). The scan bar on Home and Library was indeterminate while hidden: two endless
/// Fluent animations, always on the clock. And the dispatcher's clock is the loop's own.
/// </summary>
public class MobileIdleClockTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The endless animations subscribed to the global clock, paused or not: each keeps
    /// frames coming when nothing on screen moves. (Finite ones are left out: the headless
    /// clock never ticks, so a transition started by the mount never finishes here.)</summary>
    private static string[] EndlessAnimationsOnClock()
    {
        var clockType = typeof(Avalonia.Animation.Animation).Assembly.GetType("Avalonia.Animation.Clock")!;
        var found = new System.Collections.Generic.List<string>();
        Walk(clockType.GetProperty("GlobalClock", Any)!.GetValue(null)!, 0);
        return found.ToArray();

        // Clock -> its observable's observers -> each observer's OnNext target (a child Clock or
        // an AnimationInstance, whose Animation says whether it ever ends).
        void Walk(object node, int depth)
        {
            if (depth > 6) return;
            if (node.GetType().Name.StartsWith("AnimationInstance"))
            {
                if (node.GetType().GetField("_animation", Any)?.GetValue(node) is Avalonia.Animation.Animation a
                    && a.IterationCount == Avalonia.Animation.IterationCount.Infinite)
                    found.Add($"{a.Duration} on {node.GetType().GetField("_targetControl", Any)?.GetValue(node)?.GetType().Name}");
                return;
            }
            for (var t = node.GetType(); t != null; t = t.BaseType)
            {
                if (t.GetField("_observers", Any | BindingFlags.DeclaredOnly)?.GetValue(node) is IEnumerable observers)
                    foreach (var o in observers) Next(o, depth);
                if (t.GetField("_observable", Any | BindingFlags.DeclaredOnly)?.GetValue(node) is { } observable) Walk(observable, depth + 1);
            }
        }

        void Next(object observer, int depth)
        {
            if (observer.GetType().GetField("_onNext", Any)?.GetValue(observer) is Delegate { Target: { } target }) Walk(target, depth + 1);
            else Walk(observer, depth + 1);
        }
    }

    /// <summary>The device case: the launch scan shows the bar (its template and animation start),
    /// the scan ends and hides it. Hidden, the endless animation only paused and stayed on the
    /// clock for good; now it stops.</summary>
    [AvaloniaFact]
    public void A_finished_scan_leaves_no_endless_animation_on_the_clock()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("S0"), MobileFixtures.Song("S1") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        MobileFixtures.Land(window);

        rig.Shell.Library.IsScanning = true;
        window.UpdateLayout();
        var bar = view.GetVisualDescendants().OfType<ProgressBar>().First(b => b.IsEffectivelyVisible);
        Assert.True(bar.IsIndeterminate);
        Assert.NotEmpty(EndlessAnimationsOnClock());

        rig.Shell.Library.IsScanning = false;
        window.UpdateLayout();
        Assert.Empty(EndlessAnimationsOnClock());
        Assert.False(bar.IsIndeterminate);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        MobileFixtures.Land(window);
        Assert.Empty(EndlessAnimationsOnClock());
        Assert.All(view.GetVisualDescendants().OfType<ProgressBar>(), b => Assert.False(b.IsIndeterminate));
        window.Close();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clockOffset")]
    private static extern ref long ClockOffset(Dispatcher dispatcher);

    /// <summary>The phone's idle spin (dispatcher clock 30 ms behind its Android loop, so every
    /// timer due inside the gap re-armed "at once" forever) is fixed upstream since Avalonia
    /// 12.1.5 (#22496): when the platform loop replaces the bare dispatcher, the dispatcher keeps
    /// the clock difference in <c>_clockOffset</c> and hands the loop due times on the loop's
    /// clock. Our own DispatcherClock patch was removed with that bump. Fails if an Avalonia
    /// upgrade drops the offset again, so the phone would quietly be back to spinning.</summary>
    [AvaloniaFact]
    public void Dispatcher_keeps_due_times_on_its_loops_clock()
    {
        var offset = ClockOffset(Dispatcher.UIThread);
        Assert.InRange(offset, long.MinValue, long.MaxValue);
    }
}
