using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A rail tile's arrival fade (Appear) cut short still lands fully shown: the page covered by a
/// sheet mid-fade (hidden, still on the tree), or the tile taken off the tree mid-fade (a page
/// parked, a row recycled) and put back. A tile left part-faded would read as a dim label.
/// </summary>
public class MobileAppearTests
{
    private static void Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
    }

    /// <summary>Frames until <paramref name="landed"/> holds; false if it never does.</summary>
    private static bool Until(Func<bool> landed)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!landed() && Environment.TickCount64 < deadline) Frames(1);
        return landed();
    }

    [AvaloniaFact]
    public void AnArrivalCutShort_ByASheetOrADetach_StillLandsFullyShown()
    {
        var covered = new Border { Width = 40, Height = 40 };
        var parked = new Border { Width = 40, Height = 40 };
        Appear.SetIsEnabled(covered, true);
        Appear.SetIsEnabled(parked, true);
        var page = new StackPanel { Children = { covered } };
        var other = new StackPanel { Children = { parked } };
        var window = new Window { Width = 200, Height = 200, Content = new StackPanel { Children = { page, other } } };
        window.Show();
        Assert.True(Until(() => covered.Opacity is > 0 and < 1 && parked.Opacity is > 0 and < 1), "the fades never started");

        page.IsVisible = false;          // a sheet over the page
        other.Children.Remove(parked);   // the page parked
        Assert.True(Until(() => covered.Opacity == 1), $"hidden mid-fade, stuck at {covered.Opacity:0.00}");
        Assert.True(Until(() => parked.Opacity == 1), $"detached mid-fade, stuck at {parked.Opacity:0.00}");

        page.IsVisible = true;
        other.Children.Add(parked);      // back: it arrives again, and lands
        Assert.True(Until(() => covered.Opacity == 1 && parked.Opacity == 1), $"back on the tree, stuck at {parked.Opacity:0.00}");
        window.Close();
    }
}
