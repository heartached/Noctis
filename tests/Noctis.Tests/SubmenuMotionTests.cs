using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: a submenu card (Carousel Style ▸, Tools ▸ …) opens and closes with the same
/// motion as the menu it belongs to (the pop): it grows out of its row while it fades in, and
/// shrinks back and fades out when it closes, without ever leaving an invisible-but-open card.
/// </summary>
public class SubmenuMotionTests
{
    private static Album Alb() =>
        new() { Id = Guid.NewGuid(), Name = "Bad", Artist = "Michael Jackson", Year = 1987, Tracks = new List<Noctis.Models.Track>() };

    private static Popup SubmenuPopup(MenuItem sub) =>
        sub.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");

    private static Border CardOf(MenuItem sub) => Assert.IsType<Border>(SubmenuPopup(sub).Child);

    private static bool Shown(Control card) => TopLevel.GetTopLevel(card) is not null;

    private static Window HostWindow(Control content)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        MenuOpenAnimation.ForgetLastPress();
        var win = new Window { Width = 1200, Height = 900, Content = content };
        GlassMenus.SetHost(win, true); // the main window: menus and submenus in its overlay layer
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    /// <summary>Real-time pump that yields between frames, so DispatcherTimers fire.</summary>
    private static async Task PumpUntil(Func<bool> done, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < budgetMs)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    private static async Task Wait(int ms)
    {
        var sw = Stopwatch.StartNew();
        await PumpUntil(() => sw.ElapsedMilliseconds >= ms, ms + 2000);
    }

    /// <summary>What the motion put on a card: each transition's type, property, duration, easing.</summary>
    private static string[] Motion(Control card) => card.Transitions!
        .Select(t => t switch
        {
            DoubleTransition d => $"fade {d.Duration.TotalMilliseconds} {d.Easing.GetType().Name}",
            TransformOperationsTransition m => $"transform {m.Duration.TotalMilliseconds} {m.Easing.GetType().Name}",
            _ => t.GetType().Name,
        })
        .ToArray();

    private static (Window Win, Border Owner, AlbumContextMenuBuilder B) OpenAlbumMenu()
    {
        var owner = new Border { Width = 60, Height = 60, Background = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var win = HostWindow(owner);
        // As LibraryAlbumsView.OpenAlbumMenu builds it.
        var b = new AlbumContextMenuBuilder();
        b.Build("Remove", win, v2: true, removeIsDanger: true);
        MenuOpenAnimation.SetPop(b.Menu, true);
        var album = Alb();
        b.BindSendToFolder(album, new RelayCommand(() => { }));
        b.BindCarouselStyle(album, null, visible: true);
        owner.ContextMenu = b.Menu;
        b.Menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        return (win, owner, b);
    }

    [AvaloniaFact]
    public async Task CarouselStyle_Submenu_PopsOpen_ExactlyLikeTools_AndLikeTheMenu()
    {
        var (win, _, b) = OpenAlbumMenu();
        try
        {
            var menuMotion = Motion(b.Menu);
            var seen = new List<string[]>();
            foreach (var sub in new[] { b.Tools, b.CarouselStyle })
            {
                sub.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                var card = CardOf(sub);
                Assert.True(MenuOpenAnimation.GetSubmenu(card));
                Assert.True(card.Opacity < 1); // mid-fade
                seen.Add(Motion(card));
                // Grows out of its row: the origin sits on the card's edge beside the row.
                var origin = card.RenderTransformOrigin;
                Assert.Equal(RelativeUnit.Absolute, origin.Unit);
                Assert.True(origin.Point.X <= 0.5 || origin.Point.X >= card.Bounds.Width - 0.5, $"origin {origin}");
                // Its rows settle in one after another, as the menu's do.
                Assert.All(sub.GetRealizedContainers().Where(r => r.IsVisible), r => Assert.NotNull(r.Transitions));

                sub.IsSubMenuOpen = false;
                await PumpUntil(() => !Shown(card));
            }
            Assert.Equal(seen[0], seen[1]);
            Assert.Equal(menuMotion, seen[1]);
        }
        finally { MenuOpenAnimation.CloseNow(b.Menu); win.Close(); }
    }

    [AvaloniaFact]
    public async Task Submenu_Close_PopsOut_Inert_ThenReallyCloses_AndRestoresTheCard()
    {
        var (win, _, b) = OpenAlbumMenu();
        try
        {
            var sub = b.CarouselStyle;
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            await PumpUntil(() => card.Opacity >= 1);

            sub.IsSubMenuOpen = false;
            // Still on screen for the fade, but closed to the menu and unable to take a click.
            Assert.False(SubmenuPopup(sub).IsOpen);
            Assert.True(Shown(card));
            Assert.False(card.IsHitTestVisible);
            Assert.Contains(card.Transitions!, t => t is TransformOperationsTransition { Easing: CubicBezierEase });
            Assert.Equal(new[] { "fade 150 CubicBezierEase", "transform 150 CubicBezierEase" }, Motion(card));

            await PumpUntil(() => !Shown(card)); // never stranded on screen
            Assert.True(card.IsHitTestVisible);
            Assert.Equal(1, card.Opacity);
            Assert.True(b.Menu.IsOpen); // the menu itself stays open

            // And it opens again normally.
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Shown(card));
            await PumpUntil(() => card.Opacity >= 1);
            Assert.True(card.IsHitTestVisible);
        }
        finally { MenuOpenAnimation.CloseNow(b.Menu); win.Close(); }
    }

