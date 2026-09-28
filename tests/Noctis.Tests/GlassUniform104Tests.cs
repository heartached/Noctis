using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #104: with Liquid Glass on, the queue drawer and the island's menus frost like the
/// island itself, tinted at the one Glass Opacity setting; with glass off both behave exactly
/// as before.
/// </summary>
public class GlassUniform104Tests
{
    // ── Queue drawer ────────────────────────────────────────────────────────────

    private static (Border Panel, GlassPanel Glass, Window Window) Drawer()
    {
        var glass = new GlassPanel { IsVisible = false, Fade = 0 };
        var panel = new Border { IsVisible = false, Opacity = 0 };
        var window = new Window { Content = new Panel { Children = { glass, panel } }, Width = 400, Height = 300 };
        window.Show();
        return (panel, glass, window);
    }

    [AvaloniaFact]
    public void Queue_OpenWithGlass_ShowsTheFrostAndClearsTheDrawerFill()
    {
        var (panel, glass, window) = Drawer();
        try
        {
            QueueDrawerGlass.Animate(panel, glass, open: true, glassActive: true, stillClosed: () => false);
            Dispatcher.UIThread.RunJobs();

            Assert.True(panel.IsVisible);
            Assert.True(glass.IsVisible);
            Assert.Contains(QueueDrawerGlass.GlassClass, panel.Classes);
            Assert.Equal(1, panel.Opacity);
            Assert.Equal(1, glass.Fade);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Queue_OpenWithoutGlass_LeavesTheFrostHiddenAndTheDrawerAsBefore()
    {
        var (panel, glass, window) = Drawer();
        try
        {
            QueueDrawerGlass.Animate(panel, glass, open: true, glassActive: false, stillClosed: () => false);
            Dispatcher.UIThread.RunJobs();

            Assert.True(panel.IsVisible);
            Assert.Equal(1, panel.Opacity);
            Assert.False(glass.IsVisible);
            Assert.DoesNotContain(QueueDrawerGlass.GlassClass, panel.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Queue_Close_FadesTheFrostOnItsOwnFadeNotOpacity()
    {
        var (panel, glass, window) = Drawer();
        try
        {
            QueueDrawerGlass.Animate(panel, glass, open: true, glassActive: true, stillClosed: () => false);
            Dispatcher.UIThread.RunJobs();
            QueueDrawerGlass.Animate(panel, glass, open: false, glassActive: true, stillClosed: () => true);

            Assert.Equal(0, panel.Opacity);
            Assert.Equal(0, glass.Fade);
            // The frost is never faded through Opacity (the GPU blur ignores it).
            Assert.Equal(1, glass.Opacity);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Queue_GlassToggledWhileOpen_FollowsLive()
    {
        var (panel, glass, window) = Drawer();
        try
        {
            QueueDrawerGlass.Animate(panel, glass, open: true, glassActive: false, stillClosed: () => false);
            Dispatcher.UIThread.RunJobs();

            QueueDrawerGlass.Apply(panel, glass, glassActive: true);
            Assert.True(glass.IsVisible);
            Assert.Equal(1, glass.Fade);
            Assert.Contains(QueueDrawerGlass.GlassClass, panel.Classes);

            QueueDrawerGlass.Apply(panel, glass, glassActive: false);
            Assert.False(glass.IsVisible);
            Assert.DoesNotContain(QueueDrawerGlass.GlassClass, panel.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Queue_GlassTurnedOnWhileClosed_KeepsTheFrostHidden()
    {
        var (panel, glass, window) = Drawer();
        try
        {
            QueueDrawerGlass.Apply(panel, glass, glassActive: true);
            Assert.False(glass.IsVisible);
            Assert.Equal(0, glass.Fade);
        }
        finally { window.Close(); }
    }

    // ── Island menus ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Menu_OpensInTheOverlayWithTheGlassClassOnlyWhileGlassIsOn()
    {
        var button = new Button { Content = "…" };
        var window = new Window { Content = button, Width = 400, Height = 300 };
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "A" } } };
        GlassMenuFlyout.SetEnable(flyout, true);
        window.Show();
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var presenter = Assert.IsType<MenuFlyoutPresenter>(flyout.Popup.Child);
            Assert.True(flyout.Popup.ShouldUseOverlayLayer);
            Assert.Contains(GlassMenuFlyout.GlassClass, presenter.Classes);
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();

            AppGlass.Clear();
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.Popup.ShouldUseOverlayLayer);
            Assert.DoesNotContain(GlassMenuFlyout.GlassClass, presenter.Classes);
            flyout.Hide();
        }
        finally
        {
            AppGlass.Clear();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Menu_WithoutEnable_IsNeverTouched()
    {
        var button = new Button { Content = "…" };
        var window = new Window { Content = button, Width = 400, Height = 300 };
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "A" } } };
        window.Show();
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.Popup.ShouldUseOverlayLayer);
            Assert.DoesNotContain(GlassMenuFlyout.GlassClass, ((Control)flyout.Popup.Child!).Classes);
            flyout.Hide();
        }
        finally
        {
            AppGlass.Clear();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MenuAnimation_GlassMenuFadesItsFadeAndLeavesOpacityAlone()
    {
        // A plain control: the app-wide MenuFlyoutPresenter style already enables the motion
        // (and runs it on attach), which would hide the state under test.
        var presenter = new Border();
        MenuOpenAnimation.SetUseFade(presenter, true);
        var window = new Window { Content = presenter, Width = 300, Height = 200 };
        window.Show();
        try
        {
            // Enabling on an attached control runs the open motion at once. Its fade is a
            // transition (the read value lags the target), so check what it animates.
            MenuOpenAnimation.SetEnable(presenter, true);
            Assert.Equal(MenuOpenAnimation.FadeProperty, FadedProperty(presenter));
            Assert.Equal(1, presenter.Opacity);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MenuAnimation_PlainMenuStillFadesOpacity()
    {
        var presenter = new Border();
        var window = new Window { Content = presenter, Width = 300, Height = 200 };
        window.Show();
        try
        {
            MenuOpenAnimation.SetEnable(presenter, true);
            Assert.Equal(Visual.OpacityProperty, FadedProperty(presenter));
            Assert.Equal(1, MenuOpenAnimation.GetFade(presenter));
        }
        finally { window.Close(); }
    }

    private static AvaloniaProperty? FadedProperty(Control control) =>
        Assert.Single(control.Transitions!.OfType<Avalonia.Animation.DoubleTransition>()).Property;

    // ── Settings copy ───────────────────────────────────────────────────────────

    [Fact]
    public void Strings_RenameTheSliderToGlassOpacity()
    {
        Assert.Equal("Glass Opacity", Noctis.Localization.Loc.English("Settings.PlayerBarOpacity"));
        Assert.Contains("queue", Noctis.Localization.Loc.English("Settings.HowSolidPlaybackBar"));
        Assert.Equal("How solid the mini player's background looks.",
            Noctis.Localization.Loc.English("Settings.HowMuchDesktopShows"));
    }
}
