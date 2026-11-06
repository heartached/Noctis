using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Track = Noctis.Models.Track;

namespace Noctis.Tests;

/// <summary>
/// GitHub #169, whole app: with Liquid Glass on, every right-click menu, menu flyout, option
/// flyout, submenu, drop-down list and tooltip of every window frosts (the main window's in its
/// overlay layer, every other window's in its own popup over a GlassMenuBackdrop); with it off
/// none of them is touched. The sweep mounts the app's real surfaces and opens what it finds on
/// them, so a menu, list or tooltip added later is covered without a new test.
/// </summary>
public class GlassMenusSweep169Tests
{
    private const int Cap = 10;

    private static void EnsureStyles()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        if (!app.Resources.TryGetResource("MenuLineEyeOff", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/IconsMenuExtra.axaml"),
            });
        ComboBoxDropDownAnimator.Install();
    }

    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static bool PumpUntil(Func<bool> done, int budgetMs = 2000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Pump(1);
            Thread.Sleep(5);
        }
        Pump(1);
        return done();
    }

    /// <summary>For DispatcherTimer-driven closes: the headless timers need the awaits.</summary>
    private static async Task<bool> PumpUntilAsync(Func<bool> done, int budgetMs = 2000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Pump(1);
            await Task.Delay(8);
        }
        Pump(1);
        return done();
    }

    private static Window Show(Control content, bool host, double width = 1400, double height = 900)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        if (host) GlassMenus.SetHost(window, true);
        window.Show();
        Pump();
        return window;
    }

    private static Track Song(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Nine Inch Nails", AlbumArtist = "Nine Inch Nails",
        Album = "Pretty Hate Machine", AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromMinutes(4),
        FilePath = TestPaths.Primary("glass-sweep", title + ".mp3"),
    };

    // ── what one open must look like ──

    private sealed class Sweep(string surface, bool overlay)
    {
        public readonly List<string> Failures = new();
        public int Checked;

        private void Fail(string what, string why) => Failures.Add($"{surface}: {what}: {why}");

        /// <summary>A card hosted by its own template (menu, flyout presenter, tooltip).</summary>
        public void Card(Control card, string what, bool glassOn)
        {
            Checked++;
            var frosted = card.Classes.Contains(GlassMenus.GlassClass);
            var inOverlay = card.Classes.Contains(GlassMenus.OverlayClass);
            var glass = card.GetVisualDescendants().OfType<GlassPanel>().Any();
            if (glassOn)
            {
                if (!frosted) Fail(what, "glass on but no glass-menu class");
                if (inOverlay != overlay) Fail(what, $"overlay={inOverlay}, expected {overlay}");
                if (!glass) Fail(what, "glass on but no GlassPanel in the card");
                if (!card.GetVisualDescendants().OfType<GlassMenuBackdrop>().Any()) Fail(what, "no GlassMenuBackdrop");
                if (card.Parent is Popup p && p.ShouldUseOverlayLayer != overlay && card is not ToolTip)
                    Fail(what, $"popup overlay layer={p.ShouldUseOverlayLayer}, expected {overlay}");
            }
            else
            {
                if (frosted) Fail(what, "glass off but the card is tagged");
                // The main window keeps every card in its overlay layer glass on or off, so a
                // card stays inside the window (11-06); other windows keep their native popups.
                if (inOverlay != overlay) Fail(what, $"glass off: overlay={inOverlay}, expected {overlay}");
                if (glass) Fail(what, "glass off but a GlassPanel is in the card");
            }
        }

        /// <summary>A plain Border card frosted in place (a drop-down list, a custom menu
        /// popup, a submenu, an option flyout's card).</summary>
        public void BorderCard(Border card, string what, bool glassOn, bool checkOverlay = true)
        {
            Checked++;
            var frost = GlassMenus.FrostOf(card);
            if (glassOn)
            {
                if (frost is null) Fail(what, "glass on but the card is not frosted");
                if (checkOverlay && card.Classes.Contains(GlassMenus.OverlayClass) != overlay)
                    Fail(what, $"overlay class expected {overlay}");
            }
            else if (frost != null || card.Classes.Contains(GlassMenus.GlassClass))
            {
                Fail(what, "glass off but the card is frosted");
            }
        }
    }

    private static readonly FieldInfo? TipInstanceField =
        typeof(ToolTip).GetField("ToolTipProperty", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>The ToolTip control an owner shows (Avalonia makes one for a plain Tip).</summary>
    private static ToolTip? TipOf(Control owner)
    {
        if (ToolTip.GetTip(owner) is ToolTip own) return own;
        return TipInstanceField?.GetValue(null) is AvaloniaProperty p ? owner.GetValue(p) as ToolTip : null;
    }

    /// <summary>Opens what <paramref name="root"/> shows (menus, flyouts, lists, tooltips) with
    /// glass on and with glass off, and checks each card.</summary>
    private static Sweep SweepSurface(string surface, Control root, bool overlay)
    {
        var sweep = new Sweep(surface, overlay);
        foreach (var glassOn in new[] { true, false })
        {
            if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
            else AppGlass.Clear();
            var tag = glassOn ? "on" : "off";
            var controls = root.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();

            foreach (var owner in controls.Where(c => c.ContextMenu != null).DistinctBy(c => c.ContextMenu).Take(Cap))
            {
                var menu = owner.ContextMenu!;
                menu.Open(owner);
                Pump();
                sweep.Card(menu, $"context menu of {owner.GetType().Name} {owner.Name} ({tag})", glassOn);
                foreach (var sub in menu.GetLogicalDescendants().OfType<MenuItem>().Where(i => i.IsVisible && i.ItemCount > 0).Take(2))
                {
                    sub.IsSubMenuOpen = true;
                    Pump();
                    if (sub.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup")?.Child is Border card)
                        sweep.BorderCard(card, $"submenu {sub.Header} ({tag})", glassOn, checkOverlay: false);
                    sub.IsSubMenuOpen = false;
                    Pump();
                }
                MenuOpenAnimation.CloseNow(menu);
                PumpUntil(() => !menu.IsOpen);
            }

            var flyouts = controls.OfType<Button>().Where(b => b.Flyout is PopupFlyoutBase).Select(b => (Owner: (Control)b, Flyout: (PopupFlyoutBase)b.Flyout!))
                .Concat(controls.Where(c => c.ContextFlyout is PopupFlyoutBase).Select(c => (Owner: c, Flyout: (PopupFlyoutBase)c.ContextFlyout!)))
                .DistinctBy(x => x.Flyout).Take(Cap).ToList();
            foreach (var (owner, flyout) in flyouts)
            {
                flyout.ShowAt(owner);
                Pump();
                var what = $"flyout of {owner.GetType().Name} {owner.Name} ({tag})";
                switch (flyout.Popup.Child)
                {
                    case MenuFlyoutPresenter presenter:
                        sweep.Card(presenter, what, glassOn);
                        break;
                    case FlyoutPresenter presenter when GlassMenus.GetTrack(presenter):
                        foreach (var card in presenter.GetLogicalDescendants().OfType<Border>()
                                     .Where(b => b.Classes.Contains("ms-pop-card") || b.Classes.Contains("pill-popover")).Take(1))
                            sweep.BorderCard(card, what, glassOn);
                        break;
                }
                flyout.Hide();
                PumpUntil(() => !flyout.IsOpen);
            }

            foreach (var box in controls.OfType<ComboBox>().Where(b => b.IsEnabled && b.ItemCount > 0).Take(Cap))
            {
                box.IsDropDownOpen = true;
                Pump();
                if (GlassMenus.DropDownPopup(box)?.Child is Border card)
                    sweep.BorderCard(card, $"drop-down {box.Name} ({tag})", glassOn);
                box.IsDropDownOpen = false;
                if (!PumpUntil(() => !box.IsDropDownOpen && GlassMenus.DropDownPopup(box)?.IsOpen != true))
                    sweep.Failures.Add($"{surface}: drop-down {box.Name} ({tag}): never closed");
            }

            foreach (var owner in controls.Where(c => ToolTip.GetTip(c) != null).Take(Cap))
            {
                ToolTip.SetIsOpen(owner, true);
                Pump();
                if (TipOf(owner) is { } tip)
                    sweep.Card(tip, $"tooltip of {owner.GetType().Name} {owner.Name} ({tag})", glassOn);
                else
                    sweep.Failures.Add($"{surface}: tooltip of {owner.GetType().Name}: no ToolTip found");
                ToolTip.SetIsOpen(owner, false);
                Pump(1);
            }
        }
        AppGlass.Clear();
        return sweep;
    }

    private static void AssertSwept(Sweep sweep, int atLeast)
    {
        Assert.True(sweep.Failures.Count == 0, string.Join("\n", sweep.Failures));
        Assert.True(sweep.Checked >= atLeast, $"only {sweep.Checked} cards were opened");
    }

    // ── main window surfaces (overlay host) ──

    [AvaloniaFact]
    public void PlaybackBar_MenusAndTooltips_MainWindow()
    {
        EnsureStyles();
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = Song("Head Like A Hole");
        player.Duration = TimeSpan.FromMinutes(4);
        var bar = new PlaybackBarView { DataContext = player };
        var window = Show(bar, host: true, height: 300);
        try { AssertSwept(SweepSurface("Playback bar", bar, overlay: true), 8); }
        finally { AppGlass.Clear(); window.Close(); }
    }

    [AvaloniaFact]
    public void Sidebar_MenusAndTooltips_MainWindow()
    {
        EnsureStyles();
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService()) { TopBar = new TopBarViewModel() };
        vm.SidebarRows.Add(new PlaylistNavItem { Label = "Mixes", IsFolder = true, Folder = "Mixes" });
        vm.SidebarRows.Add(new PlaylistNavItem { Label = "Road Trip", PlaylistId = Guid.NewGuid() });
        var view = new SidebarView { DataContext = vm };
        var window = Show(view, host: true, width: 300);
        try { AssertSwept(SweepSurface("Sidebar", view, overlay: true), 4); }
        finally { AppGlass.Clear(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Settings_ListsMenusAndTooltips_MainWindow()
    {
        EnsureStyles();
        var vm = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        await vm.LoadAsync();
        var view = new SettingsView { DataContext = vm };
        var window = Show(view, host: true, width: 1100);
        try
        {
            var total = 0;
            foreach (var tab in new[] { SettingsViewModel.TabGeneral, SettingsViewModel.TabAppearance, SettingsViewModel.TabAudio, SettingsViewModel.TabLyrics })
            {
                vm.SelectedSettingsTab = tab;
                Pump();
                var sweep = SweepSurface($"Settings ({tab})", view, overlay: true);
                Assert.True(sweep.Failures.Count == 0, string.Join("\n", sweep.Failures));
                total += sweep.Checked;
            }
            Assert.True(total >= 20, $"only {total} cards were opened");
        }
        finally { AppGlass.Clear(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SongsPage_RowMenu_MainWindow()
    {
        EnsureStyles();
        var lib = new FakeLibraryService();
        var track = Song("Hello");
        lib.TrackList.Add(track);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var window = Show(view, host: true);
        vm.IsActive = true;
        try
        {
            Assert.True(PumpUntil(() => view.TrackList.GetRealizedContainers().Any(c => c.DataContext == track), 5000));
            var row = view.TrackList.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == track);
            // The row's menu is built on the right-click (TrackContextMenuBuilder).
            row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });
            Pump();
            Assert.NotNull(row.ContextMenu);
            MenuOpenAnimation.CloseNow(row.ContextMenu!);
            PumpUntil(() => !row.ContextMenu!.IsOpen);
            AssertSwept(SweepSurface("Songs page", view, overlay: true), 2);
        }
        finally { AppGlass.Clear(); window.Close(); }
    }

    // ── other windows (own popups) ──

    private static MiniPlayerViewModel MiniPlayerVm()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new NoLrcLib(), new NoNetEase(), new SearchPopupsShotsTests.OkTags(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new FakeHistoryLog());
        player.CurrentTrack = Song("Head Like A Hole");
        player.Duration = TimeSpan.FromMinutes(4);
        player.UpNext.Add(Song("Terrible Lie"));
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private sealed class NoLrcLib : Noctis.Services.ILrcLibService
    {
        public Task<Noctis.Models.LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<Noctis.Models.LrcLibResult?>(null);
        public Task<List<Noctis.Models.LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<Noctis.Models.LrcLibResult>());
    }

    private sealed class NoNetEase : Noctis.Services.INetEaseService
    {
        public Task<Noctis.Models.LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<Noctis.Models.LrcLibResult?>(null);
    }

    [AvaloniaFact]
    public async Task MiniPlayer_TooltipsMenusAndMoreMenu_FrostInTheirOwnPopups()
    {
        EnsureStyles();
        foreach (var style in Enum.GetValues<MiniPlayerStyle>())
        {
            var vm = MiniPlayerVm();
            vm.SetDesignCommand.Execute(style.ToString());
            var win = new MiniPlayerWindow { DataContext = vm, Width = 420, Height = 188 };
            win.Show();
            Pump(6);
            try
            {
                Assert.False(GlassMenus.GetHost(win));
                AssertSwept(SweepSurface($"Mini player ({style})", win, overlay: false), 4);

                // The "…" menu: a custom popup whose card frosts like the menus.
                var more = win.GetVisualDescendants().OfType<Button>()
                    .First(b => b.IsEffectivelyVisible && b.GetValue(ToolTip.TipProperty) is string tip
                                && tip == Noctis.Localization.Loc.T("MiniPlayer.MoreTip"));
                var popup = win.FindControl<Popup>("MorePopup")!;
                var card = win.FindControl<Border>("MenuCard")!;
                foreach (var glassOn in new[] { true, false, true })
                {
                    if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
                    else AppGlass.Clear();
                    more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, more));
                    Pump();
                    Assert.True(popup.IsOpen);
                    Assert.False(popup.ShouldUseOverlayLayer);
                    Assert.Equal(glassOn, GlassMenus.FrostOf(card) != null);
                    Assert.Equal(glassOn, card.Classes.Contains(GlassMenus.GlassClass));
                    // It fades in fully either way (Fade on the glass card, Opacity on the plain one).
                    Assert.True(PumpUntil(() => card.Opacity == 1 && (!glassOn || MenuOpenAnimation.GetFade(card) == 1)),
                        $"the {(glassOn ? "glass" : "plain")} card never faded in");
                    var item = card.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Classes.Contains("mini-menu-item"));
                    item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, item));
                    Assert.True(await PumpUntilAsync(() => !popup.IsOpen), $"the menu never closed (glass {glassOn}, item {item.Name}/{Avalonia.Automation.AutomationProperties.GetName(item)}, opacity {card.Opacity}, fade {MenuOpenAnimation.GetFade(card)})");
                }
            }
            finally
            {
                AppGlass.Clear();
                win.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Dialogs_ListsMenusAndTooltips_FrostInTheirOwnPopups()
    {
        EnsureStyles();
        var dialogs = new (string Name, Window Dialog)[]
        {
            ("Create smart playlist", new CreateSmartPlaylistDialog()),
            ("Edit playlist", new EditPlaylistDialog()),
            ("Create playlist", new CreatePlaylistDialog { DataContext = new CreatePlaylistDialogViewModel() }),
            ("Add to playlist", new AddToPlaylistDialog { DataContext = new AddToPlaylistDialogViewModel(AddToPlaylistDialogTests.Lists(3), 2) }),
        };
        var total = 0;
        foreach (var (name, dialog) in dialogs)
        {
            dialog.Width = 1000;
            dialog.Height = 760;
            dialog.Show();
            Pump();
            try
            {
                Assert.False(GlassMenus.GetHost(dialog));
                var sweep = SweepSurface(name, dialog, overlay: false);
                Assert.True(sweep.Failures.Count == 0, string.Join("\n", sweep.Failures));
                total += sweep.Checked;
            }
            finally
            {
                AppGlass.Clear();
                dialog.Close();
            }
        }
        Assert.True(total >= 6, $"only {total} cards were opened");
    }

    [AvaloniaFact]
    public async Task EditInfo_ListsAndTooltips_FrostInTheirOwnPopups()
    {
        EnsureStyles();
        var track = Song("Head Like A Hole");
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: new MetadataSearchPanelTests.FakeSearch());
        var win = new MetadataWindow(vm) { Width = 1100, Height = 820 };
        win.Show();
        var load = vm.InitializeAsync();
        PumpUntil(() => load.IsCompleted, 5000);
        await load;
        try { AssertSwept(SweepSurface("Edit Info", win, overlay: false), 6); }
        finally { AppGlass.Clear(); win.Close(); }
    }

    // ── mechanisms the sweep can't reach on a mounted surface ──

    [AvaloniaFact]
    public void AutoCompleteBox_SuggestionList_Frosts_AndNotWithGlassOff()
    {
        EnsureStyles();
        var box = new AutoCompleteBox { ItemsSource = new[] { "Mixes", "Moods", "Road" }, MinimumPrefixLength = 0, Width = 200 };
        var window = Show(new StackPanel { Children = { box } }, host: false, width: 600, height: 500);
        try
        {
            foreach (var glassOn in new[] { true, false })
            {
                if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
                else AppGlass.Clear();
                box.IsDropDownOpen = true;
                Pump();
                var popup = GlassMenus.DropDownPopup(box);
                Assert.NotNull(popup);
                var card = Assert.IsType<Border>(popup!.Child);
                Assert.Equal(glassOn, GlassMenus.FrostOf(card) != null);
                Assert.False(popup.ShouldUseOverlayLayer);
                box.IsDropDownOpen = false;
                Pump();
            }
        }
        finally { AppGlass.Clear(); window.Close(); }
    }

    /// <summary>The Find online "Sources" / "…" and Lyrics Studio "Options" pop-overs: a
    /// chromeless Flyout around a card. Their presenter style tracks the glass (checked in the
    /// source below), and the card frosts.</summary>
    [AvaloniaFact]
    public void OptionFlyoutCard_Frosts_WhileThePresenterIsTracked()
    {
        EnsureStyles();
        var card = new Border { Classes = { "ms-pop-card" }, Padding = new Thickness(16, 12), CornerRadius = new CornerRadius(16),
            Background = Brushes.Gray, Child = new CheckBox { Content = "Only fill empty" } };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "ms-pop" } };
        DropDownFlyoutMotion.SetEnable(flyout, true);
        var button = new Button { Content = "…", Flyout = flyout };
        var root = new StackPanel { Children = { button } };
        root.Styles.Add(new Style(x => x.OfType<FlyoutPresenter>().Class("ms-pop"))
        {
            Setters = { new Setter(GlassMenus.TrackProperty, true) },
        });
        foreach (var host in new[] { true, false })
        {
            var window = Show(root, host, 600, 500);
            try
            {
                foreach (var glassOn in new[] { true, false })
                {
                    if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
                    else AppGlass.Clear();
                    flyout.ShowAt(button);
                    Pump();
                    var presenter = Assert.IsType<FlyoutPresenter>(flyout.Popup.Child);
                    Assert.True(glassOn == presenter.Classes.Contains(GlassMenus.GlassClass),
                        $"host={host} on={glassOn} track={GlassMenus.GetTrack(presenter)} classes={string.Join(",", presenter.Classes)} parent={presenter.Parent?.GetType().Name}");
                    // The main window keeps it in its overlay layer glass on or off (11-06).
                    Assert.Equal(host, flyout.Popup.ShouldUseOverlayLayer);
                    Assert.Equal(glassOn, GlassMenus.FrostOf(card) != null);
                    if (!glassOn) Assert.Same(Brushes.Gray, card.Background);
                    // The drop-down motion fades the presenter's Fade on glass (the card's glass
                    // follows it; Opacity stays 1 so the frost never shows at full strength
                    // through a fade), its Opacity otherwise.
                    Assert.True(PumpUntil(() => presenter.Opacity == 1 && (!glassOn || MenuOpenAnimation.GetFade(presenter) == 1)),
                        $"never faded in (glass {glassOn})");
                    if (glassOn)
                    {
                        Assert.Equal(1, MenuOpenAnimation.GetFade(card));
                        Assert.Equal(1, GlassMenus.FrostOf(card)!.Fade);
                    }
                    flyout.Hide();
                    Assert.True(PumpUntil(() => !flyout.IsOpen), $"never closed (glass {glassOn})");
                    if (glassOn) Assert.Equal(1, presenter.Opacity);
                }
            }
            finally
            {
                AppGlass.Clear();
                window.Content = null;
                window.Close();
            }
        }
    }

    /// <summary>A glass drop-down fades MenuOpenAnimation.Fade, not Opacity: the close used to
    /// wait for Opacity to reach 0 (ComboBoxDropDownAnimator.WatchFadeOut), so a glass list never
    /// closed after its fade.</summary>
    [AvaloniaFact]
    public void GlassDropDown_ClosesAfterItsFade_AndReopensPlain()
    {
        EnsureStyles();
        var box = new ComboBox { ItemsSource = new[] { "1 GB", "2 GB", "4 GB" }, SelectedIndex = 0, Width = 160 };
        var window = Show(new StackPanel { Children = { box } }, host: true, width: 600, height: 500);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            box.IsDropDownOpen = true;
            Pump();
            var popup = GlassMenus.DropDownPopup(box)!;
            var card = Assert.IsType<Border>(popup.Child);
            Assert.NotNull(GlassMenus.FrostOf(card));
            Assert.True(popup.ShouldUseOverlayLayer);
            Assert.True(MenuOpenAnimation.GetUseFade(card));
            Assert.True(PumpUntil(() => MenuOpenAnimation.GetFade(card) == 1));
            Assert.Equal(1, card.Opacity);

            box.IsDropDownOpen = false;
            Assert.True(PumpUntil(() => !box.IsDropDownOpen && !popup.IsOpen), "the glass list never closed");

            AppGlass.Clear();
            box.IsDropDownOpen = true;
            Pump();
            Assert.Null(GlassMenus.FrostOf(card));
            // Plain now, still in the main window's overlay layer (11-06: cards stay inside the window).
            Assert.True(popup.ShouldUseOverlayLayer);
            Assert.True(PumpUntil(() => card.Opacity == 1));
            box.IsDropDownOpen = false;
            Assert.True(PumpUntil(() => !box.IsDropDownOpen && !popup.IsOpen), "the plain list never closed");
        }
        finally { AppGlass.Clear(); window.Close(); }
    }

    /// <summary>The other way round: a plain list's close fades its card's Opacity to 0 and
    /// leaves it there (the next plain open fades it back in). A glass open fades only
    /// MenuOpenAnimation.Fade, so a list opened plain once and then with Liquid Glass turned on
    /// stayed at Opacity 0: an invisible drop-down.</summary>
    [AvaloniaFact]
    public void PlainDropDown_ThenGlassOn_ReopensVisible()
    {
        EnsureStyles();
        var box = new ComboBox { ItemsSource = new[] { "1 GB", "2 GB", "4 GB" }, SelectedIndex = 0, Width = 160 };
        var window = Show(new StackPanel { Children = { box } }, host: true, width: 600, height: 500);
        try
        {
            AppGlass.Clear();
            box.IsDropDownOpen = true;
            Pump();
            var popup = GlassMenus.DropDownPopup(box)!;
            var card = Assert.IsType<Border>(popup.Child);
            Assert.True(PumpUntil(() => card.Opacity == 1));
            box.IsDropDownOpen = false;
            Assert.True(PumpUntil(() => !box.IsDropDownOpen && !popup.IsOpen), "the plain list never closed");

            AppGlass.Set(true, Colors.Black, Colors.Black);
            box.IsDropDownOpen = true;
            Pump();
            Assert.NotNull(GlassMenus.FrostOf(card));
            Assert.True(PumpUntil(() => MenuOpenAnimation.GetFade(card) == 1));
            Assert.True(PumpUntil(() => card.Opacity == 1), $"the glass list stayed at Opacity {card.Opacity}");
            box.IsDropDownOpen = false;
            Assert.True(PumpUntil(() => !box.IsDropDownOpen && !popup.IsOpen), "the glass list never closed");
        }
        finally { AppGlass.Clear(); window.Close(); }
    }

    /// <summary>The glass tint follows Glass Opacity in every window: an application resource.</summary>
    [AvaloniaFact]
    public void GlassTint_IsAnApplicationResource()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("app.Resources[\"GlassMenuTintOpacity\"]", source);
        Assert.DoesNotContain("    Resources[\"GlassMenuTintOpacity\"]", source);
    }

    // ── source guard: a new popup must be classified ──

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

    /// <summary>
    /// Every hand-made Popup and every non-menu Flyout in the desktop app is listed here with how
    /// it frosts (or why it is not a menu). ContextMenu, MenuFlyout, ComboBox, AutoCompleteBox and
    /// ToolTip need no entry: GlassMenus handles every one of them. A new Popup or Flyout fails this
    /// test until it is added, so it can't ship unfrosted by accident.
    /// </summary>
    [Fact]
    public void EveryCustomPopupAndFlyout_IsClassified()
    {
        var known = new Dictionary<string, string>
        {
            ["MiniPlayerWindow.axaml Popup MorePopup"] = "menu: GlassMenus.ApplyCardPopup in OnMoreMenuClick",
            ["PlaybackBarView.axaml Popup VolumeFlyout"] = "volume pill: its own GlassPanel since #104",
            ["SidebarView.axaml Popup SearchPopup"] = "the search field capsule, not a menu",
            ["SettingsView.axaml Popup PART_Popup"] = "the pill-combo ComboBox template: GlassMenus drop-down",
            ["LyricsStudioPanel.axaml Flyout studio-options-pop"] = "option menu: presenter tracked, pill-popover card frosts",
            ["MetadataSearchPanel.axaml Flyout ms-pop"] = "option menus: presenter tracked, ms-pop-card frosts",
            ["MetadataWindow.axaml Flyout search-pop"] = "artwork search panels, not menus",
            ["SettingsView.axaml Flyout -"] = "EQ preset name box and the Web Remote QR code: panels, not menus",
            ["ColorPickerFlyout.axaml Flyout -"] = "the colour picker panel, not a menu",
        };
        var found = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.axaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !f.Contains("Noctis.Mobile")))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (Match m in Regex.Matches(text, @"<Popup\b[^>]*?(?:x:Name|Name)=""([^""]+)"""))
                found.Add($"{name} Popup {m.Groups[1].Value}");
            foreach (Match m in Regex.Matches(text, @"<Flyout\b([^>]*)>"))
            {
                var cls = Regex.Match(m.Groups[1].Value, @"FlyoutPresenterClasses=""([^""]+)""");
                found.Add($"{name} Flyout {(cls.Success ? cls.Groups[1].Value : "-")}");
            }
        }
        var unknown = found.Where(f => !known.ContainsKey(f)).ToList();
        Assert.True(unknown.Count == 0, "Classify (frost or say why not) in GlassMenusSweep169Tests: " + string.Join(", ", unknown));

        // The tracked option-menu presenters really are tracked.
        foreach (var (file, cls) in new[] { ("LyricsStudioPanel.axaml", "studio-options-pop"), ("MetadataSearchPanel.axaml", "ms-pop") })
        {
            var text = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), file, SearchOption.AllDirectories).First());
            var style = Regex.Match(text, $@"<Style Selector=""FlyoutPresenter\.{Regex.Escape(cls)}"">(.*?)</Style>", RegexOptions.Singleline);
            Assert.True(style.Success && style.Groups[1].Value.Contains("(helpers:GlassMenus.Track)"), $"{file}: FlyoutPresenter.{cls} must set GlassMenus.Track");
        }
    }
}