    [AvaloniaFact]
    public async Task Submenu_ReopenedWhileFadingOut_OpensVisible_AndTheOldCloseDoesNotShutIt()
    {
        var (win, _, b) = OpenAlbumMenu();
        try
        {
            var sub = b.CarouselStyle;
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            await PumpUntil(() => card.Opacity >= 1);

            sub.IsSubMenuOpen = false; // fading out
            Assert.False(card.IsHitTestVisible);
            sub.IsSubMenuOpen = true;  // pointer back on the row
            Dispatcher.UIThread.RunJobs();
            Assert.True(card.IsHitTestVisible);

            await Wait(300); // past the old close's timer
            Assert.True(SubmenuPopup(sub).IsOpen);
            Assert.True(Shown(card));
            await PumpUntil(() => card.Opacity >= 1);
            Assert.True(card.IsHitTestVisible);
        }
        finally { MenuOpenAnimation.CloseNow(b.Menu); win.Close(); }
    }

    [AvaloniaFact]
    public async Task PickingASubmenuRow_ClosesTheSubmenuAndTheMenu_NothingStranded()
    {
        var owner = new Border { Width = 60, Height = 60, Background = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var win = HostWindow(owner);
        var picked = 0;
        var pick = new MenuItem { Header = "Vinyl", Command = new RelayCommand(() => picked++) };
        var sub = new MenuItem { Header = "Carousel Style", Items = { new MenuItem { Header = "Default" }, pick } };
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Play" }, sub } };
        owner.ContextMenu = menu;
        try
        {
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            await PumpUntil(() => card.Opacity >= 1 && menu.Opacity >= 1);

            win.UpdateLayout();
            var centre = pick.TranslatePoint(new Point(pick.Bounds.Width / 2, pick.Bounds.Height / 2), win)!.Value;
            win.MouseDown(centre, MouseButton.Left);
            win.MouseUp(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, picked);
            // Both cards stop taking input the moment the close starts.
            Assert.True(!Shown(card) || !card.IsHitTestVisible);
            Assert.True(!menu.IsOpen || !menu.IsHitTestVisible);

            await PumpUntil(() => !menu.IsOpen && !Shown(card));
            Assert.False(sub.IsSubMenuOpen);
            Assert.True(card.IsHitTestVisible);
            Assert.True(menu.IsHitTestVisible);

            // The menu opens again with its submenu, both fully visible.
            menu.Open(owner);
            Dispatcher.UIThread.RunJobs();
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            await PumpUntil(() => Shown(card) && card.Opacity >= 1 && menu.Opacity >= 1);
        }
        finally { MenuOpenAnimation.CloseNow(menu); win.Close(); }
    }

    [AvaloniaFact]
    public async Task Submenu_KeyboardLeft_ClosesIt_ThroughTheMotion()
    {
        var (win, _, b) = OpenAlbumMenu();
        try
        {
            var sub = b.CarouselStyle;
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            await PumpUntil(() => card.Opacity >= 1);
            b.CarouselStyleRows[0].Focus();
            Dispatcher.UIThread.RunJobs();

            win.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(sub.IsSubMenuOpen);
            await PumpUntil(() => !Shown(card));
            Assert.True(b.Menu.IsOpen);
        }
        finally { MenuOpenAnimation.CloseNow(b.Menu); win.Close(); }
    }

    [AvaloniaFact]
    public void WindowClosing_WhileASubmenuFadesOut_ClosesIt_WithoutThrowing()
    {
        var (win, _, b) = OpenAlbumMenu();
        var sub = b.CarouselStyle;
        sub.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var card = CardOf(sub);
        sub.IsSubMenuOpen = false; // fading out
        win.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(card));
        Assert.False(b.Menu.IsOpen);
    }

