using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class MenuFlyoutCloseOwnCardTests
{
    /// <summary>Closing a MenuFlyout runs its close motion on that flyout's own card. It used
    /// to run on the most recently opened card (MenuOpenAnimation.TryGetLastMenuFlyoutPresenter),
    /// so with two flyouts up, closing the first one faded out the second, still-open one.</summary>
    [AvaloniaFact]
    public void MenuFlyoutClose_AnimatesItsOwnCard_NotTheLastOpenedOne()
    {
        var a = new Button { Content = "a" };
        var b = new Button { Content = "b" };
        AddToPlaylistDialogTests.EnsureAppStyles();
        var win = new Window { Width = 800, Height = 600, Content = new StackPanel { Children = { a, b } } };
        win.Show();
        var first = new MenuFlyout { Items = { new MenuItem { Header = "A" } } };
        var second = new MenuFlyout { Items = { new MenuItem { Header = "B" } } };
        MenuOpenAnimation.SetEnableFlyoutClose(first, true);
        MenuOpenAnimation.SetEnableFlyoutClose(second, true);
        try
        {
            first.ShowAt(a);
            Dispatcher.UIThread.RunJobs();
            second.ShowAt(b);
            Dispatcher.UIThread.RunJobs();
            var firstCard = Assert.IsType<MenuFlyoutPresenter>(first.Popup.Child);
            var secondCard = Assert.IsType<MenuFlyoutPresenter>(second.Popup.Child);

            first.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(firstCard.IsHitTestVisible); // its close motion started
            Assert.True(secondCard.IsHitTestVisible); // the other card is left alone
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (first.IsOpen && sw.ElapsedMilliseconds < 2000)
            {
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                System.Threading.Thread.Sleep(8);
            }
            Assert.False(first.IsOpen);
            Assert.True(second.IsOpen);
        }
        finally
        {
            first.Hide();
            second.Hide();
            win.Close();
        }
    }
}
