using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-06: in the main window (1920×1030, not filling a 2560×1440 screen) the album page's track
/// menu opened downward from a right-click and ran past the window's bottom edge: Metadata,
/// Lyrics, … Remove from Library were cut off. Glass was off, so the menu was a native popup,
/// which Avalonia keeps on its monitor, not in its window. Every menu, submenu and flyout of
/// the main window now opens in its overlay layer and stays inside the window: it slides up
/// when it doesn't fit below, and a menu taller than the window scrolls (the keyboard still
/// reaches every row). The headless screen (1920×1280) is taller than the window, like the
/// owner's, so a native popup could leave the window here too.
/// </summary>
public class MenuFitsWindowTests
{
    private const double W = 1920, H = 1030;

    private static Window MainLike(Control content)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        UiScale.Install();
        MenuOpenAnimation.ForgetLastPress();
        var win = new Window { Width = W, Height = H, Content = content };
        GlassMenus.SetHost(win, true);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    private static void Settle()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(8);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where <paramref name="v"/> is drawn, in the window's client coordinates (works
    /// for a native popup, which is another top level, and an overlay one alike).</summary>
    private static Rect InWindow(Window win, Visual v)
    {
        var origin = win.PointToScreen(default);
        var tl = v.PointToScreen(default);
        var br = v.PointToScreen(new Point(v.Bounds.Width, v.Bounds.Height));
        var s = win.RenderScaling;
        return new Rect((tl.X - origin.X) / s, (tl.Y - origin.Y) / s, (br.X - tl.X) / s, (br.Y - tl.Y) / s);
    }

    private static void AssertInside(Window win, Visual v, string what)
    {
        var r = InWindow(win, v);
        const double eps = 0.5;
        Assert.True(r.X >= -eps && r.Y >= -eps && r.Right <= W + eps && r.Bottom <= H + eps,
            $"{what} {r} is not inside the {W}x{H} window");
    }

    /// <summary>The card is inside the window, and so is every row as far as the card's scroll
    /// viewport shows it (a row scrolled out of view is clipped by the card).</summary>
    private static void AssertRowsInsideOrScrolledAway(Window win, Control card, IEnumerable<Control> rows)
    {
        AssertInside(win, card, "card");
        var viewport = card.GetVisualDescendants().OfType<ScrollViewer>().First();
        var view = InWindow(win, viewport);
        foreach (var row in rows.Where(r => r.IsVisible))
        {
            var r = InWindow(win, row).Intersect(view);
            if (r.Height <= 0.5) continue; // scrolled away
            Assert.True(r.Y >= -0.5 && r.Bottom <= H + 0.5 && r.X >= -0.5 && r.Right <= W + 0.5,
                $"row {(row as MenuItem)?.Header} {r} is not inside the window");
        }
    }

    private static (ContextMenu Menu, Border Owner) BigMenu(int rows, MenuItem? last = null)
    {
        var menu = new ContextMenu();
        for (var i = 0; i < rows; i++) menu.Items.Add(new MenuItem { Header = "Row " + i });
        if (last != null) menu.Items.Add(last);
        var owner = new Border { Background = Brushes.Gray, ContextMenu = menu };
        return (menu, owner);
    }

