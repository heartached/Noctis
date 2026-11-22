using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Tab Bar (Discord #mobile-beta 11-17: "the songs tab at the bottom bar instead of
/// playlists ... a way to select the tabs at the bottom and maybe even rearrange them"): which tabs
/// stand on the bar and in what order, 2 to 5, Library always on, saved with the phone settings and
/// applied at once; and the selected tab's pill keeps one size on every tab for every bar.
/// </summary>
public class MobileTabBarSettingsTests
{
    private static readonly MobileTab[] Defaults =
        { MobileTab.Home, MobileTab.Favorites, MobileTab.Playlists, MobileTab.Library, MobileTab.Search };

    private static void Pump(Window window, int ticks = 4)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static void PumpUntil(Func<bool> done, double seconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(15);
        }
    }

    // ── The saved list ──

    [Fact]
    public void Decode_EmptyOrMissing_IsTodaysBar_AndTheDefaultSavesAsEmpty()
    {
        Assert.Equal(Defaults, TabBarTabs.Decode(null));
        Assert.Equal(Defaults, TabBarTabs.Decode(Array.Empty<string>()));
        Assert.Empty(TabBarTabs.Encode(Defaults));
        Assert.Equal(new[] { "Home", "Songs", "Library", "Search" },
            TabBarTabs.Encode(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library, MobileTab.Search }));
    }

    [Fact]
    public void Decode_SkipsUnknownAndRepeats_KeepsLibrary_AndHoldsTheLimits()
    {
        // Unknown names, numbers and flag lists skipped; a repeat taken once; case ignored.
        Assert.Equal(new[] { MobileTab.Songs, MobileTab.Library, MobileTab.Albums },
            TabBarTabs.Decode(new[] { "songs", "Radio", "3", "Songs,Albums", "Library", "Songs", " Albums " }));
        // Library missing: added at the end.
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library },
            TabBarTabs.Decode(new[] { "Home", "Songs" }));
        // Too many: the last others go, Library stays.
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Albums, MobileTab.Artists, MobileTab.Library },
            TabBarTabs.Decode(new[] { "Home", "Songs", "Albums", "Artists", "Search", "Favorites", "Library" }));
        // Too few (Library alone): the default bar.
        Assert.Equal(Defaults, TabBarTabs.Decode(new[] { "Library" }));
        Assert.Equal(Defaults, TabBarTabs.Decode(new[] { "Nothing" }));
    }

    [Fact]
    public async Task TheBar_IsSaved_AndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            Assert.Empty(s.MobileTabBar);   // the default
            s.MobileTabBar = new() { "Home", "Songs", "Library", "Search" };
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library, MobileTab.Search }, rig.Shell.BarTabs);

        rig.Shell.SetBarTabs(new[] { MobileTab.Songs, MobileTab.Albums, MobileTab.Library });
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal(new[] { "Songs", "Albums", "Library" }, (await rig.Persistence.LoadSettingsAsync()).MobileTabBar);

        rig.Shell.SetBarTabs(Defaults);
        await rig.Shell.PendingPlayerBarSave;
        Assert.Empty((await rig.Persistence.LoadSettingsAsync()).MobileTabBar);
    }

    /// <summary>A settings file from before the setting (no MobileTabBar field) loads, with today's bar.</summary>
    [Fact]
    public async Task AnOldSettingsFile_WithoutTheField_LoadsTodaysBar()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileGlassTabPill = true;
            await p.SaveSettingsAsync(s);
            var path = Path.Combine(p.DataDirectory, "settings.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            // settings.json keys are camelCase: "MobileTabBar" never matched and the test removed nothing.
            Assert.True(json.Remove("mobileTabBar"), "the key is written camelCase");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.DoesNotContain("mobileTabBar", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        });
        await rig.Shell.InitializeAsync();
        Assert.True(rig.Shell.GlassTabPill);            // the file was read
        Assert.Equal(Defaults, rig.Shell.BarTabs);
        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);
    }

    // ── The shell ──

    [Fact]
    public void RemovingTheShownTab_ShowsTheStartTab_AndBackReturnsThere()
    {
        using var rig = MobileFixtures.MakeRig();
        var shell = rig.Shell;
        shell.SelectTabCommand.Execute(MobileTab.Playlists);
        shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library, MobileTab.Search });
        Assert.Equal(MobileTab.Home, shell.SelectedTab);

        // Without Home the bar's first tab is the start tab.
        shell.SelectTabCommand.Execute(MobileTab.Search);
        shell.SetBarTabs(new[] { MobileTab.Songs, MobileTab.Library });
        Assert.Equal(MobileTab.Songs, shell.StartTab);
        Assert.Equal(MobileTab.Songs, shell.SelectedTab);
        shell.SelectTabCommand.Execute(MobileTab.Library);
        Assert.True(shell.TryHandleBack());
        Assert.Equal(MobileTab.Songs, shell.SelectedTab);
        Assert.False(shell.TryHandleBack());            // the start tab's root: the activity finishes
    }

    [Fact]
    public void SetBarTabs_HoldsTheRules()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs });            // no Library: added
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library }, rig.Shell.BarTabs);
        rig.Shell.SetBarTabs(TabBarTabs.All);                                       // eight: cut to five
        Assert.Equal(5, rig.Shell.BarTabs.Count);
        Assert.Contains(MobileTab.Library, rig.Shell.BarTabs);
    }

    /// <summary>The library's Refreshed handlers: one per page that re-reads on every refresh.</summary>
    private static int RefreshListeners(LibraryViewModel library) =>
        (typeof(LibraryViewModel).GetField(nameof(LibraryViewModel.Refreshed),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(library) as Delegate)
        ?.GetInvocationList().Length ?? 0;

    /// <summary>A tab taken off the bar stops re-reading the library on every refresh (its root was
    /// kept, listening, until the app closed), and comes back fresh when it is put back.</summary>
    [Fact]
    public void ATabTakenOffTheBar_StopsRefreshing_AndComesBackFresh()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("A"), MobileFixtures.Song("B") });
        var shell = rig.Shell;
        _ = shell.FavoritesRoot;                        // the shell view binds both at start
        _ = shell.PlaylistsRoot;
        var start = RefreshListeners(shell.Library);
        shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Albums, MobileTab.Artists, MobileTab.Library });
        var songs = shell.SongsRoot!;
        Assert.NotNull(shell.AlbumsRoot);
        Assert.NotNull(shell.ArtistsRoot);
        Assert.Null(shell.FavoritesRoot);               // off the bar: not kept, not made
        Assert.Null(shell.PlaylistsRoot);
        Assert.Equal(start + 1, RefreshListeners(shell.Library));   // three roots in, two out

        shell.SetBarTabs(Defaults);
        Assert.Null(shell.SongsRoot);
        Assert.Null(shell.AlbumsRoot);
        Assert.Null(shell.ArtistsRoot);
        _ = shell.FavoritesRoot;
        _ = shell.PlaylistsRoot;
        Assert.Equal(start, RefreshListeners(shell.Library));

        shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library });
        Assert.NotSame(songs, shell.SongsRoot);         // a new root, reading the library now
        Assert.Equal(2, shell.SongsRoot!.Songs.Count);
    }

    /// <summary>Playlists off the bar stays one tap away: the Library tab's Playlists row (on by default).</summary>
    [Fact]
    public void APageOffTheBar_IsStillALibraryRow()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library, MobileTab.Search });
        var row = rig.Shell.LibraryRows.Rows.Single(r => r.Kind == LibraryRowKind.Playlists);
        Assert.True(row.IsShown);
        rig.Shell.LibraryRows.ActivateCommand.Execute(row);
        Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        // Favorites too (a row, off by default: switched on under the Library's Edit).
        Assert.Contains(rig.Shell.LibraryRows.Rows, r => r.Kind == LibraryRowKind.Favorites);
    }

    [AvaloniaFact]
    public void ANewBar_AppliesLive_ButtonsColumnsAndTheSongsRoot()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window);
        var bar = view.FindControl<Grid>("TabBar")!;
        Assert.Equal(5, bar.ColumnDefinitions.Count);
        Assert.False(view.FindControl<Button>("SongsTab")!.IsVisible);
        Assert.Null(rig.Shell.SongsRoot);               // not made before its tab is on the bar

        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library, MobileTab.Search });
        Pump(window);
        Assert.Equal(4, bar.ColumnDefinitions.Count);
        Assert.False(view.FindControl<Button>("PlaylistsTab")!.IsVisible);
        Assert.False(view.FindControl<Button>("FavoritesTab")!.IsVisible);
        var songsTab = view.FindControl<Button>("SongsTab")!;
        Assert.True(songsTab.IsVisible);
        Assert.Equal(1, Grid.GetColumn(songsTab));
        Assert.Equal(2, Grid.GetColumn(view.FindControl<Button>("LibraryTab")!));
        Assert.Equal(3, Grid.GetColumn(view.FindControl<Button>("SearchTab")!));

        songsTab.Command!.Execute(songsTab.CommandParameter);
        Pump(window);
        Assert.Equal(MobileTab.Songs, rig.Shell.SelectedTab);
        Assert.True(view.FindControl<Grid>("SongsRoot")!.IsEffectivelyVisible);
        var page = view.GetVisualDescendants().OfType<SongListPage>().First(p => p.IsEffectivelyVisible);
        Assert.Same(rig.Shell.SongsRoot, page.DataContext);
        Assert.Equal(3, rig.Shell.SongsRoot!.Songs.Count);
        Assert.False(MobileFixtures.Named<Control>(page, "Header").IsVisible);   // the tab's own header instead
        Assert.True(view.FindControl<ProfileButton>("SongsProfile")!.IsEffectivelyVisible);   // Settings in reach

        // Albums and Artists host the same way.
        rig.Shell.SetBarTabs(new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Library });
        Pump(window);
        Assert.Equal(MobileTab.Albums, rig.Shell.SelectedTab);   // Songs left: the start tab (the first)
        Assert.True(view.FindControl<Grid>("AlbumsRoot")!.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<AlbumGridPage>(), p => p.IsEffectivelyVisible);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Artists);
        Pump(window);
        var artists = view.GetVisualDescendants().OfType<ArtistListPage>().First(p => p.IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Control>(artists, "Header").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void WithoutSearchOnTheBar_TheFoldedBarHasNoSearchButton()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window);
        rig.Shell.IsTabBarCollapsed = true;
        Pump(window);
        Assert.True(view.FindControl<Avalonia.Controls.Control>("SearchBubble")!.IsHitTestVisible);
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Songs, MobileTab.Library });
        rig.Shell.IsTabBarCollapsed = true;
        Pump(window);
        Assert.False(view.FindControl<Avalonia.Controls.Control>("SearchBubble")!.IsHitTestVisible);
        window.Close();
    }

    // ── The editor ──

    [Fact]
    public void Editor_LibraryLocked_MinAndMaxGuarded_MoveAndReset()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new TabBarSettingsViewModel(rig.Shell);
        Assert.Equal(TabBarTabs.All.Count, vm.Choices.Count);
        Assert.Equal(Defaults, vm.Choices.Take(5).Select(c => c.Tab));
        Assert.True(vm.IsDefault);
        Assert.False(Choice(MobileTab.Library).CanToggle);
        // Five on: every one on but Library can still go (the S23 repro's "stuck" switch was the
        // failed layout, not this state).
        Assert.All(Defaults.Where(t => t != MobileTab.Library), t => Assert.True(Choice(t).CanToggle));
        // Five on: nothing more switches on, and a switch forced on flips back.
        Assert.False(Choice(MobileTab.Songs).CanToggle);
        Choice(MobileTab.Songs).IsOn = true;
        Assert.False(Choice(MobileTab.Songs).IsOn);
        Assert.Equal(Defaults, rig.Shell.BarTabs);

        // Playlists off, Songs on: the tester's bar.
        Choice(MobileTab.Playlists).IsOn = false;
        Choice(MobileTab.Songs).IsOn = true;
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Library, MobileTab.Search, MobileTab.Songs }, rig.Shell.BarTabs);
        // Dragged up to the third place.
        vm.Move(vm.Choices.IndexOf(Choice(MobileTab.Songs)), 2);
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Songs, MobileTab.Library, MobileTab.Search }, rig.Shell.BarTabs);

        // Down to two: the last other one cannot go, Library cannot either.
        foreach (var tab in new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Search }) Choice(tab).IsOn = false;
        Assert.Equal(new[] { MobileTab.Songs, MobileTab.Library }, rig.Shell.BarTabs);
        Assert.False(Choice(MobileTab.Songs).CanToggle);
        Choice(MobileTab.Songs).IsOn = false;
        Assert.True(Choice(MobileTab.Songs).IsOn);
        Assert.Equal(new[] { MobileTab.Songs, MobileTab.Library }, rig.Shell.BarTabs);

        vm.ResetCommand.Execute(null);
        Assert.Equal(Defaults, rig.Shell.BarTabs);
        Assert.True(vm.IsDefault);
        Assert.Equal(Defaults, vm.Choices.Take(5).Select(c => c.Tab));
        Assert.All(vm.Choices.Skip(5), c => Assert.False(c.IsOn));

        TabBarChoice Choice(MobileTab tab) => vm.Choices.Single(c => c.Tab == tab);
    }

    /// <summary>A drop from a row index that went stale (the rows rebuilt under a drag) or past the
    /// list does nothing harmful: clamped or ignored, never thrown.</summary>
    [Fact]
    public void Editor_Move_OutOfRange_IsClampedOrIgnored()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new TabBarSettingsViewModel(rig.Shell);
        vm.Move(-1, 2);
        vm.Move(vm.Choices.Count, 0);
        vm.Move(99, 99);
        Assert.Equal(Defaults, rig.Shell.BarTabs);
        vm.Move(0, 99);                                     // Home to the very end: off-bar rows' place
        Assert.Equal(TabBarTabs.All.Count, vm.Choices.Count);
        Assert.Equal(MobileTab.Home, vm.Choices[^1].Tab);
        Assert.Equal(new[] { MobileTab.Favorites, MobileTab.Playlists, MobileTab.Library, MobileTab.Search, MobileTab.Home }, rig.Shell.BarTabs);
    }

    [AvaloniaFact]
    public async Task Editor_InSettings_SwitchesAndHandleDrag_MoveTheBar()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.InitializeAsync();
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            Assert.True(MobileFixtures.Named<Button>(page, "TabBarRow").IsEffectivelyVisible);
            vm.OpenPaneCommand.Execute(SettingsPane.TabBar);
            MobileFixtures.Land(window);
            Pump(window);
            Assert.Equal("Tab Bar", vm.HeaderTitle);
            var pane = MobileFixtures.Named<TabBarPane>(page, "TabBarPane");
            Assert.True(pane.IsEffectivelyVisible);
            var switches = pane.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name == "TabSwitch").ToList();
            Assert.Equal(TabBarTabs.All.Count, switches.Count);
            Assert.All(switches, s => Assert.True(s.Bounds.Height >= 52));
            // Hairlines between the rows, none above the first (the Settings lists' rule).
            Assert.Equal(TabBarTabs.All.Count - 1, pane.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("row-separator") && b.IsEffectivelyVisible));
            Assert.False(switches[3].IsEnabled);           // Library
            Assert.False(switches[5].IsEnabled);           // Songs: five already on

            switches[2].IsChecked = false;                  // Playlists off
            Pump(window);
            Assert.Equal(new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Library, MobileTab.Search }, rig.Shell.BarTabs);
            var songsSwitch = pane.GetVisualDescendants().OfType<ToggleButton>()
                .Single(t => t.Name == "TabSwitch" && ((TabBarChoice)t.DataContext!).Tab == MobileTab.Songs);
            Assert.True(songsSwitch.IsEnabled);
            songsSwitch.IsChecked = true;
            Pump(window);
            Assert.Equal(MobileTab.Songs, rig.Shell.BarTabs[^1]);

            // The ≡ handle: Songs (row 5) dragged up three rows lands at row 2, the bar's third tab.
            var rowHeight = pane.GetVisualDescendants().OfType<ToggleButton>().First().Bounds.Height;
            var from = TabBarChoices(pane).IndexOf(MobileTab.Songs);
            pane.CommitDrag(from, -(from - 2) * rowHeight, rowHeight);
            Pump(window);
            Assert.Equal(new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Songs, MobileTab.Library, MobileTab.Search }, rig.Shell.BarTabs);
            // The bar under the sheet followed at once.
            Assert.Equal(2, Grid.GetColumn(view.FindControl<Button>("SongsTab")!));
            await rig.Shell.PendingPlayerBarSave;
            Assert.Equal(new[] { "Home", "Favorites", "Songs", "Library", "Search" }, (await rig.Persistence.LoadSettingsAsync()).MobileTabBar);

            MobileFixtures.Named<Button>(pane, "TabBarResetButton").Command!.Execute(null);
            Assert.Equal(Defaults, rig.Shell.BarTabs);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }

        static System.Collections.Generic.List<MobileTab> TabBarChoices(TabBarPane p) =>
            ((TabBarSettingsViewModel)p.DataContext!).Choices.Select(c => c.Tab).ToList();
    }

    /// <summary>Beta 5 tester: "Icons should be filled when they're on the tab bar". Every tab the bar
    /// can hold draws its Tab*FilledIcon there, in the folded circle and in the editor's row chip.</summary>
    [AvaloniaFact]
    public void EveryTabOnTheBar_DrawsItsFilledGlyph()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window);
        var circle = view.FindControl<Button>("CollapsedTabButton")!;
        foreach (var tab in TabBarTabs.All)
        {
            var key = "Tab" + tab + "FilledIcon";
            Assert.True(view.TryFindResource(key, out var glyph), key);
            var button = view.FindControl<Button>(ButtonName(tab))!;
            Assert.Same(glyph, Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(button).OfType<PathIcon>().Single().Data);
            Assert.Contains(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(circle).OfType<PathIcon>(), i => ReferenceEquals(i.Data, glyph));
        }
        window.Close();
    }

    // ── Beta 5 testers: a blank editor card, and the bar left laid out for five with four tabs ──

    /// <summary>Favorites switched off live (the testers' bar: Home, Playlists, Library, Search): the four
    /// buttons share the whole bar, the last one ends at the bar's end, no empty slot after it.</summary>
    [AvaloniaFact]
    public void ATabTakenOffLive_TheOthersShareTheWholeBar()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window);
        var row = view.FindControl<Panel>("TabRow")!;
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Playlists, MobileTab.Library, MobileTab.Search });
        Pump(window);
        var bar = row.Bounds.Width;
        var tabs = rig.Shell.BarTabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            var button = view.FindControl<Button>(ButtonName(tabs[i]))!;
            var left = button.TranslatePoint(new Point(0, 0), row)!.Value.X;
            Assert.True(Math.Abs(button.Bounds.Width - ShellView.TabWidth(bar, 4)) <= 1.01, $"tab {tabs[i]}: width {button.Bounds.Width}, expected {ShellView.TabWidth(bar, 4)}");
            Assert.True(Math.Abs(left - (ShellView.TabEdge + i * ShellView.TabWidth(bar, 4))) <= 1.01, $"tab {tabs[i]}: left {left}");
        }
        // And back to five, then down to three: the bar follows every count.
        rig.Shell.SetBarTabs(Defaults);
        Pump(window);
        Assert.True(Math.Abs(ShellView.TabWidth(bar, 5) - view.FindControl<Button>("SearchTab")!.Bounds.Width) <= 1.01);
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Library, MobileTab.Search });
        Pump(window);
        Assert.True(Math.Abs(ShellView.TabWidth(bar, 3) - view.FindControl<Button>("SearchTab")!.Bounds.Width) <= 1.01);
        window.Close();
    }

    /// <summary>
    /// The beta 5 crash, as reproduced on an S23 (Liquid Glass pill on, bar Home, Playlists, Library,
    /// Search): Favorites on (it goes fifth, its hidden button moves to column 4), then off again. The
    /// bar went back to four columns but the hidden Favorites button stayed in column 4, and as no
    /// shown button changed column the Grid kept its cells: the next measure read column 4 of four,
    /// threw ArgumentOutOfRangeException (Grid.GetMeasureSizeForRange) and every layout pass after it
    /// did too: the bar stayed laid out for five, the switch looked stuck, and the editor's rows,
    /// rebuilt on the next change, were never measured (the blank card).
    /// </summary>
    [AvaloniaFact]
    public async Task FavoritesOnThenOff_FromTheFourTabBar_KeepsLayingOut()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.InitializeAsync();
        rig.Shell.GlassTabPill = true;
        rig.Shell.SetBarTabs(new[] { MobileTab.Home, MobileTab.Playlists, MobileTab.Library, MobileTab.Search });
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            vm.OpenPaneCommand.Execute(SettingsPane.TabBar);
            MobileFixtures.Land(window);
            Pump(window);
            var pane = MobileFixtures.Named<TabBarPane>(page, "TabBarPane");
            var row = view.FindControl<Panel>("TabRow")!;

            Switch(MobileTab.Favorites).IsChecked = true;       // (1) five on
            Pump(window);
            Assert.Equal(MobileTab.Favorites, rig.Shell.BarTabs[^1]);
            Switch(MobileTab.Favorites).IsChecked = false;      // (2) off again: this layout threw
            Pump(window);
            Assert.Equal(new[] { MobileTab.Home, MobileTab.Playlists, MobileTab.Library, MobileTab.Search }, rig.Shell.BarTabs);
            Assert.True(Switch(MobileTab.Favorites).IsEnabled);  // four on: it can come back
            var bar = row.Bounds.Width;
            var search = view.FindControl<Button>("SearchTab")!;
            Assert.True(Math.Abs(search.Bounds.Width - ShellView.TabWidth(bar, 4)) <= 1.01, $"Search {search.Bounds.Width} wide, a quarter is {ShellView.TabWidth(bar, 4)}");

            Switch(MobileTab.Playlists).IsChecked = false;      // (4) the rows rebuild: still drawn
            Pump(window);
            var model = (TabBarSettingsViewModel)pane.DataContext!;
            var list = MobileFixtures.Named<ItemsControl>(pane, "TabBarList");
            for (var i = 0; i < model.Choices.Count; i++)
                Assert.True(list.ContainerFromIndex(i) is { Bounds.Height: >= 52 }, $"row {i} not laid out");
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }

        ToggleButton Switch(MobileTab tab) => MobileFixtures.Find<SettingsPage>(view).GetVisualDescendants().OfType<ToggleButton>()
            .Single(t => t.Name == "TabSwitch" && ((TabBarChoice)t.DataContext!).Tab == tab);
    }

    /// <summary>The editor's rows after switches flip and a drag lands, and after the page is left and
    /// opened again: one drawn row per tab, each with its label, none blank.</summary>
    [AvaloniaFact]
    public async Task Editor_RowsStayDrawn_AfterTogglesDragsAndReopening()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.InitializeAsync();
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            vm.OpenPaneCommand.Execute(SettingsPane.TabBar);
            MobileFixtures.Land(window);
            Pump(window);
            var pane = MobileFixtures.Named<TabBarPane>(page, "TabBarPane");
            AssertRowsDrawn("opened");

            Switch(MobileTab.Favorites).IsChecked = false;      // the testers' change
            Pump(window);
            AssertRowsDrawn("Favorites off");
            Switch(MobileTab.Songs).IsChecked = true;
            Pump(window);
            AssertRowsDrawn("Songs on");
            Switch(MobileTab.Songs).IsChecked = false;          // two quick toggles, a frame apart
            Pump(window, 1);
            Switch(MobileTab.Favorites).IsChecked = true;
            Pump(window);
            AssertRowsDrawn("two toggles");

            var rowHeight = Switch(MobileTab.Home).Bounds.Height;
            pane.CommitDrag(0, 3 * rowHeight, rowHeight);
            Pump(window);
            AssertRowsDrawn("dragged");

            Assert.True(rig.Shell.TryHandleBack());            // back to Settings' main list
            Pump(window);
            vm.OpenPaneCommand.Execute(SettingsPane.TabBar);
            MobileFixtures.Land(window);
            Pump(window);
            AssertRowsDrawn("reopened");

            void AssertRowsDrawn(string when)
            {
                var model = (TabBarSettingsViewModel)pane.DataContext!;
                var list = MobileFixtures.Named<ItemsControl>(pane, "TabBarList");
                var rows = pane.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(t => t.Name == "TabSwitch" && t.IsEffectivelyVisible).ToList();
                Assert.True(model.Choices.Count == rows.Count, $"{when}: {rows.Count} rows drawn for {model.Choices.Count} tabs");
                for (var i = 0; i < model.Choices.Count; i++)
                {
                    var container = list.ContainerFromIndex(i);
                    Assert.True(container != null, $"{when}: row {i} has no container");
                    Assert.True(container!.Bounds.Height >= 52, $"{when}: row {i} {container.Bounds.Height} tall");
                    Assert.Same(model.Choices[i], container.DataContext);
                    var label = container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("row-label"));
                    Assert.True(label is { IsEffectivelyVisible: true } && label.Text == model.Choices[i].Title && label.Bounds.Width > 0, $"{when}: row {i} label blank");
                    Assert.True(container.Opacity > 0.99 && container.RenderTransform == null, $"{when}: row {i} opacity {container.Opacity} transform {container.RenderTransform}");
                    // The row chip draws the bar's own filled glyph.
                    Assert.True(pane.TryFindResource("Tab" + model.Choices[i].Tab + "FilledIcon", out var glyph));
                    Assert.Same(glyph, container.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("tab-glyph")).Data);
                }
            }

            ToggleButton Switch(MobileTab tab) => pane.GetVisualDescendants().OfType<ToggleButton>()
                .Single(t => t.Name == "TabSwitch" && ((TabBarChoice)t.DataContext!).Tab == tab);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    // ── The pill: one size on every tab, any bar (tester 11-15: "smaller at the ends") ──

    public static TheoryData<int, double> Bars()
    {
        var data = new TheoryData<int, double>();
        foreach (var n in new[] { 2, 3, 4, 5 })
            foreach (var w in new[] { 360.0, 412.0 })
                data.Add(n, w);
        return data;
    }

    private static readonly MobileTab[] FiveTabs = Defaults;

    private static MobileTab[] BarOf(int n) => n switch
    {
        2 => new[] { MobileTab.Home, MobileTab.Library },
        3 => new[] { MobileTab.Home, MobileTab.Library, MobileTab.Search },
        4 => new[] { MobileTab.Home, MobileTab.Favorites, MobileTab.Library, MobileTab.Search },
        _ => FiveTabs,
    };

    private static string ButtonName(MobileTab tab) => tab + "Tab";

    /// <summary>French labels (Accueil, Favoris, Playlists, Bibliothèque, Recherche): at rest on each
    /// tab the pill is one width and height, centred on the tab's button, inside the bar's ends.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Bars))]
    public void ThePill_IsOneSize_OnEveryTab_AtRest(int count, double screen)
    {
        Noctis.Localization.Loc.Instance.SetCulture("fr");
        try
        {
            using var rig = MobileFixtures.MakeRig();
            rig.Shell.SetBarTabs(BarOf(count));
            var window = MobileFixtures.Mount(rig.Shell, out var view, width: screen);
            Pump(window);
            var row = view.FindControl<Panel>("TabRow")!;
            var pill = view.FindControl<GlassDroplet>("SelectionPill")!;
            var host = view.FindControl<Panel>("SelectionPillHost")!;
            var sizes = new System.Collections.Generic.List<Size>();
            var tabs = BarOf(count);
            for (var i = 0; i < count; i++)
            {
                rig.Shell.SelectTabCommand.Execute(tabs[i]);
                PumpUntil(() => pill.Lift == 0 && Math.Abs((host.RenderTransform?.Value.M31 ?? 0) - ShellView.SelectionPillX(row.Bounds.Width, i, count)) < 0.1);
                window.UpdateLayout();
                var bar = row.Bounds.Width;
                var left = host.RenderTransform!.Value.M31;
                sizes.Add(new Size(pill.Width, pill.Height));
                var button = view.FindControl<Button>(ButtonName(tabs[i]))!;
                var buttonCentre = button.TranslatePoint(new Point(button.Bounds.Width / 2, 0), row)!.Value.X;
                Assert.True(Math.Abs(buttonCentre - (left + pill.Width / 2)) <= 1.01, $"{count} tabs, tab {i} at {screen}: tab centre {buttonCentre}, pill centre {left + pill.Width / 2}");
                Assert.True(Math.Abs(button.Bounds.Width - ShellView.TabWidth(bar, count)) <= 1.01, $"{count} tabs, tab {i}: button {button.Bounds.Width}");
                Assert.True(left >= 4 - 0.01 && left + pill.Width <= bar - 4 + 0.01, $"{count} tabs, tab {i} at {screen}: {left}..{left + pill.Width} in {bar}");
            }
            Assert.All(sizes, s => Assert.Equal(sizes[0], s));
            Assert.Equal(ShellView.TabWidth(row.Bounds.Width, count) + 4, sizes[0].Width, 3);
            window.Close();
        }
        finally
        {
            Noctis.Localization.Loc.Instance.SetCulture("en");
        }
    }

    /// <summary>Held and dragged to the first and the last tab (and past them), the pill keeps its
    /// resting width and stops at the end tab's resting place.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Bars))]
    public void ThePill_KeepsItsSize_DraggedToTheFirstAndLastTab(int count, double screen)
    {
        Noctis.Localization.Loc.Instance.SetCulture("fr");
        try
        {
            using var rig = MobileFixtures.MakeRig();
            var tabs = BarOf(count);
            rig.Shell.SetBarTabs(tabs);
            var window = MobileFixtures.Mount(rig.Shell, out var view, width: screen);
            Pump(window, 10);
            var row = view.FindControl<Panel>("TabRow")!;
            var pill = view.FindControl<GlassDroplet>("SelectionPill")!;
            var host = view.FindControl<Panel>("SelectionPillHost")!;
            var rest = pill.Width;
            var first = view.FindControl<Button>(ButtonName(tabs[0]))!;
            var y = first.TranslatePoint(new Point(0, first.Bounds.Height / 2), window)!.Value.Y;
            var rowLeft = row.TranslatePoint(new Point(0, 0), window)!.Value.X;
            var bar = row.Bounds.Width;

            var start = rowLeft + ShellView.TabCentre(bar, 0, count);
            window.MouseDown(new Point(start, y), MouseButton.Left, RawInputModifiers.None);
            // Right to past the last tab, then back left past the first.
            foreach (var x in Enumerable.Range(1, 60).Select(k => start + k * 10.0).Concat(Enumerable.Range(1, 80).Select(k => rowLeft + bar + 20 - k * 10.0)))
            {
                window.MouseMove(new Point(x, y), RawInputModifiers.LeftMouseButton);
                Pump(window, 1);
                Assert.True(view.IsDraggingTabPill || x < start + 20, $"not dragging at {x}");
                if (!view.IsDraggingTabPill) continue;
                Assert.Equal(rest, pill.Width, 3);
                var left = host.RenderTransform!.Value.M31;
                Assert.True(left >= ShellView.SelectionPillX(bar, 0, count) - 0.01 && left <= ShellView.SelectionPillX(bar, count - 1, count) + 0.01,
                    $"{count} tabs at {screen}: pill at {left} for finger {x - rowLeft}");
                if (x - rowLeft >= bar) Assert.Equal(ShellView.SelectionPillX(bar, count - 1, count), left, 3);
                if (x - rowLeft <= 0) Assert.Equal(ShellView.SelectionPillX(bar, 0, count), left, 3);
            }
            window.MouseUp(new Point(rowLeft - 10, y), MouseButton.Left, RawInputModifiers.None);
            Pump(window);
            Assert.Equal(tabs[0], rig.Shell.SelectedTab);
            Assert.Equal(rest, pill.Width, 3);
            window.Close();
        }
        finally
        {
            Noctis.Localization.Loc.Instance.SetCulture("en");
        }
    }
}
