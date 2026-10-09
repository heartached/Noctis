using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09: the v2 menu look "everywhere in the app". The card, rows, separators, hover and
/// submenu cards are app-wide styles (Styles.axaml "Menus") on every ContextMenu and every
/// MenuFlyout, not a ".v2" opt-in. The Fluent templates write several of those parts from
/// resources, and a template value outranks a style setter, so those go through
/// <see cref="MenuChrome"/>; these tests read the values off the live templates.
/// </summary>
public class AppMenusV2Tests
{
    private static void EnsureAppStyles() => AddToPlaylistDialogTests.EnsureAppStyles();

    private static object Res(Control c, string key)
    {
        Assert.True(c.TryFindResource(key, c.ActualThemeVariant, out var v), key);
        return v!;
    }

    private static T Part<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static ItemsPresenter Rows(Visual card)
        => card.GetVisualDescendants().OfType<ItemsPresenter>().First(p => p.Name == "PART_ItemsPresenter");

    private static (ContextMenu Menu, MenuItem Item, MenuItem Danger, Separator Sep, MenuItem Sub, Window Win) OpenContextMenu()
    {
        EnsureAppStyles();
        var item = new MenuItem { Header = "Play", Icon = new LineIcon { Classes = { "mv2-icon" }, Data = Geometry.Parse("M0 0L1 1") } };
        var danger = new MenuItem { Header = "Remove", Classes = { "danger" }, Icon = new LineIcon { Classes = { "mv2-icon" }, Data = Geometry.Parse("M0 0L1 1") } };
        var sep = new Separator();
        var sub = new MenuItem { Header = "Tools", Items = { new MenuItem { Header = "Convert" }, new Separator(), new MenuItem { Header = "ReplayGain" } } };
        var menu = new ContextMenu { Items = { item, sep, sub, danger } };
        var owner = new Border { Width = 50, Height = 50 };
        var win = new Window { Width = 1200, Height = 900, Content = owner };
        win.Show();
        owner.ContextMenu = menu;
        menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        return (menu, item, danger, sep, sub, win);
    }

    private static Border OpenSubmenu(MenuItem sub)
    {
        sub.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var card = sub.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup").Child as Border;
        Assert.NotNull(card);
        return card!;
    }