    private static void RightClick(Window win, Point at)
    {
        win.MouseDown(at, MouseButton.Right);
        win.MouseUp(at, MouseButton.Right);
        Settle();
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    [InlineData(false, 2.0)]
    [InlineData(true, 2.0)]
    public void TallMenu_OpenedLow_StaysInsideTheWindow(bool glass, double scale)
    {
        // ~650 px of rows, like the album page's track menu; opened from the first track row.
        var (menu, owner) = BigMenu(20);
        UiScale.SetFactor(scale);
        var win = MainLike(owner);
        try
        {
            if (glass) AppGlass.Set(true, Colors.Black, Colors.Black);
            foreach (var y in new[] { 531.0, 650.0, 1000.0 })
            {
                RightClick(win, new Point(500, y));
                Assert.True(menu.IsOpen);
                // Drawn in the window itself (its overlay layer), not a native popup window: a
                // native popup is only kept on its monitor (the headless platform keeps it in
                // the window, so only this tells the two apart here).
                Assert.Same(win, TopLevel.GetTopLevel(menu));
                AssertRowsInsideOrScrolledAway(win, menu, menu.Items.OfType<Control>());
                MenuOpenAnimation.CloseNow(menu);
                Settle();
            }
        }
        finally
        {
            AppGlass.Clear();
            UiScale.SetFactor(1.0);
            MenuOpenAnimation.CloseNow(menu);
            win.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenuTallerThanTheWindow_Scrolls_AndTheKeyboardReachesEveryRow(bool glass)
    {
        var (menu, owner) = BigMenu(45);
        var win = MainLike(owner);
        try
        {
            if (glass) AppGlass.Set(true, Colors.Black, Colors.Black);
            RightClick(win, new Point(500, 900));
            Assert.True(menu.IsOpen);
            Assert.Same(win, TopLevel.GetTopLevel(menu));
            AssertRowsInsideOrScrolledAway(win, menu, menu.Items.OfType<Control>());
            var rows = menu.Items.OfType<MenuItem>().ToList();
            Assert.True(InWindow(win, rows[^1]).Y >= H, "the last row should start scrolled out of view");

            // Arrow down through every row: each focused row is brought into view, inside the window.
            for (var i = 0; i < rows.Count; i++)
            {
                var focused = TopLevel.GetTopLevel(menu)!.FocusManager!.GetFocusedElement() as Control ?? menu;
                focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = focused });
                Settle();
            }
            var last = rows[^1];
            Assert.True(last.IsFocused || last.IsSelected, "Down never reached the last row");
            AssertInside(win, last, "last row");
            AssertRowsInsideOrScrolledAway(win, menu, rows);
        }
        finally
        {
            AppGlass.Clear();
            MenuOpenAnimation.CloseNow(menu);
            win.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Submenu_OfAMenuOpenedLow_StaysInsideTheWindow(bool glass)
    {
        var tools = new MenuItem { Header = "Tools" };
        for (var i = 0; i < 12; i++) tools.Items.Add(new MenuItem { Header = "Tool " + i });
        var (menu, owner) = BigMenu(14, tools);
        var win = MainLike(owner);
        try
        {
            if (glass) AppGlass.Set(true, Colors.Black, Colors.Black);
            RightClick(win, new Point(1800, 600));
            AssertRowsInsideOrScrolledAway(win, menu, menu.Items.OfType<Control>());
            tools.IsSubMenuOpen = true;
            Settle();
            var card = Assert.IsType<Border>(tools.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup").Child);
            Assert.Same(win, TopLevel.GetTopLevel(card));
            AssertRowsInsideOrScrolledAway(win, card, tools.Items.OfType<Control>());
        }
        finally
        {
            AppGlass.Clear();
            MenuOpenAnimation.CloseNow(menu);
            win.Close();
        }
    }

    /// <summary>Menus that turn the positioner's slide off (the Songs filter/sort menus and the
    /// album "…" menu: None; the island's menus: FlipY) still stay inside the window.</summary>
    [AvaloniaTheory]
    [InlineData(false, PopupPositionerConstraintAdjustment.None, PlacementMode.BottomEdgeAlignedRight, false)]
    [InlineData(true, PopupPositionerConstraintAdjustment.None, PlacementMode.BottomEdgeAlignedRight, false)]
    [InlineData(false, PopupPositionerConstraintAdjustment.FlipY, PlacementMode.Top, true)]
    [InlineData(true, PopupPositionerConstraintAdjustment.FlipY, PlacementMode.Top, true)]
    public void Flyout_WithoutSlide_StaysInsideTheWindow(bool glass, PopupPositionerConstraintAdjustment adjust, PlacementMode placement, bool buttonHigh)
    {
        var flyout = new MenuFlyout { Placement = placement, PlacementConstraintAdjustment = adjust };
        for (var i = 0; i < 18; i++) flyout.Items.Add(new MenuItem { Header = "Choice " + i });
        var button = new Button
        {
            Content = "...", Width = 40, Height = 30, Flyout = flyout,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = buttonHigh ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = new Thickness(0, 300, 4, 20),
        };
        var win = MainLike(new Panel { Children = { button } });
        try
        {
            if (glass) AppGlass.Set(true, Colors.Black, Colors.Black);
            flyout.ShowAt(button);
            Settle();
            var presenter = win.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()
                ?? Assert.IsType<MenuFlyoutPresenter>(((Popup)typeof(PopupFlyoutBase).GetProperty("Popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flyout)!).Child);
            AssertRowsInsideOrScrolledAway(win, presenter, presenter.GetVisualDescendants().OfType<MenuItem>());
        }
        finally
        {
            AppGlass.Clear();
            flyout.Hide();
            Settle();
            win.Close();
        }
    }
}
