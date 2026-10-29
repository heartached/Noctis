using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Plugins as a card grid (owner 10-29): "Official Noctis plugins" (listed under author
/// Noctis, always the verified mark) and "Community plugins" (other listings and whatever was
/// installed that isn't the verified official copy), a search box over both, a page per plugin,
/// icons with a letter-tile fallback, and the layout down to a narrow pane.
/// </summary>
public class PluginsGridTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private const string OfficialUrl = "https://github.com/heartached/Noctis/releases/download/plugin-zip-v1/p.zip";
    private const string CommunityUrl = "https://github.com/someone/listed/releases/download/v1.0.0/listed.zip";
    private const string OfficialId = "dev.test.zip";

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed record Mounted(SettingsViewModel Vm, PluginHost Host, List<ConfirmationRequest> Dialogs,
        TestPersistenceService Persistence, PluginCatalogTests.FakeHandler Server) : IDisposable
    {
        public void Dispose() => Persistence.Dispose();
    }

    /// <summary>A view model on a sandboxed host with a fake GitHub; no built-in list.</summary>
    private Mounted Mount(bool withClient = true)
    {
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        var dialogs = new List<ConfirmationRequest>();
        vm.ShowPluginDialog = r => { dialogs.Add(r); return Task.FromResult(new ConfirmationResult(true, false)); };
        _box.Settings.CommunityPluginsEnabled = true;
        var host = _box.NewHost(appVersion: "1.6.6");
        host.LoadAll();
        vm.Plugins = host;
        var server = new PluginCatalogTests.FakeHandler();
        if (withClient)
            vm.PluginCatalogClient = new PluginCatalogClient(new HttpClient(server), () => null) { Embedded = () => PluginCatalog.Empty };
        return new Mounted(vm, host, dialogs, persistence, server);
    }

    private string Zip(string name, string id, string version, string author, byte dll = 1, string? extra = null)
        => _box.WriteZip(name,
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: id, name: "Test Plugin", version: version, author: author, extra: extra))),
            ("Test.Plugin.dll", new[] { dll }));

    /// <summary>Lists the official zip (author Noctis) and one community plugin (author Jafezy JD).</summary>
    private static void Publish(Mounted m, byte[] officialZip, string version = "1.0.0")
    {
        m.Server[OfficialUrl] = officialZip;
        m.Server[PluginCatalog.DefaultUrl] = System.Text.Encoding.UTF8.GetBytes(PluginCatalogTests.Index(
            PluginCatalogTests.Entry(id: OfficialId, name: "Test Plugin", version: version, download: OfficialUrl,
                sha256: PluginCatalogTests.Hash(officialZip), size: officialZip.LongLength, minAppVersion: null),
            PluginCatalogTests.Entry(id: "dev.community.listed", name: "Listed Town", version: "1.0.0", download: CommunityUrl,
                minAppVersion: null, extra: new (string, object?)[] { ("author", "Jafezy JD"), ("description", "Albums wander a plaza.") })));
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Lets the page slide (0.28 s) land before measuring where things are.</summary>
    private static void Settle(Window window, int ms = 450)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Pump();
            Thread.Sleep(5);
        }
        window.UpdateLayout();
    }

    // ── Sections ──

    [AvaloniaFact]
    public async Task Sections_OfficialAreTheNoctisListings_CommunityIsEveryoneElse()
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        _box.WriteFolder("dev.test.hand", PluginSandbox.Manifest(id: "dev.test.hand", name: "Hand Made", author: "Someone"), ("Test.Plugin.dll", new byte[] { 1 }));
        _box.WriteFolder("dev.test.anon", PluginSandbox.Manifest(id: "dev.test.anon", name: "Anon", author: ""), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();

        await m.Vm.LoadOfficialPluginsAsync();

        var official = Assert.Single(m.Vm.OfficialCards);
        Assert.Equal(OfficialId, official.Id);
        Assert.True(official.IsOfficial);
        // Author "Noctis" is the trust check; the card credits the maker (owner 10-29).
        Assert.Equal(Loc.T("Plugins.Get.By", PluginTrust.OfficialMaker), official.ByLine);

        Assert.Equal(new[] { "dev.community.listed", "dev.test.anon", "dev.test.hand" }, m.Vm.CommunityCards.Select(c => c.Id));
        Assert.All(m.Vm.CommunityCards, c => Assert.False(c.IsOfficial));

        // The Community switch off hides the whole Community section (owner 10-29).
        Assert.True(m.Vm.ShowCommunitySection);
        m.Host.SetCommunityPluginsEnabled(false);
        Assert.False(m.Vm.ShowCommunitySection);
        Assert.True(m.Vm.ShowOfficialSection);
        Assert.Equal(Loc.T("Plugins.Get.By", "Jafezy JD"), m.Vm.CommunityCards[0].ByLine);
        Assert.Equal(Loc.T("Plugins.Card.NoAuthor"), m.Vm.CommunityCards.Single(c => c.Id == "dev.test.anon").ByLine);
        Assert.Equal(Loc.T("Plugins.Get.By", "Someone"), m.Vm.CommunityCards.Single(c => c.Id == "dev.test.hand").ByLine);
        Assert.Equal("1", m.Vm.OfficialCountText);
        Assert.Equal("3", m.Vm.CommunityCountText);
        Assert.False(m.Vm.ShowCommunityEmpty);

        // Screen readers hear what kind of plugin it is.
        Assert.Equal(Loc.T("Plugins.Card.A11yOfficial", "Test Plugin"), official.AccessibleName);
        Assert.Equal(Loc.T("Plugins.Card.A11yCommunity", "Listed Town", "Jafezy JD"), m.Vm.CommunityCards[0].AccessibleName);
    }

    [AvaloniaFact]
    public void Sections_WithNothingInstalled_CommunitySaysWhereTheyComeFrom()
    {
        using var m = Mount(withClient: false);
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins; // the built-in list: Mixxx

        Assert.Contains(m.Vm.OfficialCards, c => c.Id == "dev.noctis.plugins.mixxx" && c.IsOfficial);
        Assert.Empty(m.Vm.CommunityCards);
        Assert.True(m.Vm.ShowCommunityEmpty);
        Assert.True(m.Vm.ShowCommunitySection);
    }

    [AvaloniaFact]
    public async Task VerifiedMark_ShowsOnOfficialCardsAndPagesOnly()
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        _box.WriteFolder("dev.test.hand", PluginSandbox.Manifest(id: "dev.test.hand", name: "Hand Made", author: "Noctis"), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        await m.Vm.LoadOfficialPluginsAsync();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;

        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Pump();
            window.UpdateLayout();

            static List<Viewbox> Marks(Control root) => root.GetVisualDescendants().OfType<Viewbox>()
                .Where(v => v.Classes.Contains("verified-mark") && v.IsEffectivelyVisible).ToList();

            var officialGrid = view.FindControl<ItemsControl>("OfficialPluginsGrid")!;
            var communityGrid = view.FindControl<ItemsControl>("CommunityPluginsGrid")!;
            Assert.Equal(m.Vm.OfficialCards.Count, Marks(officialGrid).Count);
            // A community plugin whose plugin.json says "author": "Noctis" still gets no mark.
            Assert.Equal(2, m.Vm.CommunityCards.Count);
            Assert.Empty(Marks(communityGrid));

            m.Vm.OpenPluginCardCommand.Execute(m.Vm.OfficialCards[0]);
            Pump();
            window.UpdateLayout();
            Assert.Single(Marks(view.FindControl<StackPanel>("PluginDetailPage")!));

            m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.hand"));
            Pump();
            window.UpdateLayout();
            Assert.Empty(Marks(view.FindControl<StackPanel>("PluginDetailPage")!));
        }
        finally { window.Close(); }
    }

    // ── Trust: the mark can't be claimed ──

    [AvaloniaFact]
    public async Task Spoof_AZipWithAnOfficialIdAndAuthorNoctis_StaysCommunity_UntilReplacedByTheListedZip()
    {
        using var m = Mount();
        var officialZip = Zip("official.zip", OfficialId, "1.0.0", "Noctis");
        Publish(m, File.ReadAllBytes(officialZip));
        await m.Vm.LoadOfficialPluginsAsync();

        // Same id, version and author, even an "official": true of its own: different bytes.
        var spoof = Zip("spoof.zip", OfficialId, "1.0.0", "Noctis", dll: 66, extra: "  \"official\": true");
        Assert.True(await m.Vm.InstallPluginPackageAsync(spoof));

        var listing = Assert.Single(m.Vm.OfficialPlugins, o => o.Entry.Id == OfficialId);
        Assert.Null(listing.Installed);
        Assert.True(listing.HasUnverifiedCopy);
        Assert.Equal(Loc.T("Plugins.Get.Replace"), listing.ButtonText);
        Assert.True(listing.CanClick && listing.IsPrimary && listing.ShowButton);
        var spoofCard = Assert.Single(m.Vm.CommunityCards, c => c.Id == OfficialId);
        Assert.False(spoofCard.IsOfficial);
        Assert.False(m.Vm.OfficialCards.Single().IsInstalled);
        Assert.Empty(_box.Settings.VerifiedOfficialPlugins);
        var copy = m.Host.FindById(OfficialId)!;
        Assert.False(m.Host.IsVerifiedOfficial(copy, listing.Entry));

        // A folder dropped in by hand, or a forged record for another version, does not count either.
        _box.Settings.VerifiedOfficialPlugins[OfficialId] = "2.0.0|" + listing.Entry.Sha256;
        Assert.False(m.Host.IsVerifiedOfficial(copy, listing.Entry));
        _box.Settings.VerifiedOfficialPlugins.Clear();

        // Replace with official: the listed zip goes in its place and is verified.
        await m.Vm.InstallOfficialPluginAsync(listing);
        var installed = Assert.Single(m.Host.Plugins);
        Assert.Same(installed, listing.Installed);
        Assert.False(listing.HasUnverifiedCopy);
        Assert.False(listing.ShowButton); // the page's switch stands in for "Installed"
        Assert.DoesNotContain(m.Vm.CommunityCards, c => c.Id == OfficialId);
        Assert.True(m.Vm.OfficialCards.Single().IsInstalled);
        Assert.Equal("1.0.0|" + listing.Entry.Sha256, _box.Settings.VerifiedOfficialPlugins[OfficialId]);

        // Remove drops the record; the official zip installed by hand is verified again (same SHA).
        m.Host.Remove(installed, deleteData: true);
        Assert.Empty(_box.Settings.VerifiedOfficialPlugins);
        Assert.True(await m.Vm.InstallPluginPackageAsync(officialZip));
        Assert.Same(m.Host.FindById(OfficialId), listing.Installed);
        Assert.True(m.Host.IsVerifiedOfficial(listing.Installed!, listing.Entry));
    }

    [AvaloniaFact]
    public async Task Spoof_TheListedZipCreditedToSomeoneElse_IsNotOfficial()
    {
        using var m = Mount();
        // The list says Noctis, the zip's own plugin.json does not: both must agree.
        var zip = Zip("other-author.zip", OfficialId, "1.0.0", "Someone");
        Publish(m, File.ReadAllBytes(zip));
        await m.Vm.LoadOfficialPluginsAsync();

        await m.Vm.InstallOfficialPluginAsync(m.Vm.OfficialPlugins.Single(o => o.Entry.Id == OfficialId));

        Assert.Single(m.Host.Plugins);
        Assert.Empty(_box.Settings.VerifiedOfficialPlugins);
        Assert.Contains(m.Vm.CommunityCards, c => c.Id == OfficialId);
        Assert.True(m.Vm.OfficialPlugins.Single(o => o.Entry.Id == OfficialId).HasUnverifiedCopy);
    }

    [Fact]
    public void Trust_OnlyTheAuthorNoctisListingIsOfficial()
    {
        Assert.True(PluginTrust.IsOfficialAuthor("Noctis"));
        Assert.True(PluginTrust.IsOfficialAuthor(" noctis "));
        Assert.False(PluginTrust.IsOfficialAuthor("Noctis Team"));
        Assert.False(PluginTrust.IsOfficialAuthor("Jafezy JD"));
        Assert.False(PluginTrust.IsOfficialAuthor(null));

        var catalog = PluginCatalog.Parse(PluginCatalogTests.Index(
            PluginCatalogTests.Entry(id: "dev.a.one"),
            PluginCatalogTests.Entry(id: "dev.a.two", extra: new (string, object?)[] { ("author", "Alfamin") })));
        Assert.True(catalog.Plugins[0].IsOfficial);
        Assert.False(catalog.Plugins[1].IsOfficial);
    }

    // ── A plugin's page ──

    [AvaloniaFact]
    public void DetailPage_OpensFromACardWithEnter_BackReturnsToTheGrid_FocusFollows()
    {
        using var m = Mount(withClient: false);
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code", name: "Code Thing", author: "Someone",
            settingsJson: """[{ "key": "loud", "label": "Loud", "type": "bool" }]""", permissions: new[] { "playback.control" }),
            ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Pump();
            window.UpdateLayout();

            var list = view.FindControl<StackPanel>("PluginsListPage")!;
            var detail = view.FindControl<StackPanel>("PluginDetailPage")!;
            Assert.True(list.IsVisible);
            Assert.False(detail.IsVisible);

            var cardData = m.Vm.CommunityCards.Single(c => c.Id == "dev.test.code");
            var card = view.FindControl<ItemsControl>("CommunityPluginsGrid")!.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("plugin-card") && ReferenceEquals(b.DataContext, cardData));
            Assert.Equal(Loc.T("Plugins.Card.A11yCommunity", "Code Thing", "Someone"), AutomationProperties.GetName(card));
            Assert.True(card.IsTabStop);

            Assert.True(card.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            window.UpdateLayout();

            Assert.Same(cardData, m.Vm.SelectedPluginCard);
            Assert.False(list.IsVisible);
            Assert.True(detail.IsVisible);
            var back = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginBackButton");
            Assert.Same(back, window.FocusManager?.GetFocusedElement()); // keyboard users land on Back
            Assert.Equal(Loc.T("Plugins.Detail.Back"), AutomationProperties.GetName(back));
            var texts = detail.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Code Thing", texts);
            // "by Someone · v1.0.0" on one line.
            Assert.Contains(Loc.T("Plugins.Get.By", "Someone") + " · " + Loc.T("Plugins.Detail.Version", "1.0.0"), texts);
            Assert.Equal("v1.0.0", Loc.T("Plugins.Detail.Version", "1.0.0"));
            // Permissions are one quiet line, not chips.
            Assert.Contains(Loc.T("Plugins.Permissions"), texts);
            Assert.Contains(Loc.T("Plugins.PermSummary", Loc.T("Plugins.PermVerb.PlaybackControl")), texts);
            Assert.DoesNotContain(Loc.T("Plugins.PermShort.PlaybackControl"), texts);
            Assert.Contains("Loud", texts); // the plugin's own settings, as rows of the same card
            // Its switch and Remove plugin are on the page; the folder is in the "..." menu.
            Assert.NotNull(detail.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>().FirstOrDefault(t => t.Name == "PluginPageToggle"));
            var remove = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageRemoveButton");
            Assert.Same(m.Vm.RemovePluginCommand, remove.Command);
            Assert.Same(m.Host.FindById("dev.test.code"), remove.CommandParameter);
            Assert.Equal(Loc.T("Plugins.Detail.RemoveRow"), AutomationProperties.GetName(remove));
            var more = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageMoreButton");
            Assert.True(more.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Plugins.Detail.More"), AutomationProperties.GetName(more));
            var folder = Assert.Single(SettingsView.PluginMoreMenuItems(m.Vm, cardData)); // no homepage: just the folder
            Assert.Same(m.Vm.OpenPluginDirectoryCommand, folder.Command);
            Assert.Same(m.Host.FindById("dev.test.code"), folder.CommandParameter);

            back.Command!.Execute(back.CommandParameter);
            Pump();
            window.UpdateLayout();
            Assert.Null(m.Vm.SelectedPluginCard);
            Assert.True(list.IsVisible);
            Assert.False(detail.IsVisible);
            Assert.Same(card, window.FocusManager?.GetFocusedElement()); // back on the card that opened it

            // Leaving the tab and coming back shows the grid, not the last page.
            m.Vm.OpenPluginCardCommand.Execute(cardData);
            m.Vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
            m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
            Assert.Null(m.Vm.SelectedPluginCard);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DetailPage_OfARemovedPlugin_GoesBackToTheGrid()
    {
        using var m = Mount(withClient: false);
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code", name: "Code Thing"), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single());
        Assert.True(m.Vm.IsPluginDetailOpen);

        m.Host.Remove(m.Host.FindById("dev.test.code")!, deleteData: true);

        Assert.False(m.Vm.IsPluginDetailOpen);
        Assert.Empty(m.Vm.CommunityCards);
    }

    [AvaloniaFact]
    public async Task DetailPage_OfAListedPlugin_HasItsInstallButtonAndHomepage()
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        await m.Vm.LoadOfficialPluginsAsync();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Pump();
            m.Vm.OpenPluginCardCommand.Execute(m.Vm.OfficialCards.Single());
            Pump();
            window.UpdateLayout();

            var detail = view.FindControl<StackPanel>("PluginDetailPage")!;
            var hero = detail.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PluginHero");
            var install = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageInstallButton");
            Assert.True(install.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Plugins.Get.Install"), AutomationProperties.GetName(install));
            Assert.Same(m.Vm.OfficialPluginActionCommand, install.Command);
            Assert.Contains("accent-pill", install.Classes);
            // Not installed (owner 10-29): Install sits top-right, where "..." goes, and there is no "...".
            Assert.Equal("PluginPageCorner", install.FindAncestorOfType<Panel>()!.Name);
            var corner = install.TranslatePoint(new Point(install.Bounds.Width, 0), hero)!.Value;
            Assert.True(corner.X >= hero.Bounds.Width - 1 && corner.Y <= 8, $"Install ends at {corner} of a {hero.Bounds.Size} header");
            var more = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageMoreButton");
            Assert.False(more.IsVisible);
            Assert.False(detail.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Name == "PluginPageActions").IsVisible);
            Assert.DoesNotContain(detail.GetVisualDescendants().OfType<Button>(), b => b.Name == "PluginPageHomepageButton");
            // Not installed: no switch, no rows, no Remove.
            Assert.False(detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageSwitchPanel").IsEffectivelyVisible);
            Assert.False(detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageRemovePanel").IsVisible);

            // Installed: "..." takes the corner back (Visit homepage and Open folder), Install is gone.
            await m.Vm.InstallOfficialPluginAsync(m.Vm.OfficialPlugins.Single(o => o.Entry.Id == OfficialId));
            Pump();
            window.UpdateLayout();
            Assert.True(m.Vm.SelectedPluginCard!.IsInstalled);
            Assert.False(install.IsVisible);
            Assert.False(detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageUpdateButton").IsVisible);
            Assert.True(more.IsEffectivelyVisible);
            Assert.Equal("PluginPageCorner", more.FindAncestorOfType<Panel>()!.Name);
            // A plugin's own views ("Open Album Town") sit in that corner too, left of "..." (owner 10-29),
            // not under the description.
            var views = detail.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "PluginPageViews");
            var cornerPanel = Assert.IsType<StackPanel>(views.Parent);
            Assert.Equal("PluginPageCorner", cornerPanel.Name);
            Assert.True(cornerPanel.Children.IndexOf(views) < cornerPanel.Children.IndexOf(more));
            Assert.False(m.Vm.SelectedPluginCard.HasHeaderActions);
            more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            var menu = Assert.IsType<MenuFlyout>(view.PluginMoreMenu);
            var homepage = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal(Loc.T("Plugins.Detail.Homepage"), homepage.Header);
            Assert.Equal("https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.Mixxx", ToolTip.GetTip(homepage));
            Assert.Same(m.Vm.OpenPluginHomepageCommand, homepage.Command);
            Assert.Equal(Loc.T("Plugins.OpenPluginFolder"), Assert.IsType<MenuItem>(menu.Items[1]).Header);
            menu.Hide();
            Assert.True(detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageRemovePanel").IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>An installed plugin with an update: Update sits under the description (the corner keeps
    /// its "..."), next to the plugin's Open buttons.</summary>
    [AvaloniaFact]
    public void PluginCard_CornerAndInlineButtons_FollowTheInstallState()
    {
        var card = new PluginCardItem("x");
        Assert.False(card.ShowCornerButton || card.ShowInlineButton || card.HasMoreMenu || card.HasHeaderActions);
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code", name: "Code Thing"), ("Test.Plugin.dll", new byte[] { 1 }));
        var host = _box.NewHost(appVersion: "1.6.6");
        host.LoadAll();
        card.SetSource(null, host.FindById("dev.test.code"));
        Assert.True(card.HasMoreMenu);
        Assert.False(card.ShowCornerButton || card.ShowInlineButton);
    }

    /// <summary>Album Town's and Album Roulette's kinds of settings, plus a number with no full range
    /// and a text setting with a hint.</summary>
    private const string MixedSettings = """
        [{ "key": "crowd", "label": "Albums in the plaza", "type": "number", "default": 40, "min": 24, "max": 60,
           "description": "How many albums come to the plaza at once. Fewer is lighter on older computers, and the town is just as busy." },
         { "key": "gap", "label": "Gap", "type": "number", "default": 1.5, "min": 0 },
         { "key": "names", "label": "Show album names", "type": "bool", "default": false },
         { "key": "motion", "label": "Motion", "type": "choice", "choices": ["Smooth (60 fps)", "Balanced (30 fps)", "Battery saver (15 fps)"], "default": "Balanced (30 fps)" },
         { "key": "tool", "label": "Custom yt-dlp (optional)", "type": "string", "default": "", "placeholder": "Path to yt-dlp",
           "description": "Leave empty and the plugin installs yt-dlp and keeps it up to date." }]
        """;

    private (SettingsView View, Window Window, StackPanel Detail) OpenMixedPage(Mounted m, double width = 1100)
    {
        _box.WriteFolder("dev.test.mixed", PluginSandbox.Manifest(id: "dev.test.mixed", name: "Mixed Settings", author: "Someone",
            settingsJson: MixedSettings, permissions: new[] { "library.read", "playback.control" }), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = width, Height = 900, Content = view };
        window.Show();
        Pump();
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.mixed"));
        Settle(window);
        return (view, window, view.FindControl<StackPanel>("PluginDetailPage")!);
    }

    [AvaloniaFact]
    public void DetailPage_NumberSettings_AreTheSettingsSliderOrAPillStepper_NeverASquareSpinBox()
    {
        using var m = Mount(withClient: false);
        var (_, window, detail) = OpenMixedPage(m);
        try
        {
            var items = m.Host.FindById("dev.test.mixed")!.SettingItems;
            Assert.Empty(detail.GetVisualDescendants().OfType<NumericUpDown>());

            // A number with min and max: the Settings slider (accent-slider, 200 wide) with its value beside it.
            var crowd = items.Single(i => i.Key == "crowd");
            Assert.True(crowd.UsesSlider);
            var slider = detail.GetVisualDescendants().OfType<Noctis.Controls.SmoothSlider>()
                .Single(s => AutomationProperties.GetName(s) == "Albums in the plaza");
            Assert.True(slider.IsEffectivelyVisible);
            Assert.Contains("accent-slider", slider.Classes);
            Assert.Equal((24d, 60d, 1d, 40d), (slider.Minimum, slider.Maximum, slider.Step, slider.SteppedValue));
            var row = slider.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("plugin-row"));
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("plugin-number-value") && t.Text == "40" && t.IsEffectivelyVisible);
            slider.SteppedValue = 52;
            Pump();
            Assert.Equal("52", crowd.Value);
            Assert.Equal("52", _box.Settings.PluginSettingValues["dev.test.mixed"]["crowd"]); // saved like before
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("plugin-number-value") && t.Text == "52");

            // No max: minus, the value, plus, in one rounded capsule; steps of 0.1 for a decimal default.
            var gap = items.Single(i => i.Key == "gap");
            Assert.True(gap.UsesStepper);
            var stepper = detail.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("plugin-stepper") && b.IsEffectivelyVisible);
            Assert.Equal(new CornerRadius(999), stepper.CornerRadius);
            var buttons = stepper.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(new[] { Loc.T("Plugins.Setting.Decrease", "Gap"), Loc.T("Plugins.Setting.Increase", "Gap") },
                buttons.Select(AutomationProperties.GetName));
            buttons[1].Command!.Execute(null);
            Assert.Equal(1.6m, gap.NumberValue);
            for (var i = 0; i < 30; i++) buttons[0].Command!.Execute(null);
            Assert.Equal(0m, gap.NumberValue); // clamped at min
            Pump();
            Assert.Contains(stepper.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0");

            // The other kinds: the Settings switch, the pill list, a full-width text box.
            Assert.Contains(detail.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>(), t => AutomationProperties.GetName(t) == "Show album names");
            var motion = detail.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Motion");
            Assert.Contains("pill-list", motion.Classes);
            var text = detail.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Custom yt-dlp (optional)");
            var textRow = text.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("plugin-row"));
            Assert.True(text.Bounds.Width >= textRow.Bounds.Width - 1, $"text box {text.Bounds.Width} of a {textRow.Bounds.Width} row");
            Assert.Equal("Path to yt-dlp", text.Watermark);

            // A long description is one grey line; the whole of it is in the tooltip.
            var hint = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("plugin-setting-hint"));
            Assert.Equal(1, hint.MaxLines);
            Assert.Equal(crowd.Description, ToolTip.GetTip(hint));
        }
        finally { window.Close(); }
    }

    /// <summary>What a slider looks like, part by part: the same text for the same control and style.</summary>
    internal static string SliderLook(Slider s)
    {
        var track = s.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().Single();
        var thumb = s.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
        var buttons = s.GetVisualDescendants().OfType<RepeatButton>().ToList();
        string Fill(RepeatButton b)
        {
            var border = b.GetVisualDescendants().OfType<Border>().First();
            return $"{border.Background}/{border.Opacity}/{border.CornerRadius}/{border.Height}";
        }
        var value = s.GetVisualParent()!.GetVisualChildren().OfType<TextBlock>().Single();
        return string.Join(" | ",
            s.GetType().Name, string.Join(",", s.Classes.Where(c => !c.StartsWith(':'))), s.Bounds.Width, s.Height, s.Margin,
            track.Height, $"{thumb.Width}x{thumb.Height}", thumb.Background, thumb.Transitions?.Count,
            string.Join(" ; ", buttons.Select(b => $"{b.Name} {b.Margin} {Fill(b)}")),
            $"value {value.FontSize} {value.Opacity} {value.Margin} {value.MinWidth} {value.TextAlignment}");
    }

    /// <summary>The plugin page's number slider IS the Settings slider (owner 10-29): the same control,
    /// track, thumb, fill, size and value beside it as Lyrics > Minimum line opacity, and a
    /// double-click puts the default back like there.</summary>
    [AvaloniaFact]
    public void DetailPage_NumberSlider_IsExactlyTheSettingsSlider()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var m = Mount(withClient: false);
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabLyrics;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Settle(window);
            var core = view.FindControl<Noctis.Controls.SmoothSlider>("LyricsMinLineOpacitySlider")!;
            Assert.True(core.IsEffectivelyVisible);
            var coreLook = SliderLook(core);

            _box.WriteFolder("dev.test.mixed", PluginSandbox.Manifest(id: "dev.test.mixed", name: "Mixed Settings", author: "Someone",
                settingsJson: MixedSettings), ("Test.Plugin.dll", new byte[] { 1 }));
            m.Host.LoadAll();
            m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
            m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.mixed"));
            Settle(window);
            var detail = view.FindControl<StackPanel>("PluginDetailPage")!;
            var slider = detail.GetVisualDescendants().OfType<Noctis.Controls.SmoothSlider>().Single(s => s.IsEffectivelyVisible);
            Assert.Equal(coreLook, SliderLook(slider));

            var crowd = m.Host.FindById("dev.test.mixed")!.SettingItems.Single(i => i.Key == "crowd");
            slider.SteppedValue = 52;
            Pump();
            Assert.Equal("52", crowd.Value);
            slider.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            Pump();
            Assert.Equal("40", crowd.Value); // plugin.json's default
            Assert.Equal(40, slider.SteppedValue);
        }
        finally { window.Close(); }
    }

    /// <summary>The plugin page's choice list IS a Settings drop-down (owner 10-29): the same pill, the
    /// same card and rows as Interface Size's list, a pill highlight under the pointer like a menu
    /// row, and it opens and closes with the menus' pop.</summary>
    [AvaloniaFact]
    public void DetailPage_ChoiceList_IsTheSettingsDropDown_AndPopsLikeAMenu()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var m = Mount(withClient: false);
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Settle(window);
            var core = view.FindControl<ComboBox>("UiScaleCombo")!;
            var coreLook = DropDownLook(window, core);

            _box.WriteFolder("dev.test.mixed", PluginSandbox.Manifest(id: "dev.test.mixed", name: "Mixed Settings", author: "Someone",
                settingsJson: MixedSettings), ("Test.Plugin.dll", new byte[] { 1 }));
            m.Host.LoadAll();
            m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
            m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.mixed"));
            Settle(window);
            var motion = view.FindControl<StackPanel>("PluginDetailPage")!.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => AutomationProperties.GetName(c) == "Motion");
            Assert.Equal(coreLook, DropDownLook(window, motion));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Opens <paramref name="box"/> (its popup drawn in the window so the headless pointer reaches
    /// it), checks the menus' pop and the pill hover, closes it and checks the close motion, and
    /// returns its look: shell and list card and rows, part by part.
    /// </summary>
    internal static string DropDownLook(Window window, ComboBox box)
    {
        AddToPlaylistDialogTests.EnsureAppStyles(); // the menus' hover fill lives in Styles.axaml
        Noctis.Helpers.ComboBoxDropDownAnimator.Install(); // App.axaml.cs installs it for every list
        var popup = box.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "PART_Popup");
        popup.ShouldUseOverlayLayer = true;
        var shell = box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Background");
        var look = $"shell {box.CornerRadius} {shell.Background} {shell.CornerRadius} {shell.BorderThickness}";

        Noctis.Helpers.MenuOpenAnimation.ForgetLastPress();
        box.IsDropDownOpen = true;
        var card = Assert.IsAssignableFrom<Control>(popup.Child);
        Assert.Equal("PopupBorder", card.Name);
        // Opening: the menus' pop (MenuOpenAnimation.PopIn): it starts small and clear, grows to
        // full size while it fades in, and its rows settle in after it.
        Assert.Equal(0, card.Opacity);
        Assert.Equal(0.9, card.RenderTransform!.Value.M11, 3);
        PumpFor(60);
        Assert.Contains(card.Transitions!, t => t is Avalonia.Animation.TransformOperationsTransition { Duration.TotalMilliseconds: 280 });
        PumpFor(600);
        Assert.Equal(Matrix.Identity, card.RenderTransform!.Value); // landed at full size
        Assert.Equal(1, card.Opacity);
        var rows = box.GetRealizedContainers().OfType<ComboBoxItem>().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(1, r.Opacity)); // every row settled in
        var c = (Border)card;
        var row = rows.First(r => !r.IsSelected);
        var rowFace = row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        look += $" | card {c.CornerRadius} {c.Padding} {c.Background} {c.BorderThickness} {c.ClipToBounds}"
                + $" | row {row.Margin} {row.Padding} {row.CornerRadius} {rowFace.CornerRadius}";

        // Hover: the menus' pill fill on that row only.
        Assert.True(window.TryFindResource("MenuV2HoverBrush", window.ActualThemeVariant, out var hoverBrush));
        var hover = ((ISolidColorBrush)hoverBrush!).Color;
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        PumpFor(60);
        Assert.Equal(hover, (rowFace.Background as ISolidColorBrush)?.Color);
        var chosen = rows.Single(r => r.IsSelected).GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        look += $" | chosen {chosen.Background}";
        window.MouseMove(new Point(2, 2));

        // Closing: held for the pop's reverse (inert, shrinking toward the click, fading), then
        // really closed.
        box.IsDropDownOpen = false;
        PumpFor(40);
        Assert.True(popup.IsOpen); // still on screen
        Assert.NotNull(TopLevel.GetTopLevel(card));
        Assert.False(card.IsHitTestVisible);
        Assert.True(card.Opacity < 1, $"opacity {card.Opacity}: not fading");
        Assert.Contains(card.Transitions!, t => t is Avalonia.Animation.TransformOperationsTransition { Duration.TotalMilliseconds: 150 });
        PumpFor(500);
        Assert.False(popup.IsOpen);
        Assert.Null(TopLevel.GetTopLevel(card)); // gone
        Assert.False(box.IsDropDownOpen);
        // A new open is a full pop again, and the card takes clicks.
        box.IsDropDownOpen = true;
        PumpFor(500);
        Assert.True(card.IsHitTestVisible);
        Assert.Equal(1, card.Opacity);
        box.IsDropDownOpen = false;
        PumpFor(500);
        Assert.False(popup.IsOpen);
        popup.ShouldUseOverlayLayer = false;
        return look;
    }

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Pump();
            Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
    }

    [AvaloniaFact]
    public void DetailPage_IsOneCardOfRows_WithoutDebugText_AndThePermissionsLineOpens()
    {
        using var m = Mount(withClient: false);
        var (_, window, detail) = OpenMixedPage(m);
        try
        {
            var plugin = m.Host.FindById("dev.test.mixed")!;
            plugin.Extensions = "View: A-B Loop · Menu: Loop a part";
            Pump();
            var texts = detail.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.DoesNotContain(texts, t => t?.Contains("View: A-B Loop") == true);
            Assert.DoesNotContain(Loc.T("Plugins.Settings"), texts); // no section titles: the rows say it all
            Assert.DoesNotContain(Loc.T("Plugins.Detail.EnableLine"), texts); // a plugin that runs fine has no subtitle

            // Enabled, the five settings and the permissions line share one card; Remove is its own row.
            var group = detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageGroup");
            var rows = group.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("plugin-row") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(7, rows.Count);
            Assert.Equal(new Thickness(0), rows[0].BorderThickness); // no hairline above the first row
            Assert.All(rows.Skip(1), r => Assert.Equal(new Thickness(0, 1, 0, 0), r.BorderThickness));
            Assert.Equal(2, detail.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("plugin-panel") && b.IsEffectivelyVisible));

            var summary = Loc.T("Plugins.PermSummary", Loc.T("Plugins.PermVerb.LibraryRead") + ", " + Loc.T("Plugins.PermVerb.PlaybackControl"));
            Assert.Equal(summary, plugin.PermissionSummary);
            Assert.Contains(summary, texts);
            var line = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPagePermissions");
            Assert.Equal(summary, AutomationProperties.GetHelpText(line));
            var card = (PluginCardItem)detail.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PluginHero").DataContext!;
            Assert.False(card.IsPermissionsExpanded);
            line.Command!.Execute(null);
            Settle(window);
            Assert.True(card.IsPermissionsExpanded);
            var list = detail.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "PluginPagePermissionList");
            Assert.Equal(new[] { Loc.T("Plugins.Perm.LibraryRead"), Loc.T("Plugins.Perm.PlaybackControl") },
                list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1300)]
    [InlineData(900)]
    [InlineData(640)]
    [InlineData(580)]
    public void DetailPage_AtNarrowWidths_NoTextIsClippedOrRunsPastTheEdge(double windowWidth)
    {
        using var m = Mount(withClient: false);
        var (view, window, detail) = OpenMixedPage(m, windowWidth);
        try
        {
            m.Vm.SelectedPluginCard!.IsPermissionsExpanded = true;
            Settle(window);
            AssertNoClippedText(detail, $"page {windowWidth}");

            // The grid's own pills (Install from file, Read the guide ...) too.
            m.Vm.ClosePluginDetailCommand.Execute(null);
            Settle(window);
            AssertNoClippedText(view.FindControl<StackPanel>("PluginsListPage")!, $"grid {windowWidth}");
        }
        finally { window.Close(); }
    }

    /// <summary>Every visible text under <paramref name="page"/> shows whole: inside the page, as wide
    /// and as tall as it needs (or, for a description trimmed on purpose, the whole text in its
    /// tooltip); and every control ends inside the page.</summary>
    private static void AssertNoClippedText(Control page, string what)
    {
        foreach (var t in page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            var left = t.TranslatePoint(new Point(0, 0), page)!.Value.X;
            var right = t.TranslatePoint(new Point(t.Bounds.Width, 0), page)!.Value.X;
            Assert.True(left >= -0.5 && right <= page.Bounds.Width + 0.5, $"{what}: \"{t.Text}\" spans {left}..{right} of {page.Bounds.Width}");
            var layout = t.TextLayout;
            if (t.TextTrimming == TextTrimming.None)
            {
                Assert.True(layout.WidthIncludingTrailingWhitespace <= t.Bounds.Width + 0.5 || t.TextWrapping != TextWrapping.NoWrap,
                    $"{what}: \"{t.Text}\" needs {layout.WidthIncludingTrailingWhitespace}, has {t.Bounds.Width}");
                Assert.True(layout.Height <= t.Bounds.Height + 0.5, $"{what}: \"{t.Text}\" cut at the bottom ({layout.Height} in {t.Bounds.Height})");
            }
            else if (layout.TextLines.Any(l => l.HasCollapsed) && (t.Classes.Contains("plugin-setting-hint") || t.Classes.Contains("plugin-choice-text")))
            {
                Assert.Equal(t.Text, ToolTip.GetTip(t) as string);
            }
        }
        foreach (var c in page.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is Button or TextBox or ComboBox or Slider or Noctis.Controls.LottieToggle))
        {
            var right = c.TranslatePoint(new Point(c.Bounds.Width, 0), page)!.Value.X;
            Assert.True(right <= page.Bounds.Width + 0.5, $"{what}: {c.GetType().Name} {AutomationProperties.GetName(c)} ends at {right} of {page.Bounds.Width}");
        }
    }

    [AvaloniaFact]
    public async Task ReleaseBuild_HasNoRemovePlugin_OnlyTheSwitch()
    {
        // Owner 10-29: release builds can only switch plugins on and off.
        using var m = Mount(withClient: false);
        m.Vm.PluginRemovalAllowed = false;
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code", name: "Code Thing", author: "Someone"),
            ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Pump();
            m.Vm.SelectedPluginCard = m.Vm.CommunityCards.Single(c => c.Id == "dev.test.code");
            Pump();
            window.UpdateLayout();

            var detail = view.FindControl<StackPanel>("PluginDetailPage")!;
            Assert.True(detail.IsVisible);
            Assert.NotNull(detail.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>().FirstOrDefault(t => t.Name == "PluginPageToggle"));
            Assert.DoesNotContain(detail.GetVisualDescendants().OfType<Button>(), b => b.Name == "PluginPageRemoveButton" && b.IsEffectivelyVisible);
        }
        finally { window.Close(); }

        // Nothing else can remove it either.
        var asked = 0;
        m.Vm.ShowPluginDialog = r => { asked++; return Task.FromResult(new ConfirmationResult(true, true)); };
        await m.Vm.RemovePluginCommand.ExecuteAsync(m.Host.FindById("dev.test.code"));
        Assert.Equal(0, asked);
        Assert.NotNull(m.Host.FindById("dev.test.code"));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remove_AsksWithTheSwitchRow_AndTheDataOptionStillDecides(bool deleteData)
    {
        using var m = Mount(withClient: false);
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code", name: "Code Thing"), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        var plugin = m.Host.FindById("dev.test.code")!;
        Directory.CreateDirectory(plugin.DataDirectory);
        File.WriteAllText(Path.Combine(plugin.DataDirectory, "state.json"), "{}");

        ConfirmationRequest? asked = null;
        m.Vm.ShowPluginDialog = r => { asked = r; return Task.FromResult(new ConfirmationResult(true, deleteData)); };
        await m.Vm.RemovePluginCommand.ExecuteAsync(plugin);
        Assert.Equal(Loc.T("Plugins.RemoveDeleteData"), asked!.OptionText);
        Assert.Null(m.Host.FindById("dev.test.code"));
        Assert.Equal(!deleteData, Directory.Exists(plugin.DataDirectory));

        // The dialog itself: the option is a row with the app's switch, never a square checkbox.
        var dialog = new ConfirmationDialog(asked);
        dialog.Show();
        try
        {
            Pump();
            Assert.Empty(dialog.GetVisualDescendants().OfType<CheckBox>());
            var toggle = dialog.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>().Single(t => t.Name == "OptionToggle");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Plugins.RemoveDeleteData"), AutomationProperties.GetName(toggle));
            var label = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "OptionLabel");
            Assert.Equal(Loc.T("Plugins.RemoveDeleteData"), label.Text);
            Assert.True(label.TranslatePoint(new Point(0, 0), toggle)!.Value.X < 0); // label left, switch right
            Assert.False(dialog.OptionChecked);
            toggle.IsChecked = true;
            Assert.True(dialog.OptionChecked);
        }
        finally { dialog.Close(); }
    }

    // ── Icons ──

    [Fact]
    public void Monogram_IsTheInitials_OnAColourThatOnlyDependsOnTheName()
    {
        Assert.Equal("AT", PluginMonogram.Initials("Album Town"));
        Assert.Equal("M", PluginMonogram.Initials("Mixxx"));
        Assert.Equal("LM", PluginMonogram.Initials("lyric-motion"));
        Assert.Equal("?", PluginMonogram.Initials("  "));

        // FNV-1a over the lower-cased name: the same tile every run (string.GetHashCode is not).
        uint hash = 2166136261;
        foreach (var ch in "album town") { hash ^= ch; hash *= 16777619; }
        Assert.Equal((int)(hash % (uint)PluginMonogram.Palette.Length), PluginMonogram.PaletteIndex("Album Town"));
        Assert.Equal(PluginMonogram.PaletteIndex("Album Town"), PluginMonogram.PaletteIndex(" ALBUM TOWN "));
        Assert.True(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }.Select(PluginMonogram.PaletteIndex).Distinct().Count() > 1);
    }

    [AvaloniaFact]
    public void Icon_FromPluginJson_ElseTheLetterTile_AndABadPathIsIgnored()
    {
        using var m = Mount(withClient: false);
        var png = File.ReadAllBytes(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/icon.png"));
        _box.WriteFolder("dev.test.icon", PluginSandbox.Manifest(id: "dev.test.icon", name: "With Icon", extra: "  \"icon\": \"assets/icon.png\""),
            ("Test.Plugin.dll", new byte[] { 1 }), ("assets/icon.png", png));
        _box.WriteFolder("dev.test.plain", PluginSandbox.Manifest(id: "dev.test.plain", name: "Plain Thing"), ("Test.Plugin.dll", new byte[] { 1 }));
        _box.WriteFolder("dev.test.evil", PluginSandbox.Manifest(id: "dev.test.evil", name: "Evil", extra: "  \"icon\": \"../dev.test.icon/assets/icon.png\""),
            ("Test.Plugin.dll", new byte[] { 1 }));
        _box.WriteFolder("dev.test.gone", PluginSandbox.Manifest(id: "dev.test.gone", name: "Gone", extra: "  \"icon\": \"missing.png\""),
            ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;

        var withIcon = m.Vm.CommunityCards.Single(c => c.Id == "dev.test.icon");
        Assert.EndsWith(Path.Combine("assets", "icon.png"), withIcon.Installed!.IconPath);
        Assert.True(withIcon.HasIcon);
        Assert.False(withIcon.ShowMonogram);

        var plain = m.Vm.CommunityCards.Single(c => c.Id == "dev.test.plain");
        Assert.Null(plain.Installed!.IconPath);
        Assert.False(plain.HasIcon);
        Assert.True(plain.ShowMonogram);
        Assert.Equal("PT", plain.Monogram);
        Assert.Equal(PluginMonogram.PaletteIndex("Plain Thing"), plain.MonogramPaletteIndex);
        Assert.IsType<LinearGradientBrush>(plain.MonogramBrush);
        Assert.IsType<LinearGradientBrush>(plain.TintBrush); // the faint wash of its colour

        var evil = m.Vm.CommunityCards.Single(c => c.Id == "dev.test.evil");
        Assert.Null(evil.Installed!.Manifest!.Icon);
        Assert.Contains(evil.Installed.Manifest.Warnings, w => w.Contains("\"icon\""));
        Assert.True(evil.ShowMonogram);

        Assert.True(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.gone").ShowMonogram);
    }

    [AvaloniaFact]
    public void Icon_OfAListedPlugin_IsBuiltIntoTheApp()
    {
        var repo = File.ReadAllBytes(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Mixxx/icon.png"));
        Assert.Equal(repo, PluginCatalog.ReadEmbeddedIcon("Noctis.Plugins.Mixxx/icon.png"));
        Assert.Null(PluginCatalog.ReadEmbeddedIcon("Noctis.Plugins.Nope/icon.png"));

        using var m = Mount(withClient: false);
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins; // the built-in list, Mixxx not installed
        var mixxx = m.Vm.OfficialCards.Single(c => c.Id == "dev.noctis.plugins.mixxx");
        Assert.True(mixxx.HasIcon);
    }

    [AvaloniaFact]
    public async Task Icon_OfAListedPluginTheAppDoesNotHave_IsFetchedOnce_FromGitHubOnly()
    {
        using var m = Mount();
        var png = File.ReadAllBytes(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/icon.png"));
        const string iconUrl = "https://raw.githubusercontent.com/someone/listed/main/icon.png";
        m.Server[iconUrl] = png;
        m.Server[PluginCatalog.DefaultUrl] = System.Text.Encoding.UTF8.GetBytes(PluginCatalogTests.Index(
            PluginCatalogTests.Entry(id: "dev.community.listed", name: "Listed", download: CommunityUrl, minAppVersion: null,
                extra: new (string, object?)[] { ("author", "Alfamin"), ("icon", iconUrl) }),
            PluginCatalogTests.Entry(id: "dev.community.elsewhere", name: "Elsewhere", download: CommunityUrl, minAppVersion: null,
                extra: new (string, object?)[] { ("author", "Alfamin"), ("icon", "https://example.com/icon.png") })));

        await m.Vm.LoadOfficialPluginsAsync();
        for (var i = 0; i < 200 && !m.Vm.CommunityCards.Single(c => c.Id == "dev.community.listed").HasIcon; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(m.Vm.CommunityCards.Single(c => c.Id == "dev.community.listed").HasIcon);
        Assert.False(m.Vm.CommunityCards.Single(c => c.Id == "dev.community.elsewhere").HasIcon);
        Assert.DoesNotContain(m.Server.Requests, r => r.Contains("example.com"));
        Assert.Single(m.Server.Requests, r => r == iconUrl);
    }

    [Fact]
    public void ManifestIcon_IsOptional_RelativePng_InsideTheFolder()
    {
        PluginManifest Parse(string icon) => PluginManifest.Parse(PluginSandbox.Manifest(extra: $"  \"icon\": \"{icon}\""));

        Assert.Equal("icon.png", Parse("icon.png").Icon);
        Assert.Equal("assets/icon.png", Parse("assets/icon.png").Icon);
        Assert.Null(PluginManifest.Parse(PluginSandbox.Manifest()).Icon);
        foreach (var bad in new[] { "../icon.png", "/icon.png", "C:/icon.png", "icon.jpg", "a/./icon.png", "https://x.dev/icon.png" })
        {
            var m = Parse(bad);
            Assert.Null(m.Icon);
            Assert.Contains(m.Warnings, w => w.Contains("\"icon\""));
        }

        // Content packs refuse unknown fields; "icon" is a known one now.
        var pack = PluginManifest.Parse(ContentPackKit.Manifest(extra: "\"icon\": \"icon.png\""));
        Assert.Equal("icon.png", pack.Icon);
    }

    [Fact]
    public void CatalogIcon_RelativeToTheList_OrAGitHubLink()
    {
        var catalog = PluginCatalog.Parse(PluginCatalogTests.Index(
            PluginCatalogTests.Entry(id: "dev.a.rel", extra: new (string, object?)[] { ("icon", "Noctis.Plugins.Mixxx/icon.png") }),
            PluginCatalogTests.Entry(id: "dev.a.abs", extra: new (string, object?)[] { ("icon", "https://raw.githubusercontent.com/x/y/main/icon.png") }),
            PluginCatalogTests.Entry(id: "dev.a.bad", extra: new (string, object?)[] { ("icon", "https://example.com/icon.png") }),
            PluginCatalogTests.Entry(id: "dev.a.none")));

        Assert.Empty(catalog.Warnings); // an icon is never a reason to skip a plugin
        var rel = catalog.Plugins.Single(p => p.Id == "dev.a.rel");
        Assert.Equal("Noctis.Plugins.Mixxx/icon.png", rel.IconPath);
        Assert.Equal("https://raw.githubusercontent.com/heartached/Noctis/main/plugins/Noctis.Plugins.Mixxx/icon.png", rel.IconUri!.AbsoluteUri);
        Assert.Equal("https://raw.githubusercontent.com/x/y/main/icon.png", catalog.Plugins.Single(p => p.Id == "dev.a.abs").IconUri!.AbsoluteUri);
        Assert.Null(catalog.Plugins.Single(p => p.Id == "dev.a.bad").IconUri);
        Assert.Null(catalog.Plugins.Single(p => p.Id == "dev.a.none").IconUri);
    }

    [Fact]
    public void RepoIcons_Are256pxPngs_WiredIntoPluginJsonAndTheList()
    {
        foreach (var folder in new[] { "Noctis.Plugins.Mixxx", "Noctis.Plugins.AlbumTown", "Noctis.Plugins.TrueShuffle", "Noctis.Plugins.LyricMotion" })
        {
            var bytes = File.ReadAllBytes(PluginSandbox.RepoFile($"plugins/{folder}/icon.png"));
            Assert.True(PluginCatalogClient.IsPng(bytes), folder);
            // IHDR: width and height, big-endian, at bytes 16..23.
            Assert.Equal(256, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
            Assert.Equal(256, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
            Assert.True(bytes.Length <= PluginCatalog.MaxIconBytes);
        }
        foreach (var folder in new[] { "Noctis.Plugins.Mixxx", "Noctis.Plugins.AlbumTown" })
            Assert.Equal("icon.png", PluginManifest.TryLoad(PluginSandbox.RepoFile($"plugins/{folder}"))!.Icon);

        var mixxx = PluginCatalog.LoadEmbedded().Plugins.Single(p => p.Id == "dev.noctis.plugins.mixxx");
        Assert.Equal("Noctis.Plugins.Mixxx/icon.png", mixxx.IconPath);
        Assert.True(File.Exists(PluginSandbox.RepoFile("plugins/" + mixxx.IconPath)));
        Assert.Contains("icon.png", File.ReadAllText(PluginSandbox.RepoFile("samples/PluginPackage.targets")));
    }

    // ── Search ──

    [AvaloniaFact]
    public async Task Search_FiltersBothSections_ByNameAuthorOrDescription()
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        _box.WriteFolder("dev.test.hand", PluginSandbox.Manifest(id: "dev.test.hand", name: "Hand Made", author: "Someone"), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        await m.Vm.LoadOfficialPluginsAsync();

        m.Vm.PluginSearchText = "jafezy";            // author
        Assert.Empty(m.Vm.OfficialCards);
        Assert.Equal("dev.community.listed", Assert.Single(m.Vm.CommunityCards).Id);

        m.Vm.PluginSearchText = "PLAZA";             // description, any case
        Assert.Equal("dev.community.listed", Assert.Single(m.Vm.CommunityCards).Id);

        m.Vm.PluginSearchText = "test plugin";       // every word must match
        Assert.Equal(OfficialId, Assert.Single(m.Vm.OfficialCards).Id);
        Assert.Empty(m.Vm.CommunityCards);
        Assert.False(m.Vm.ShowCommunitySection);     // no empty "install one" hint while searching
        Assert.False(m.Vm.ShowNoPluginMatches);

        m.Vm.PluginSearchText = "nothing like this";
        Assert.Empty(m.Vm.OfficialCards);
        Assert.Empty(m.Vm.CommunityCards);
        Assert.True(m.Vm.ShowNoPluginMatches);

        m.Vm.ClearPluginSearchCommand.Execute(null);
        Assert.Single(m.Vm.OfficialCards);
        Assert.Equal(2, m.Vm.CommunityCards.Count);
        Assert.False(m.Vm.ShowNoPluginMatches);
    }

    [AvaloniaFact]
    public async Task Search_BoxIsBoundAndNamed_AndAnOpenPageSurvivesAFilterThatHidesItsCard()
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        await m.Vm.LoadOfficialPluginsAsync();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            window.Show();
            Pump();
            var box = view.FindControl<TextBox>("PluginSearchBox")!;
            Assert.Equal(Loc.T("Plugins.Search"), AutomationProperties.GetName(box));
            box.Text = "listed";
            Pump();
            Assert.Equal("listed", m.Vm.PluginSearchText);
            Assert.Empty(m.Vm.OfficialCards);
            Assert.False(view.FindControl<StackPanel>("OfficialPluginsSection")!.IsVisible);

            var card = m.Vm.CommunityCards.Single();
            m.Vm.OpenPluginCardCommand.Execute(card);
            m.Vm.PluginSearchText = "zzz";
            Assert.Same(card, m.Vm.SelectedPluginCard);
        }
        finally { window.Close(); }
    }

    // ── Narrow panes ──

    [Theory]
    [InlineData(900, 4)]
    [InlineData(700, 3)]
    [InlineData(590, 2)]
    [InlineData(400, 2)]
    [InlineData(380, 1)]
    [InlineData(0, 1)]
    public void GridColumns_FollowThePageWidth(double width, int columns)
        => Assert.Equal(columns, SettingsView.PluginGridColumnsFor(width));

    [AvaloniaTheory]
    [InlineData(1300)]
    [InlineData(900)]
    [InlineData(640)]
    [InlineData(580)]
    public async Task Layout_AtEveryWidth_CardsFitTheirColumns_AndNothingRunsPastTheEdge(double windowWidth)
    {
        using var m = Mount();
        Publish(m, File.ReadAllBytes(Zip("official.zip", OfficialId, "1.0.0", "Noctis")));
        _box.WriteFolder("dev.test.hand", PluginSandbox.Manifest(id: "dev.test.hand",
            name: "A Plugin With A Rather Long Name For Its Card", author: "Someone With A Long Name"), ("Test.Plugin.dll", new byte[] { 1 }));
        m.Host.LoadAll();
        await m.Vm.LoadOfficialPluginsAsync();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = windowWidth, Height = 900, Content = view };
        try
        {
            window.Show();
            Settle(window);

            var page = view.FindControl<StackPanel>("PluginsTabPanel")!;
            Assert.Equal(SettingsView.PluginGridColumnsFor(page.Bounds.Width), m.Vm.PluginGridColumns);

            void AssertInside(Control root, string what)
            {
                foreach (var c in root.GetVisualDescendants().OfType<Control>()
                             .Where(c => c.IsEffectivelyVisible && (c is Button || c is TextBox || (c is Border b && b.Classes.Contains("plugin-panel")))))
                {
                    var left = c.TranslatePoint(new Point(0, 0), page)!.Value.X;
                    var right = c.TranslatePoint(new Point(c.Bounds.Width, 0), page)!.Value.X;
                    Assert.True(left >= -0.5 && right <= page.Bounds.Width + 0.5,
                        $"{what} {windowWidth}: {c.Name ?? c.GetType().Name} spans {left}..{right}, page is {page.Bounds.Width}");
                }
            }

            AssertInside(view.FindControl<StackPanel>("PluginsListPage")!, "grid");
            var cards = view.FindControl<StackPanel>("PluginsListPage")!.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("plugin-card")).ToList();
            Assert.Equal(3, cards.Count);
            Assert.All(cards, c => Assert.True(c.Bounds.Width >= SettingsView.PluginCardMinWidth - 0.5 || m.Vm.PluginGridColumns == 1,
                $"card {c.Bounds.Width} wide at {m.Vm.PluginGridColumns} columns"));
            // A long name trims on its card instead of widening it.
            var longName = cards.Single(c => ((PluginCardItem)c.DataContext!).Id == "dev.test.hand")
                .GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("plugin-card-name"));
            Assert.True(longName.Bounds.Width <= cards[0].Bounds.Width);

            // The header's actions sit beside the search box when there is room, under it when not.
            var actions = view.FindControl<WrapPanel>("PluginsHeaderActions")!;
            var header = view.FindControl<Grid>("PluginsHeader")!;
            Assert.Equal(header.Bounds.Width < actions.DesiredSize.Width + SettingsView.PluginSearchMinWidth ? 1 : 0, Grid.GetRow(actions));

            // A plugin's page fits too.
            m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == "dev.test.hand"));
            Settle(window);
            AssertInside(view.FindControl<StackPanel>("PluginDetailPage")!, "page");
        }
        finally { window.Close(); }
    }
}