    private static void AssertSubmenuCard(Border card, Control parentCard)
    {
        Assert.Same(Res(parentCard, "AppSidebarBackground"), card.Background);
        Assert.Same(Res(card, "MenuV2BorderBrush"), card.BorderBrush);
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.Equal(new Thickness(6), card.Padding);
        Assert.Equal(new CornerRadius(16), card.CornerRadius);
        Assert.Equal(MenuChrome.SubmenuMaxWidth, card.MaxWidth);
        Assert.Equal(new Thickness(0), Rows(card).Margin);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            card.GetVisualDescendants().OfType<ScrollViewer>().First().HorizontalScrollBarVisibility);
        // Slides out of the parent card (not the menu's rise).
        Assert.True(MenuOpenAnimation.GetEnable(card));
        Assert.Equal(-8, MenuOpenAnimation.GetOffsetX(card));
        Assert.Contains(card.Transitions!, t => t is Avalonia.Animation.TransformOperationsTransition);
    }

    [AvaloniaFact]
    public void PlainContextMenu_GetsTheV2Card_RowsAndSeparators()
    {
        var (menu, item, _, sep, _, win) = OpenContextMenu();
        try
        {
            Assert.DoesNotContain(MenuV2.MenuClass, menu.Classes); // no opt-in needed
            Assert.Equal(new CornerRadius(18), menu.CornerRadius);
            Assert.Equal(248, menu.MinWidth);
            Assert.Equal(330, menu.MaxWidth);
            Assert.Equal(new Thickness(1), menu.BorderThickness);
            Assert.Same(Res(menu, "MenuV2BorderBrush"), menu.BorderBrush);
            Assert.Same(Res(menu, "AppSidebarBackground"), menu.Background);
            var chrome = menu.GetVisualChildren().OfType<Border>().Single();
            Assert.Equal(new Thickness(6), chrome.Padding);
            // Was 0,4 from the template's MenuFlyoutScrollerMargin (a style setter could not reach it).
            Assert.Equal(new Thickness(0), Rows(menu).Margin);
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                menu.GetVisualDescendants().OfType<ScrollViewer>().First().HorizontalScrollBarVisibility);

            Assert.Equal(32, item.MinHeight);
            Assert.Equal(new Thickness(0), item.Margin);
            Assert.Equal(new CornerRadius(999), Part<Border>(item, "PART_LayoutRoot").CornerRadius);
            Assert.True(item.Bounds.Height >= 32);

            Assert.Equal(new Thickness(8, 5), sep.Margin);
            Assert.Equal(1, sep.Height);
            Assert.Same(Res(sep, "MenuV2SeparatorBrush"), sep.Background);
        }
        finally
        {
            menu.Close();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void ContextSubmenu_IsAV2Card_InTheParentsThemeColour()
    {
        var (menu, _, _, _, sub, win) = OpenContextMenu();
        try
        {
            var card = OpenSubmenu(sub);
            AssertSubmenuCard(card, menu);
            var innerSep = sub.Items.OfType<Separator>().Single();
            Assert.Equal(new Thickness(8, 5), innerSep.Margin);
            Assert.Same(Res(innerSep, "MenuV2SeparatorBrush"), innerSep.Background);
        }
        finally
        {
            menu.Close();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Hover_IsThePillHighlight_OnTheTemplateRoot_RedOnDanger()
    {
        var (menu, item, danger, _, sub, win) = OpenContextMenu();
        try
        {
            var before = item.Background;
            ((IPseudoClasses)item.Classes).Add(":pointerover");
            ((IPseudoClasses)danger.Classes).Add(":pointerover");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Res(item, "MenuV2HoverBrush"), Part<Border>(item, "PART_LayoutRoot").Background);
            Assert.Same(Res(danger, "MenuV2DangerHoverBrush"), Part<Border>(danger, "PART_LayoutRoot").Background);
            // The highlight lives on PART_LayoutRoot: MenuItem.Background never changes, so the
            // old 70 ms BrushTransition on it could never run (removed), and nothing tweens now.
            Assert.Same(before, item.Background);
            Assert.True(item.Transitions is null || item.Transitions.Count == 0);
            // Nor does anything fade the submenu Popup itself (the old 20 ms Opacity tween).
            var popup = sub.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");
            Assert.True(popup.Transitions is null || popup.Transitions.Count == 0);
        }
        finally
        {
            menu.Close();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void LineIcons_AreMuted_RedOnDanger_AndChecksTakeTheLabelColour()
    {
        var (menu, item, danger, _, _, win) = OpenContextMenu();
        try
        {
            Assert.Same(Res(item, "MenuV2IconBrush"), ((LineIcon)item.Icon!).Foreground);
            Assert.Equal(Color.Parse("#E74856"), ((ISolidColorBrush)((LineIcon)danger.Icon!).Foreground!).Color);

            var check = new LineIcon { Classes = { "mv2-icon", "mv2-check" }, Data = Geometry.Parse("M0 0L1 1") };
            item.Icon = check;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Res(item, "SystemControlForegroundBaseHighBrush"), check.Foreground);
        }
        finally
        {
            menu.Close();
            win.Close();
        }
    }

    private static (MenuFlyout Flyout, MenuItem Sub, Button Anchor, Window Win) OpenFlyout()
    {
        EnsureAppStyles();
        var sub = new MenuItem { Header = "Sleep Timer", Items = { new MenuItem { Header = "15 min" } } };
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "0.75×" }, new Separator(), sub } };
        var anchor = new Button { Content = "…" };
        var win = new Window { Width = 1200, Height = 900, Content = anchor };
        win.Show();
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        return (flyout, sub, anchor, win);
    }

    [AvaloniaFact]
    public void MenuFlyout_GetsTheSameCard_AndSubmenuCard()
    {
        var (flyout, sub, _, win) = OpenFlyout();
        try
        {
            var presenter = Assert.IsType<MenuFlyoutPresenter>(flyout.Popup.Child);
            Assert.Equal(new CornerRadius(18), presenter.CornerRadius);
            Assert.Equal(248, presenter.MinWidth);
            Assert.Equal(330, presenter.MaxWidth);
            Assert.Same(Res(presenter, "MenuV2BorderBrush"), presenter.BorderBrush);
            Assert.Same(Res(presenter, "AppSidebarBackground"), presenter.Background);
            // The flyout card's padding is the template's FlyoutBorderThemePadding (0), not
            // its Padding, so the old "MenuFlyoutPresenter { Padding: 6 }" never showed.
            Assert.Equal(new Thickness(6), Part<Border>(presenter, "LayoutRoot").Padding);
            Assert.Equal(new Thickness(0), Rows(presenter).Margin);
            Assert.True(MenuOpenAnimation.GetEnable(presenter));
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                presenter.GetVisualDescendants().OfType<ScrollViewer>().First().HorizontalScrollBarVisibility);

            AssertSubmenuCard(OpenSubmenu(sub), presenter);
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void MenuFlyout_KeyboardNavigationStillSelectsRows()
    {
        var (flyout, _, _, win) = OpenFlyout();
        try
        {
            var presenter = (MenuFlyoutPresenter)flyout.Popup.Child!;
            presenter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = presenter });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(presenter.Items.OfType<MenuItem>(), i => i.IsSelected);
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    /// <summary>GitHub #104: with Liquid Glass on the island's menus swap the card for a
    /// GlassPanel; it must carry the same padding and row margin as the plain card.</summary>
    [AvaloniaFact]
    public async Task IslandMenu_GlassOnAndOff_KeepsTheV2Spacing()
    {
        EnsureAppStyles();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1280, Height = 900, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var button = bar.FindControl<Button>("PlaybackSpeedButton")!;
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var presenter = Assert.IsType<MenuFlyoutPresenter>(flyout.Popup.Child);
            Assert.Contains(GlassMenuFlyout.GlassClass, presenter.Classes);
            var glass = presenter.GetVisualDescendants().OfType<GlassPanel>().Single();
            Assert.Equal(new Thickness(6), glass.Padding);
            Assert.Equal(new Thickness(0), Rows(presenter).Margin);
            Assert.Equal(248, presenter.MinWidth);
            // The island menus close with the 120 ms close motion (EnableFlyoutClose): wait it out
            // (a yielding pump, so its DispatcherTimer can fire).
            flyout.Hide();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (flyout.IsOpen && sw.ElapsedMilliseconds < 2000) { Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
            Assert.False(flyout.IsOpen);

            AppGlass.Clear();
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(GlassMenuFlyout.GlassClass, presenter.Classes);
            Assert.Equal(new Thickness(6), Part<Border>(presenter, "LayoutRoot").Padding);
            Assert.Equal(new Thickness(0), Rows(presenter).Margin);
            // Speed rows: line checkmarks, no Fluent filled CheckmarkIcon.
            var checks = presenter.Items.OfType<MenuItem>().Select(i => i.Icon).OfType<LineIcon>().ToList();
            Assert.NotEmpty(checks);
            Assert.All(checks, c => Assert.Contains("mv2-check", c.Classes));
            flyout.Hide();
        }
        finally
        {
            AppGlass.Clear();
            win.Close();
        }
    }

    // ── Source checks: every menu in these views uses the v2 line icons ──

    private static readonly XNamespace Av = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static readonly string[] MenuViewNames =
    {
        "MainWindow", "PlaybackBarView", "AlbumDetailView", "ArtistDetailView", "LyricsStudioPanel",
        "ServerView", "AudioCdView", "SettingsView",
    };

    public static TheoryData<string> MenuViews() => new(MenuViewNames);

    [Theory]
    [MemberData(nameof(MenuViews))]
    public void XamlMenus_UseLineIcons_NotPngMasksOrFilledGlyphs(string view)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "Noctis", "Views", view + ".axaml"));
        // Album / artist pages: only their header MenuFlyouts are XAML menus of this pass (their
        // tile and track menus are built in code by the shared builders).
        var flyoutsOnly = view is "AlbumDetailView" or "ArtistDetailView";
        var menus = doc.Descendants().Where(e => e.Name == Av + "MenuFlyout" || (!flyoutsOnly && e.Name == Av + "ContextMenu")).ToList();
        Assert.NotEmpty(menus);
        foreach (var menu in menus)
        {
            Assert.DoesNotContain(menu.Descendants(), e => e.Name.LocalName is "ImageBrush" or "OpacityMask" or "Border.OpacityMask");
            Assert.DoesNotContain(menu.Descendants(), e => e.Name.LocalName == "PathIcon");
            Assert.DoesNotContain(menu.Descendants(), e => ((string?)e.Attribute("ResourceKey"))?.StartsWith("IconMask") == true);
            foreach (var icon in menu.Descendants().Where(e => e.Name.LocalName == "MenuItem.Icon").SelectMany(i => i.Elements()))
                Assert.Equal("LineIcon", icon.Name.LocalName);
        }
    }

    [Fact]
    public void EveryMenuLineKeyUsedInXaml_IsDefined()
    {
        var keys = new HashSet<string>();
        foreach (var f in new[] { "Icons.axaml", "IconsLine.axaml", "IconsMenuExtra.axaml", "IconsPages.axaml" })
        {
            var p = Path.Combine(RepoRoot(), "src", "Noctis.UI", "Assets", f);
            if (!File.Exists(p)) continue;
            foreach (var g in XDocument.Load(p).Descendants(Av + "StreamGeometry"))
                keys.Add((string)g.Attribute(X + "Key")!);
        }
        foreach (var view in MenuViewNames.Append("MiniPlayerWindow"))
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "Views", view + ".axaml"));
            foreach (Match m in Regex.Matches(text, @"Resource ((?:MenuLine|Line)\w+)\}"))
                Assert.True(keys.Contains(m.Groups[1].Value), $"{view}: {m.Groups[1].Value} is not defined");
        }
    }

    [Fact]
    public void MiniPlayerMenu_IsTheV2Card()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "Noctis", "Views", "MiniPlayerWindow.axaml"));
        var card = doc.Descendants(Av + "Border").Single(e => (string?)e.Attribute(X + "Name") == "MenuCard");
        Assert.Equal("18", (string?)card.Attribute("CornerRadius"));
        Assert.Equal("{DynamicResource MenuV2BorderBrush}", (string?)card.Attribute("BorderBrush"));
        Assert.DoesNotContain(card.Descendants(), e => e.Name.LocalName is "ImageBrush" or "PathIcon");
        Assert.Equal(2, card.Descendants(Av + "Border").Count(b => (string?)b.Attribute("Classes") == "mini-menu-separator"));
    }

    // ── Eyeball probe (NOCTIS_TEST_SKIA=1): a top-bar style sort menu and an island style
    //    flyout card, laid out inline like ContextMenuV2Tests.Probe_RenderMenus, both themes ──

    [AvaloniaFact]
    public async Task Probe_RenderPlainMenus()
    {
        if (!HeadlessTestApp.RealRendering) return;
        EnsureAppStyles();
        Assert.True(Application.Current!.TryGetResource("MenuLineCheck", null, out var check));
        Assert.True(Application.Current!.TryGetResource("MenuLineTrash", null, out var trash));
        Assert.True(Application.Current!.TryGetResource("MenuLineTimer", null, out var timer));
        foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Dark, Avalonia.Styling.ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            MenuItem Row(string header, Geometry? icon = null, bool isCheck = false, bool danger = false)
            {
                var i = new MenuItem { Header = header };
                if (icon != null) i.Icon = new LineIcon { Classes = { "mv2-icon" }, Data = icon };
                if (isCheck) ((LineIcon)i.Icon!).Classes.Add("mv2-check");
                if (danger) i.Classes.Add("danger");
                return i;
            }
            var sort = new ContextMenu { Items = { Row("Title", (Geometry)check!, true), Row("Artist"), Row("Album"), new Separator(), Row("Ascending", (Geometry)check!, true), Row("Descending") } };
            var island = new ContextMenu { Items = { Row("Sleep Timer", (Geometry)timer!), new Separator(), Row("Remove from Library", (Geometry)trash!, danger: true) } };
            ((IPseudoClasses)((MenuItem)sort.Items[1]!).Classes).Add(":pointerover");
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, Margin = new Thickness(16) };
            row.Children.Add(sort);
            row.Children.Add(island);
            var win = new Window
            {
                Width = 700, Height = 400, Content = row,
                Background = (IBrush?)(Application.Current.TryGetResource("AppMainBackground", variant, out var bg) ? bg : null),
            };
            win.Show();
            // Let the open fade (MenuOpenAnimation, 150 ms) finish before the capture.
            for (var t = 0; t < 40; t++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            var path = Path.Combine(Path.GetTempPath(), $"noctis-menu-plain-{variant}.png");
            win.CaptureRenderedFrame()!.Save(path);
            win.Close();
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }
}
