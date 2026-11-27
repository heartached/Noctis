using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Plugins › Built-in Features (Discord, Mistery 11-02) and the playlist page's
/// Featured Artists / Suggested rail switch (Discord, DendyAly 11-02): the new settings
/// default on and persist, each row drives the feature's own setting, and switching a
/// feature off removes its UI and stops its work.
/// </summary>
public class BuiltInFeaturesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static SettingsViewModel Settings(IPersistenceService? persistence = null, ILibraryService? lib = null)
        => new(persistence ?? new TestPersistenceService(), lib ?? new FakeLibraryService(), new NoOpPlayHistoryService());

    private static async Task<AppSettings> WaitForSaved(PersistenceService persistence, Func<AppSettings, bool> done)
    {
        var saved = await persistence.LoadSettingsAsync();
        for (var i = 0; i < 200 && !done(saved); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
            saved = await persistence.LoadSettingsAsync();
        }
        return saved;
    }

    private static Track T(string title, string artist) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = title + " LP",
        AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".mp3",
    };

    // ── Settings ──

    [Fact]
    public void NewSwitches_DefaultOn_SoTodaysBehaviourStays()
    {
        var s = new AppSettings();
        Assert.True(s.MiniPlayerEnabled);
        Assert.True(s.AlbumCarouselEnabled);
        Assert.True(s.VisualizerEnabled);
        Assert.True(s.PlaylistShowSideRail);
        // A settings file from before them has no keys: still on.
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(old.MiniPlayerEnabled && old.AlbumCarouselEnabled && old.VisualizerEnabled && old.PlaylistShowSideRail);
    }

    [AvaloniaFact]
    public async Task NewSwitches_LoadAndSave()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            MiniPlayerEnabled = false, AlbumCarouselEnabled = false, VisualizerEnabled = false, PlaylistShowSideRail = false,
        });
        var vm = Settings(persistence);
        await vm.LoadAsync();
        Assert.False(vm.MiniPlayerEnabled);
        Assert.False(vm.AlbumCarouselEnabled);
        Assert.False(vm.VisualizerEnabled);
        Assert.False(vm.PlaylistShowSideRail);

        vm.MiniPlayerEnabled = true;
        vm.AlbumCarouselEnabled = true;
        vm.VisualizerEnabled = true;
        vm.PlaylistShowSideRail = true;

        var saved = await WaitForSaved(persistence,
            s => s.MiniPlayerEnabled && s.AlbumCarouselEnabled && s.VisualizerEnabled && s.PlaylistShowSideRail);
        Assert.True(saved.MiniPlayerEnabled);
        Assert.True(saved.AlbumCarouselEnabled);
        Assert.True(saved.VisualizerEnabled);
        Assert.True(saved.PlaylistShowSideRail);
    }

    // ── The card's rows ──

    [AvaloniaFact]
    public void Rows_ListTheRequestedFeatures_EachLocalized()
    {
        var vm = Settings();
        Assert.Equal(
            new[] { "MiniPlayer", "AnimatedArtwork", "AlbumCarousel", "Visualizer", "NewBadge", "Inbox", "Discord", "Scrobbling", "PlaylistSideRail" },
            vm.BuiltInFeatures.Select(r => r.Key).ToArray());
        foreach (var row in vm.BuiltInFeatures)
        {
            Assert.NotEqual($"Features.{row.Key}.Title", row.Title);
            Assert.NotEqual($"Features.{row.Key}.Line", row.Line);
            Assert.True(Application.Current!.TryGetResource(row.IconKey, null, out _) || row.IconKey.Length > 0);
        }
        foreach (var key in new[] { "Features.Section", "Features.SectionLine", "Features.Scrobbling.NeedsAccount",
                     "Settings.PlaylistSideRail", "Settings.PlaylistSideRailHint", "Playlist.HideSideRail", "Playlist.ShowSideRail" })
            Assert.NotEqual(key, Loc.T(key));
    }

    [AvaloniaFact]
    public void Rows_AreTheExistingSettings_BothWays()
    {
        var vm = Settings();
        var badge = vm.BuiltInFeatures.Single(r => r.Key == "NewBadge");
        var animated = vm.BuiltInFeatures.Single(r => r.Key == "AnimatedArtwork");
        var rail = vm.BuiltInFeatures.Single(r => r.Key == "PlaylistSideRail");
        Assert.True(badge.IsOn);

        badge.IsOn = false;
        Assert.False(vm.PlaylistShowNewBadge);
        animated.IsOn = false;
        Assert.False(vm.EnableAnimatedCovers);

        // Changed elsewhere (Settings › Library, the playlist menu): the row follows.
        var raised = new List<string?>();
        rail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.PlaylistShowSideRail = false;
        Assert.False(rail.IsOn);
        Assert.Contains(nameof(BuiltInFeatureRow.IsOn), raised);
        vm.PlaylistShowNewBadge = true;
        Assert.True(badge.IsOn);
    }

    [AvaloniaFact]
    public void Scrobbling_IsOneSwitchOverBothServices_AndNeedsAnAccount()
    {
        var vm = Settings();
        var row = vm.BuiltInFeatures.Single(r => r.Key == "Scrobbling");
        Assert.False(row.CanToggle);
        Assert.Equal(Loc.T("Features.Scrobbling.NeedsAccount"), row.Line);

        vm.IsLastFmConnected = true;
        Assert.True(row.CanToggle);
        Assert.Equal(Loc.T("Features.Scrobbling.Line"), row.Line);

        row.IsOn = true;
        Assert.True(vm.LastFmScrobblingEnabled);
        Assert.False(vm.ListenBrainzScrobblingEnabled); // not connected: left off

        vm.ListenBrainzScrobblingEnabled = true; // turned on in Integrations
        row.IsOn = false;
        Assert.False(vm.LastFmScrobblingEnabled);
        Assert.False(vm.ListenBrainzScrobblingEnabled);
        Assert.False(row.IsOn);
    }

    [AvaloniaFact]
    public void Scrobbling_ReadsOff_AfterLastFmLogOut()
    {
        var vm = Settings();
        var row = vm.BuiltInFeatures.Single(r => r.Key == "Scrobbling");
        vm.IsLastFmConnected = true;
        vm.LastFmScrobblingEnabled = true;
        Assert.True(row.IsOn);

        vm.LogoutLastFmCommand.Execute(null);

        Assert.False(vm.LastFmScrobblingEnabled);
        Assert.False(row.IsOn);
        Assert.False(row.CanToggle); // no account left to scrobble to
    }

    [AvaloniaFact]
    public void Card_HidesWhileSearchingPlugins()
    {
        var vm = Settings();
        Assert.True(vm.ShowBuiltInFeatures);
        _ = vm.BuiltInFeatures;
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SettingsViewModel.ShowBuiltInFeatures);
        vm.PluginSearchText = "lyrics";
        Assert.False(vm.ShowBuiltInFeatures);
        Assert.True(raised);
    }

    /// <summary>The Plugins search finds a Built-in Feature by name (searching "Discord" used to
    /// hide the card and say no plugin matches).</summary>
    [AvaloniaFact]
    public void Card_SearchShowsTheMatchingRowsOnly()
    {
        var vm = Settings();
        _ = vm.BuiltInFeatures;
        vm.PluginSearchText = "discord";
        Assert.True(vm.ShowBuiltInFeatures);
        Assert.Equal(new[] { "Discord" }, vm.VisibleBuiltInFeatures.Select(r => r.Key));
        Assert.False(vm.ShowNoPluginMatches);
        vm.PluginSearchText = "zzz nothing";
        Assert.False(vm.ShowBuiltInFeatures);
        Assert.True(vm.ShowNoPluginMatches);
        vm.PluginSearchText = "";
        Assert.Same(vm.BuiltInFeatures, vm.VisibleBuiltInFeatures);
    }

    // ── Effects ──

    [AvaloniaFact]
    public void MiniPlayerAndVisualizer_Off_GateThePlayer()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = Settings(persistence, lib);
        vm.SetPlayer(player);
        vm.PlaybackBarShowMiniPlayer = true;
        vm.LyricsVisualizerEnabled = true;
        Assert.True(player.IslandShowMiniPlayer);
        Assert.True(player.LyricsVisualizerEnabled);

        vm.MiniPlayerEnabled = false;
        vm.VisualizerEnabled = false;
        Assert.False(player.IslandShowMiniPlayer);
        Assert.False(player.LyricsVisualizerEnabled);
        // The user's own choices are kept for when the feature returns.
        Assert.True(vm.PlaybackBarShowMiniPlayer);
        Assert.True(vm.LyricsVisualizerEnabled);

        vm.MiniPlayerEnabled = true;
        vm.VisualizerEnabled = true;
        Assert.True(player.IslandShowMiniPlayer);
        Assert.True(player.LyricsVisualizerEnabled);
    }

    [AvaloniaFact]
    public void Visualizer_Off_RemovesTheSidebarEntry_AndPutsItBackAfterPlaylists()
    {
        var lib = new FakeLibraryService();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), lib);
        Assert.True(sidebar.IsVisualizerSectionVisible);

        sidebar.SetVisualizerSectionVisible(false);
        Assert.DoesNotContain(sidebar.NavItems, i => i.Key == "visualizer");

        sidebar.SetVisualizerSectionVisible(true);
        var keys = sidebar.NavItems.Select(i => i.Key).ToList();
        Assert.Equal(keys.IndexOf("playlists") + 1, keys.IndexOf("visualizer"));
        Assert.Equal(keys.IndexOf("visualizer") + 1, keys.IndexOf("lyricsstudio"));
    }

    [AvaloniaFact]
    public void AlbumCarousel_Off_ShowsTheGrid_AndKeepsThePick()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = Settings(persistence, lib);
        var albums = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        albums.SetAlbumViewCommand.Execute("carousel");
        Assert.True(albums.IsCarouselView);

        var raised = new List<string?>();
        albums.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        settings.AlbumCarouselEnabled = false;
        Assert.False(albums.IsCarouselView);
        Assert.True(albums.IsGridView);
        Assert.Contains(nameof(LibraryAlbumsViewModel.IsCarouselView), raised);
        Assert.Equal("carousel", albums.AlbumViewMode);

        settings.AlbumCarouselEnabled = true;
        Assert.True(albums.IsCarouselView);
    }

    /// <summary>A feature switched off leaves no usable settings behind: Player hides the Mini
    /// player section (like the Albums View card), Lyrics closes the visualizer's options under
    /// its greyed switch and says where the feature was turned off.</summary>
    [AvaloniaFact]
    public void SettingsPages_HideTheOptionsOfAFeatureThatIsOff()
    {
        var vm = Settings();
        vm.LyricsVisualizerEnabled = true;
        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 920, Height = 720, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            // Tab pages are realized on their first visit (GitHub #139): visit Lyrics too.
            vm.SelectedSettingsTab = SettingsViewModel.TabLyrics;
            Dispatcher.UIThread.RunJobs();
            var miniTitle = view.FindControl<TextBlock>("MiniPlayerSectionTitle")!;
            var miniCard = view.FindControl<Border>("MiniPlayerSectionCard")!;
            var vizOptions = view.FindControl<Noctis.Controls.CollapsibleContent>("LyricsVisualizerOptions")!;
            var vizHint = view.FindControl<Noctis.Controls.CollapsibleContent>("LyricsVisualizerFeatureOffHint")!;

            Assert.True(miniTitle.IsVisible && miniCard.IsVisible);
            Assert.True(vizOptions.IsOpen);
            Assert.False(vizHint.IsOpen);

            vm.MiniPlayerEnabled = false;
            vm.VisualizerEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(miniTitle.IsVisible);
            Assert.False(miniCard.IsVisible);
            Assert.False(vizOptions.IsOpen);
            Assert.True(vizHint.IsOpen);
            Assert.True(vm.LyricsVisualizerEnabled); // the user's own switch is kept

            vm.MiniPlayerEnabled = true;
            vm.VisualizerEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(miniTitle.IsVisible && miniCard.IsVisible);
            Assert.True(vizOptions.IsOpen);
            Assert.False(vizHint.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Found by clicking in the app: with the Player page on screen, Mini Player off and
    /// on again left "Clicking the artwork" on Lyrics Page for good. The picker echoed the
    /// option it was showing while its list was rebuilt, and that echo was stored over the
    /// user's Mini Player choice.</summary>
    [AvaloniaFact]
    public void MiniPlayerOffAndOnAgain_WithThePlayerPageOnScreen_KeepsTheStoredArtworkChoice()
    {
        var vm = Settings();
        vm.PlaybackBarArtworkClick = "MiniPlayer";
        // As in the app: the feature is already off when the page first shows its picker.
        vm.MiniPlayerEnabled = false;
        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 920, Height = 720, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("MiniPlayer", vm.PlaybackBarArtworkClick);
            Assert.Equal("LyricsPage", vm.SelectedArtworkClickOption?.Value);
            // The switch lives on another page: the Player page stays loaded behind it.
            vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
            Dispatcher.UIThread.RunJobs();

            vm.MiniPlayerEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("MiniPlayer", vm.PlaybackBarArtworkClick);
            Assert.Equal("MiniPlayer", vm.SelectedArtworkClickOption?.Value);

            // And the picker itself shows it once the page is back on screen: it kept
            // showing Lyrics Page while the island already opened the mini player.
            vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
            Dispatcher.UIThread.RunJobs();
            var combo = view.FindControl<ComboBox>("ArtworkClickCombo")!;
            Assert.Equal("MiniPlayer", (combo.SelectedItem as SettingsViewModel.LyricsChoiceOption)?.Value);
            Assert.Equal("MiniPlayer", vm.PlaybackBarArtworkClick);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The rest of Mini Player off: the island's Mini player button switch says why
    /// it is greyed, Appearance › Text Animation drops its Mini Player rows (stored values
    /// kept), and Settings search no longer finds the hidden mini player rows and card.</summary>
    [AvaloniaFact]
    public void MiniPlayerOff_ExplainsTheGreyedButton_HidesItsMarqueeRows_AndSearchSkipsThem()
    {
        var vm = Settings();
        vm.MiniPlayerTitleMarqueeEnabled = true;
        vm.MiniPlayerAlbumMarqueeEnabled = true;
        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 920, Height = 720, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.SelectedSettingsTab = SettingsViewModel.TabAppearance; // realize the page (GitHub #139)
            Dispatcher.UIThread.RunJobs();

            var buttonHint = view.FindControl<Noctis.Controls.CollapsibleContent>("MiniPlayerButtonFeatureOffHint")!;
            var marquee = new Control[]
            {
                view.FindControl<TextBlock>("MiniPlayerMarqueeHeader")!,
                view.FindControl<Grid>("MiniPlayerTitleMarqueeRow")!,
                view.FindControl<Grid>("MiniPlayerArtistMarqueeRow")!,
            };
            var panels = new (string, Control)[]
            {
                ("Appearance", view.FindControl<StackPanel>("AppearanceTabPanel")!),
                ("Player", view.FindControl<StackPanel>("PlayerTabPanel")!),
            };
            var designTitle = Loc.T("Settings.MiniPlayerDesign");
            var marqueeHeader = Loc.T("Settings.MINIPLAYER");
            string MarqueeCardText(Noctis.Helpers.SettingsSearchIndex index) =>
                index.Entries.Single(e => e.Blocks.Contains((TextBlock)marquee[0])).Text;

            Assert.False(buttonHint.IsOpen);
            Assert.All(marquee, c => Assert.True(c.IsVisible));
            var index = Noctis.Helpers.SettingsSearchIndex.Build(panels);
            Assert.NotEmpty(index.Query(designTitle));
            Assert.Contains(marqueeHeader, MarqueeCardText(index));

            vm.MiniPlayerEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(buttonHint.IsOpen);
            Assert.All(marquee, c => Assert.False(c.IsVisible));
            Assert.True(vm.MiniPlayerTitleMarqueeEnabled && vm.MiniPlayerAlbumMarqueeEnabled);
            // SettingsSearchIndex: hidden card (IsNaturallyVisible) and hidden rows (Entry.Text).
            Assert.Empty(index.Query(designTitle));
            Assert.DoesNotContain(marqueeHeader, MarqueeCardText(index));

            vm.MiniPlayerEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(buttonHint.IsOpen);
            Assert.All(marquee, c => Assert.True(c.IsVisible));
            Assert.NotEmpty(index.Query(designTitle));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Playlist rail ──

    private (PlaylistViewModel Vm, FakeLibraryService Lib) PlaylistVm(bool showRail)
    {
        var lib = new FakeLibraryService();
        var a = T("One", "Artist A");
        var b = T("Two", "Artist B");
        var extra = T("Three", "Artist A"); // library-only: suggestion material
        lib.TrackList.AddRange(new[] { a, b, extra });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Mix", TrackIds = new() { a.Id, b.Id } };
        sidebar.Playlists.Add(playlist);
        return (new PlaylistViewModel(playlist, player, lib, persistence, sidebar, null, showRail), lib);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task Rail_Hidden_BuildsNothing_AndShowingItBuildsBoth()
    {
        var (vm, _) = PlaylistVm(showRail: false);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Tracks.Count);
        Assert.Empty(vm.FeaturedArtists);
        Assert.Empty(vm.SuggestedTracks);
        Assert.Equal(Loc.T("Playlist.ShowSideRail"), vm.SideRailMenuLabel);

        vm.ToggleSideRailCommand.Execute(null); // no app action: the page flips itself
        Assert.True(vm.ShowSideRail);
        Assert.Equal(2, vm.FeaturedArtists.Count);
        await WaitUntil(() => vm.SuggestedTracks.Count == 1);
        Assert.Equal(Loc.T("Playlist.HideSideRail"), vm.SideRailMenuLabel);

        vm.ShowSideRail = false;
        Assert.Empty(vm.FeaturedArtists);
        Assert.Empty(vm.SuggestedTracks);
        Assert.False(vm.HasSuggestions);
    }

    [AvaloniaFact]
    public void Rail_Toggle_GoesThroughTheAppSetting()
    {
        var (vm, _) = PlaylistVm(showRail: true);
        bool? asked = null;
        vm.SetSideRailAction(v => asked = v);
        vm.ToggleSideRailCommand.Execute(null);
        Assert.False(asked);
        Assert.True(vm.ShowSideRail); // the app pushes the setting back; nothing flips on its own
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    [AvaloniaFact]
    public void Rail_Hidden_TheTrackListTakesItsWidth()
    {
        EnsureAppStyles();
        var (vm, _) = PlaylistVm(showRail: true);
        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(6);

        var rail = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlaylistSideRail");
        var list = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TrackListWrapper");
        Assert.True(rail.IsVisible);
        var withRail = list.Bounds.Width;
        var railWidth = rail.Bounds.Width;
        Assert.True(railWidth > 200);

        vm.ShowSideRail = false;
        Pump(6);
        Assert.False(rail.IsVisible);
        Assert.Equal(withRail + railWidth, list.Bounds.Width, 1);
        win.Close();
    }
}