    /// <summary>A menu closed at once (CloseNow, before a reopen) takes its open submenu with it
    /// at once: no submenu card fading on its own after its menu is gone.</summary>
    [AvaloniaFact]
    public void Menu_ClosedNow_WithASubmenuOpen_ClosesTheSubmenuAtOnce()
    {
        var (win, _, b) = OpenAlbumMenu();
        try
        {
            var sub = b.CarouselStyle;
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            MenuOpenAnimation.CloseNow(b.Menu);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Menu.IsOpen);
            Assert.False(Shown(card));
            Assert.True(card.IsHitTestVisible);
        }
        finally { MenuOpenAnimation.CloseNow(b.Menu); win.Close(); }
    }

    /// <summary>The same for a 3-dots MenuFlyout that closes without a motion of its own.</summary>
    [AvaloniaFact]
    public void Flyout_Hidden_WithASubmenuOpen_ClosesTheSubmenuAtOnce()
    {
        var sub = new MenuItem { Header = "Sleep Timer", Items = { new MenuItem { Header = "15 min" }, new MenuItem { Header = "30 min" } } };
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "Play" }, sub } };
        var button = new Button { Content = "…", Flyout = flyout, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var win = HostWindow(button);
        try
        {
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            Assert.True(Shown(card));
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            Assert.False(Shown(card));
        }
        finally { flyout.Hide(); win.Close(); }
    }

    [AvaloniaFact]
    public async Task GlassSubmenu_FadesItsFade_NotItsOpacity_AndShowsFullyOnTheNextOpen()
    {
        var (win, _, b) = (default(Window)!, default(Border)!, default(AlbumContextMenuBuilder)!);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            (win, _, b) = OpenAlbumMenu();
            var sub = b.CarouselStyle;
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var card = CardOf(sub);
            Assert.True(MenuOpenAnimation.GetUseFade(card));
            Assert.NotNull(GlassMenus.FrostOf(card));
            await PumpUntil(() => MenuOpenAnimation.GetFade(card) >= 1);
            Assert.Equal(1, card.Opacity);

            sub.IsSubMenuOpen = false;
            Assert.Contains(card.Transitions!, t => t is DoubleTransition d && d.Property == MenuOpenAnimation.FadeProperty);
            await Wait(60);
            Assert.True(MenuOpenAnimation.GetFade(card) < 1); // the frost and rows fade out together
            Assert.Equal(1, card.Opacity);
            await PumpUntil(() => !Shown(card));
            Assert.Equal(1, MenuOpenAnimation.GetFade(card));

            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            await PumpUntil(() => MenuOpenAnimation.GetFade(card) >= 1 && card.Opacity >= 1);
            Assert.True(card.IsHitTestVisible);
        }
        finally
        {
            if (b != null) MenuOpenAnimation.CloseNow(b.Menu);
            AppGlass.Clear();
            win?.Close();
        }
    }
}
