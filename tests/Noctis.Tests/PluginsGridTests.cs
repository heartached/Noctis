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
        Assert.Equal(Loc.T("Plugins.Get.By", "Noctis"), official.ByLine);

        Assert.Equal(new[] { "dev.community.listed", "dev.test.anon", "dev.test.hand" }, m.Vm.CommunityCards.Select(c => c.Id));
        Assert.All(m.Vm.CommunityCards, c => Assert.False(c.IsOfficial));
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
            Assert.Contains(Loc.T("Plugins.Get.By", "Someone"), texts);
            Assert.Contains(Loc.T("Plugins.Detail.Version", "1.0.0"), texts);
            Assert.Contains(Loc.T("Plugins.Permissions"), texts);
            Assert.Contains(Loc.T("Plugins.Settings"), texts); // the settings moved here from the old row
            Assert.Contains("Loud", texts);
            // Its switch, its folder and Remove are on the page.
            Assert.NotNull(detail.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>().FirstOrDefault(t => t.Name == "PluginPageToggle"));
            var remove = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageRemoveButton");
            Assert.Same(m.Vm.RemovePluginCommand, remove.Command);
            Assert.Same(m.Host.FindById("dev.test.code"), remove.CommandParameter);

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
            var install = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageInstallButton");
            Assert.True(install.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Plugins.Get.Install"), AutomationProperties.GetName(install));
            Assert.Same(m.Vm.OfficialPluginActionCommand, install.Command);
            Assert.Contains("accent-pill", install.Classes);
            var homepage = detail.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PluginPageHomepageButton");
            Assert.True(homepage.IsEffectivelyVisible);
            Assert.Equal("https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.Mixxx", ToolTip.GetTip(homepage));
            // Not installed: no switch, no Remove.
            Assert.False(detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageSwitchPanel").IsVisible);
            Assert.False(detail.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PluginPageFilesPanel").IsVisible);
        }
        finally { window.Close(); }
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
