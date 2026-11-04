using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #169: with Liquid Glass on, every right-click menu, its submenus and every tooltip
/// in the main window frost like the island's menus (#104); with glass off they look and open
/// exactly as before.
/// </summary>
public class GlassMenus169Tests
{
    private static Window HostWindow(Control content, bool host = true)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var win = new Window { Width = 1200, Height = 900, Content = content };
        if (host) GlassMenus.SetHost(win, true);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    private static Popup PopupOf(Control card) => Assert.IsType<Popup>(card.Parent);

    private static T Single<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Single();

    private static ItemsPresenter Rows(Visual card)
        => card.GetVisualDescendants().OfType<ItemsPresenter>().First(p => p.Name == "PART_ItemsPresenter");

    private static void PumpUntil(System.Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < 2000)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(8);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    private static (ContextMenu Menu, MenuItem Sub, Border Owner) Menu()
    {
        var sub = new MenuItem { Header = "Tools", Items = { new MenuItem { Header = "Convert" }, new MenuItem { Header = "ReplayGain" } } };
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Play" }, sub } };
        var owner = new Border { Width = 50, Height = 50, Background = Brushes.Gray, ContextMenu = menu };
        return (menu, sub, owner);
    }

    private static Border OpenSubmenu(MenuItem sub)
    {
        sub.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<Border>(SubmenuPopup(sub).Child);
    }

    private static Popup SubmenuPopup(MenuItem sub) =>
        sub.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");

    [AvaloniaFact]
    public void ContextMenu_GlassOn_FrostsInTheOverlay_GlassOff_IsThePlainCardAgain()
    {
        var (menu, _, owner) = Menu();
        var win = HostWindow(owner);
        win.Resources["GlassMenuTintOpacity"] = 0.55;
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.True(PopupOf(menu).ShouldUseOverlayLayer);
            Assert.Contains(GlassMenus.GlassClass, menu.Classes);
            Assert.True(MenuOpenAnimation.GetUseFade(menu));
            var glass = Single<GlassPanel>(menu);
            Assert.Equal(new Thickness(6), glass.Padding);
            Assert.Equal(new CornerRadius(18), glass.CornerRadius);
            Assert.Equal(GlassMenus.BlurRadius, glass.BlurRadius);
            // Tinted with the menu's own theme colour at the window's Glass Opacity.
            Assert.Same(menu.Background, glass.Background);
            Assert.Equal(0.55, glass.GlassTintOpacity);
            Assert.Equal(new Thickness(0), Rows(menu).Margin);

            MenuOpenAnimation.CloseNow(menu);
            PumpUntil(() => !menu.IsOpen);

            AppGlass.Clear();
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.False(PopupOf(menu).ShouldUseOverlayLayer);
            Assert.DoesNotContain(GlassMenus.GlassClass, menu.Classes);
            Assert.False(MenuOpenAnimation.GetUseFade(menu));
            Assert.Empty(menu.GetVisualDescendants().OfType<GlassPanel>());
            Assert.Equal(new Thickness(6), menu.GetVisualChildren().OfType<Border>().Single().Padding);
            menu.Close();
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }

    /// <summary>A menu closed as a plain card (its close motion fades Opacity to 0) and reopened
    /// with glass on (whose motion fades Fade instead) must not stay at Opacity 0, and back.</summary>
    [AvaloniaFact]
    public void ContextMenu_GlassSwitchedBetweenOpens_ShowsFullyEitherWay()
    {
        var (menu, _, owner) = Menu();
        var win = HostWindow(owner);
        try
        {
            foreach (var glassOn in new[] { false, true, false })
            {
                if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
                else AppGlass.Clear();
                menu.Open(owner);
                PumpUntil(() => menu.Opacity == 1 && MenuOpenAnimation.GetFade(menu) == 1);
                menu.Close();
                PumpUntil(() => !menu.IsOpen);
            }
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void ContextMenu_GlassOff_NeverTouchesThePopup()
    {
        var (menu, _, owner) = Menu();
        var win = HostWindow(owner);
        try
        {
            AppGlass.Clear();
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.False(PopupOf(menu).IsSet(Popup.ShouldUseOverlayLayerProperty));
            Assert.DoesNotContain(GlassMenus.GlassClass, menu.Classes);
            menu.Close();
        }
        finally { win.Close(); }
    }

    /// <summary>Whole app: a menu in a window that is no overlay host (mini player, dialogs,
    /// plugin windows) frosts too, in its own native popup (no overlay layer: it may reach past
    /// a small window), over the GlassMenuBackdrop.</summary>
    [AvaloniaFact]
    public void ContextMenu_InAWindowThatIsNoGlassHost_FrostsInItsOwnPopup()
    {
        var (menu, sub, owner) = Menu();
        var win = HostWindow(owner, host: false);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.False(PopupOf(menu).IsSet(Popup.ShouldUseOverlayLayerProperty));
            Assert.Contains(GlassMenus.GlassClass, menu.Classes);
            Assert.DoesNotContain(GlassMenus.OverlayClass, menu.Classes);
            var backdrop = Single<GlassMenuBackdrop>(menu);
            Assert.IsType<GlassPanel>(backdrop.Child);
            Assert.Same(menu.Background, backdrop.Background);
            // Its submenu stays in a popup of its own as well, and frosts.
            var card = OpenSubmenu(sub);
            Assert.False(SubmenuPopup(sub).ShouldUseOverlayLayer);
            Assert.NotNull(GlassMenus.FrostOf(card));
            sub.IsSubMenuOpen = false;
            menu.Close();
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Submenu_OfAGlassMenu_FrostsToo_AndGetsTheV2CardBackWithGlassOff()
    {
        var (menu, sub, owner) = Menu();
        var win = HostWindow(owner);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            var card = OpenSubmenu(sub);
            Assert.True(SubmenuPopup(sub).ShouldUseOverlayLayer);
            var glass = Assert.IsType<GlassPanel>(GlassMenus.FrostOf(card));
            Assert.IsType<GlassMenuBackdrop>(card.Child);
            Assert.Equal(new Thickness(6), glass.Padding);
            Assert.Equal(new CornerRadius(16), glass.CornerRadius);
            Assert.Null(card.Background);
            Assert.Equal(new Thickness(0), card.Padding);
            Assert.Equal(new Thickness(0), card.BorderThickness);
            Assert.True(MenuOpenAnimation.GetUseFade(card));
            Assert.True(glass.TryFindResource("AppSidebarBackground", glass.ActualThemeVariant, out var tint));
            Assert.Same(tint, glass.Background);
            Assert.Equal(2, sub.GetLogicalChildren().OfType<MenuItem>().Count(i => i.IsVisible));
            sub.IsSubMenuOpen = false;
            MenuOpenAnimation.CloseNow(menu);
            PumpUntil(() => !menu.IsOpen);

            AppGlass.Clear();
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            card = OpenSubmenu(sub);
            Assert.False(SubmenuPopup(sub).ShouldUseOverlayLayer);
            Assert.IsNotType<GlassPanel>(card.Child);
            Assert.Same(tint, card.Background);
            Assert.Equal(new Thickness(6), card.Padding);
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.Equal(new CornerRadius(16), card.CornerRadius);
            Assert.False(MenuOpenAnimation.GetUseFade(card));
            Assert.Equal(1, Assert.IsType<ScrollViewer>(card.Child).Opacity);
            sub.IsSubMenuOpen = false;
            menu.Close();
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void ToolTip_GlassOn_FrostsInTheOverlay_GlassOff_IsTheStockTip()
    {
        var tip = new ToolTip { Content = "Shuffle" };
        var button = new Button { Content = "x" };
        ToolTip.SetTip(button, tip);
        var win = HostWindow(button);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            ToolTip.SetIsOpen(button, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ToolTip.GetShouldUseOverlayLayer(button));
            Assert.True(PopupOf(tip).ShouldUseOverlayLayer);
            Assert.Contains(GlassMenus.GlassClass, tip.Classes);
            var glass = Single<GlassPanel>(tip);
            Assert.Same(tip.Background, glass.Background);
            Assert.Equal(1, tip.Opacity); // the card never fades its Opacity over the blur
            ToolTip.SetIsOpen(button, false);
            Dispatcher.UIThread.RunJobs();

            AppGlass.Clear();
            ToolTip.SetIsOpen(button, true);
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsSet(ToolTip.ShouldUseOverlayLayerProperty));
            Assert.False(PopupOf(tip).ShouldUseOverlayLayer);
            Assert.DoesNotContain(GlassMenus.GlassClass, tip.Classes);
            Assert.Empty(tip.GetVisualDescendants().OfType<GlassPanel>());
            ToolTip.SetIsOpen(button, false);
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }
}
