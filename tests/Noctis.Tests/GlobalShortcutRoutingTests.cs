using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Spacebar = play/pause is a global shortcut, but Avalonia's Button handles Space
/// itself (it is the keyboard "click") and marks the event handled. A window-level
/// KeyDown handler registered the ordinary way is a bubbling handler, so it never
/// sees the event once any button holds focus — click a lyric line to seek, press
/// Space, and the line re-seeks instead of pausing. Same for the fullscreen toggle.
///
/// These tests pin the routing that makes the shortcut win: tunnel, exactly like the
/// queue-popup PointerPressed handler in MainWindow already does.
/// </summary>
public class GlobalShortcutRoutingTests
{
    private static (Window Window, Button Button) BuildFocusedButtonWindow()
    {
        var button = new Button { Content = "Seek here" };
        var window = new Window { Width = 400, Height = 300, Content = button };
        return (window, button);
    }

    [AvaloniaFact]
    public void BubblingHandler_LosesSpaceToFocusedButton()
    {
        var (window, button) = BuildFocusedButtonWindow();
        var shortcutFired = false;
        var buttonClicked = false;

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { shortcutFired = true; e.Handled = true; }
        };
        button.Click += (_, _) => buttonClicked = true;

        window.Show();
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        // Documents the bug: the focused button eats Space, the shortcut never runs.
        Assert.False(shortcutFired);
        Assert.True(buttonClicked);
    }

    [AvaloniaFact]
    public void TunnelHandler_WinsSpaceOverFocusedButton()
    {
        var (window, button) = BuildFocusedButtonWindow();
        var shortcutFired = false;
        var buttonClicked = false;

        var consumedDown = false;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Space) { shortcutFired = true; consumedDown = true; e.Handled = true; }
            },
            RoutingStrategies.Tunnel);
        // The press alone is not enough: Button clicks on the *release*, so the
        // matching KeyUp has to be swallowed too or the focused button still fires.
        window.AddHandler(
            InputElement.KeyUpEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Space && consumedDown) { consumedDown = false; e.Handled = true; }
            },
            RoutingStrategies.Tunnel);
        button.Click += (_, _) => buttonClicked = true;

        window.Show();
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.True(shortcutFired);
        Assert.False(buttonClicked);
    }

    /// <summary>
    /// The exclusion MainWindow relies on: a tunneling Space handler must leave the key
    /// alone while a TextBox is the source, or the search box can no longer type spaces.
    /// </summary>
    [AvaloniaFact]
    public void TunnelHandler_LeavesSpaceAloneWhileTypingInTextBox()
    {
        var box = new TextBox();
        var window = new Window { Width = 400, Height = 300, Content = box };
        var shortcutFired = false;

        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key != Key.Space) return;
                if (e.Source is TextBox) return;
                shortcutFired = true;
                e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        window.Show();
        box.Focus();
        window.KeyTextInput(" ");

        Assert.False(shortcutFired);
        Assert.Equal(" ", box.Text);
    }

    /// <summary>
    /// The same tunnel pair MainWindow uses, but resolved against ShortcutService: once
    /// Play/Pause is rebound to P, P wins over a focused button, Space does nothing, and
    /// P typed into a TextBox stays a letter.
    /// </summary>
    [AvaloniaFact]
    public void ReboundPlayPause_FiresOnNewKey_NotOnOld_NotInTextBox()
    {
        var shortcuts = new ShortcutService(isMac: false);
        shortcuts.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));

        var button = new Button { Content = "Seek here" };
        var box = new TextBox();
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { button, box } } };
        var fired = 0;
        var buttonClicked = false;
        ShortcutAction? consumed = null;

        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (shortcuts.TryMatch(e) is not { } action) return;
                if (e.KeyModifiers == KeyModifiers.None && e.Source is TextBox) return;
                if (action == ShortcutAction.PlayPause) fired++;
                consumed = action;
                e.Handled = true;
            },
            RoutingStrategies.Tunnel);
        window.AddHandler(
            InputElement.KeyUpEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (consumed is null) return;
                consumed = null;
                e.Handled = true;
            },
            RoutingStrategies.Tunnel);
        button.Click += (_, _) => buttonClicked = true;

        window.Show();
        button.Focus();

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.None);
        Assert.Equal(1, fired);
        Assert.False(buttonClicked);

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(1, fired);           // Space is no longer bound…
        Assert.True(buttonClicked);       // …so the focused button gets its click back.

        box.Focus();
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        window.KeyTextInput("p");
        window.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.None);
        Assert.Equal(1, fired);
        Assert.Equal("p", box.Text);
    }
}
