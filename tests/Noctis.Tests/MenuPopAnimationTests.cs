using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The "pop" open motion (MenuOpenAnimation.Pop, Albums-page album menu trial 10-09): the card
/// grows out of the click point and its rows settle in, then the rows go back to their styles.
/// </summary>
public class MenuPopAnimationTests
{
    private static (Window window, Border owner, ContextMenu menu) OpenPopMenu(System.Func<Border, PixelPoint?> anchor)
    {
        MenuOpenAnimation.ForgetLastPress();
        var owner = new Border { Width = 400, Height = 300 };
        var window = new Window { Content = owner, Width = 400, Height = 300 };
        window.Show();
        var menu = new ContextMenu
        {
            Items = { new MenuItem { Header = "One" }, new MenuItem { Header = "Two" }, new MenuItem { Header = "Three" } },
        };
        MenuOpenAnimation.SetPop(menu, true);
        MenuOpenAnimation.SetPopAnchor(menu, anchor(owner));
        MenuOpenAnimation.SetEnable(menu, true);
        owner.ContextMenu = menu;
        menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        return (window, owner, menu);
    }

    [AvaloniaFact]
    public async Task Pop_ScalesTheCardIn_WithTheSoftEase_AndRowsSettleThenGoBackToTheirStyles()
    {
        var (window, _, menu) = OpenPopMenu(_ => null);
        try
        {
            Assert.True(menu.IsOpen);
            var scale = Assert.Single(menu.Transitions!.OfType<TransformOperationsTransition>());
            Assert.IsType<CubicBezierEase>(scale.Easing);
            Assert.True(menu.Opacity < 1); // mid-fade: the transition is animating it in
            // No anchor: grows from the top-left corner.
            Assert.Equal(RelativePoint.TopLeft, menu.RenderTransformOrigin);

            var rows = menu.GetRealizedContainers().ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.Transitions));

            // After the stagger has landed the rows hold no local motion values any more.
            // Real-time pump that yields between frames, so the hand-back DispatcherTimer fires.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000 && rows.Any(r => r.Transitions != null))
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(8);
            }
            Assert.Equal(1, menu.Opacity);
            Assert.All(rows, r =>
            {
                Assert.Null(r.Transitions);
                Assert.False(r.IsSet(Visual.OpacityProperty));
                Assert.False(r.IsSet(Visual.RenderTransformProperty));
            });
        }
        finally { menu.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public void Pop_GrowsOutOfTheAnchor_ClampedToTheCard()
    {
        // An anchor far below-right of the menu: the origin pins to the card's bottom-right corner.
        var (window, _, menu) = OpenPopMenu(o => o.PointToScreen(new Point(5000, 5000)));
        try
        {
            var origin = menu.RenderTransformOrigin;
            Assert.Equal(RelativeUnit.Absolute, origin.Unit);
            Assert.Equal(menu.Bounds.Width, origin.Point.X, 3);
            Assert.Equal(menu.Bounds.Height, origin.Point.Y, 3);
        }
        finally { menu.Close(); window.Close(); }
    }

    /// <summary>Real-time pump that yields between frames, so DispatcherTimers fire.</summary>
    private static async Task PumpUntilAsync(System.Func<bool> condition, int budgetMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs && !condition())
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    [AvaloniaFact]
    public async Task Pop_Close_FadesOutInert_ThenReallyCloses_AndRestoresTheCard()
    {
        var (window, _, menu) = OpenPopMenu(_ => null);
        try
        {
            await PumpUntilAsync(() => menu.Opacity >= 1);
            menu.Close();

            // Held open for the fade, but it can't take a click while it fades.
            Assert.True(menu.IsOpen);
            Assert.False(menu.IsHitTestVisible);
            menu.Close(); // a second close while fading is absorbed, not doubled
            Assert.True(menu.IsOpen);

            await PumpUntilAsync(() => !menu.IsOpen);
            Assert.False(menu.IsOpen); // never stranded open
            Assert.True(menu.IsHitTestVisible);
            Assert.Equal(1, menu.Opacity);
        }
        finally { MenuOpenAnimation.CloseNow(menu); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pop_ReopenDuringTheCloseFade_OpensVisible_AndTheOldCloseDoesNotShutIt()
    {
        var (window, owner, menu) = OpenPopMenu(_ => null);
        try
        {
            menu.Close(); // starts the fade (within the open's double-attach guard window, too)
            Assert.True(menu.IsOpen);

            MenuOpenAnimation.CloseNow(menu);
            Assert.False(menu.IsOpen);
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();

            // Wait past the old close's timer: the reopened menu stays open and fades in fully.
            await Task.Delay(300);
            await PumpUntilAsync(() => menu.Opacity >= 1);
            Assert.True(menu.IsOpen);
            Assert.True(menu.IsHitTestVisible);
            Assert.Equal(1, menu.Opacity);
        }
        finally { MenuOpenAnimation.CloseNow(menu); window.Close(); }
    }

    [AvaloniaFact]
    public void WindowClosing_WhileAMenuIsOpen_ClosesIt_WithoutThrowing()
    {
        // A popup closes itself as its window tears down; the close motion must not refuse that.
        var (window, _, menu) = OpenPopMenu(_ => null);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void WindowClosing_WhileAMenuIsFadingOut_ClosesIt_WithoutThrowing()
    {
        var (window, _, menu) = OpenPopMenu(_ => null);
        menu.Close(); // fading out
        Assert.True(menu.IsOpen);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void NonPopMenu_KeepsTheRiseUp()
    {
        var owner = new Border();
        var window = new Window { Content = owner, Width = 300, Height = 200 };
        window.Show();
        try
        {
            MenuOpenAnimation.SetEnable(owner, true);
            var move = Assert.Single(owner.Transitions!.OfType<TransformOperationsTransition>());
            Assert.IsNotType<CubicBezierEase>(move.Easing);
        }
        finally { window.Close(); }
    }
}
