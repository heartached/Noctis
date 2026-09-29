using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The one-menu-at-a-time coordinator held the last opened menu in a static forever,
/// which kept that menu's owner (a tile on a page the user had long left) alive.
/// </summary>
public class ContextMenuCoordinatorTests
{
    [AvaloniaFact]
    public void ClosedMenu_IsReleased()
    {
        var owner = new Border { Width = 50, Height = 50 };
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Play" } } };
        owner.ContextMenu = menu;
        var win = new Window { Width = 200, Height = 200, Content = owner };
        win.Show();
        try
        {
            ContextMenuCoordinator.NotifyOpening(menu);
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(menu, ContextMenuCoordinator.Current);

            menu.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ContextMenuCoordinator.Current);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void OpeningASecondMenu_ClosesTheFirst()
    {
        var a = new Border { Width = 50, Height = 50 };
        var b = new Border { Width = 50, Height = 50 };
        var menuA = new ContextMenu { Items = { new MenuItem { Header = "A" } } };
        var menuB = new ContextMenu { Items = { new MenuItem { Header = "B" } } };
        a.ContextMenu = menuA;
        b.ContextMenu = menuB;
        var win = new Window { Width = 200, Height = 200, Content = new StackPanel { Children = { a, b } } };
        win.Show();
        try
        {
            ContextMenuCoordinator.NotifyOpening(menuA);
            menuA.Open(a);
            Dispatcher.UIThread.RunJobs();
            Assert.True(menuA.IsOpen);

            ContextMenuCoordinator.NotifyOpening(menuB);
            menuB.Open(b);
            Dispatcher.UIThread.RunJobs();
            Assert.False(menuA.IsOpen);
            Assert.Same(menuB, ContextMenuCoordinator.Current);

            menuB.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ContextMenuCoordinator.Current);
        }
        finally { win.Close(); }
    }
}
