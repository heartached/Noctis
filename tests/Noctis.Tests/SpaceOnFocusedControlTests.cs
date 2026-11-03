using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-30: Space presses a button, switch or drop-down reached with the keyboard (its focus
/// ring showing); after a mouse click it stays play/pause (MainWindow.OnGlobalShortcutKeyDown).
/// </summary>
public class SpaceOnFocusedControlTests
{
    private static (Window Window, Button Button, LottieToggle Toggle, TextBlock Text) Mount()
    {
        var button = new Button { Content = "Next" };
        var toggle = new LottieToggle();
        var text = new TextBlock { Text = "label" };
        var window = new Window { Content = new StackPanel { Children = { button, toggle, text } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, button, toggle, text);
    }

    [AvaloniaFact]
    public void KeyboardFocus_PressesTheControl_MouseFocusKeepsPlayPause()
    {
        var (window, button, toggle, text) = Mount();
        try
        {
            Assert.True(button.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            Assert.True(MainWindow.IsKeyboardFocusedControl(button));

            Assert.True(toggle.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            Assert.True(MainWindow.IsKeyboardFocusedControl(toggle));

            // Clicked: no focus ring, so Space stays play/pause.
            Assert.True(button.Focus(NavigationMethod.Pointer));
            Dispatcher.UIThread.RunJobs();
            Assert.False(MainWindow.IsKeyboardFocusedControl(button));

            // Not a control Space presses.
            Assert.False(MainWindow.IsKeyboardFocusedControl(text));
            Assert.False(MainWindow.IsKeyboardFocusedControl(null));
        }
        finally { window.Close(); }
    }
}
