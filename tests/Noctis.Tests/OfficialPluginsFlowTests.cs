using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Plugins → Get plugins, end to end without a network: the list loads, each row's
/// button follows the installed version, and Install / Update download, verify and install
/// through the same path as Install from file.
/// </summary>
public class OfficialPluginsFlowTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private const string Url = "https://github.com/heartached/Noctis/releases/download/plugin-zip-v1/p.zip";

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

    private Mounted Mount(Func<ConfirmationRequest, ConfirmationResult> answer, string appVersion = "1.5.9")
    {
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        var dialogs = new List<ConfirmationRequest>();
        vm.ShowPluginDialog = r => { dialogs.Add(r); return Task.FromResult(answer(r)); };
        var host = _box.NewHost(appVersion: appVersion);
        vm.Plugins = host;
        var server = new PluginCatalogTests.FakeHandler();
        vm.PluginCatalogClient = new PluginCatalogClient(new HttpClient(server), () => null) { Embedded = () => PluginCatalog.Empty };
        return new Mounted(vm, host, dialogs, persistence, server);
    }

    private byte[] ZipBytes(string version, string id = "dev.test.zip")
        => File.ReadAllBytes(_box.WriteZip($"{id}-{version}.zip",
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: id, version: version))),
            ("Test.Plugin.dll", new byte[] { 1 })));

    /// <summary>Serves <paramref name="zip"/> at <see cref="Url"/> and lists it as dev.test.zip <paramref name="version"/>.</summary>
    private static void Publish(Mounted m, byte[] zip, string version, string? sha = null, string? minApp = null)
    {
        m.Server[Url] = zip;
        m.Server[PluginCatalog.DefaultUrl] = System.Text.Encoding.UTF8.GetBytes(PluginCatalogTests.Index(PluginCatalogTests.Entry(
            id: "dev.test.zip", name: "Test Plugin", version: version, download: Url,
            sha256: sha ?? PluginCatalogTests.Hash(zip), size: zip.LongLength, minAppVersion: minApp)));
    }

    [AvaloniaFact]
    public async Task Install_DownloadsVerifiesAndInstalls_ThenTheButtonSaysInstalled()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false));
        _box.Settings.CommunityPluginsEnabled = true;
        m.Host.LoadAll();
        Publish(m, ZipBytes("1.0.0"), "1.0.0");

        await m.Vm.LoadOfficialPluginsAsync();
        var item = Assert.Single(m.Vm.OfficialPlugins);
        Assert.Equal(OfficialPluginState.Install, item.State);
        Assert.Equal(Loc.T("Plugins.Get.Install"), item.ButtonText);
        Assert.True(item.IsPrimary);

        await m.Vm.InstallOfficialPluginAsync(item);

        var plugin = Assert.Single(m.Host.Plugins);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.False(plugin.IsEnabled); // switched on by hand, with the usual permission prompt
        Assert.Empty(m.Dialogs);        // community plugins were already on
        Assert.Equal(Loc.T("Plugins.Installed", "Test Plugin", "1.0.0"), m.Vm.PluginsStatus);
        Assert.Equal(OfficialPluginState.Installed, item.State);
        Assert.False(item.CanClick);
        Assert.False(item.IsBusy);
    }

    [AvaloniaFact]
    public async Task Install_WhileCommunityPluginsAreOff_OffersTheSwitchsOwnConfirmation()
    {
        var confirm = false;
        using var m = Mount(_ => new ConfirmationResult(confirm, false));
        m.Host.LoadAll(); // fresh: restricted
        Publish(m, ZipBytes("1.0.0"), "1.0.0");
        await m.Vm.LoadOfficialPluginsAsync();

        await m.Vm.InstallOfficialPluginAsync(m.Vm.OfficialPlugins[0]);

        var dialog = Assert.Single(m.Dialogs);
        Assert.Equal(Loc.T("Plugins.TurnOnTitle"), dialog.Title);
        Assert.False(m.Host.CommunityPluginsEnabled); // declined: never switched on silently
        Assert.Equal(PluginStatus.Restricted, Assert.Single(m.Host.Plugins).Status);

        // Removing and installing again, this time saying yes.
        m.Host.Remove(m.Host.Plugins[0], deleteData: true);
        confirm = true;
        await m.Vm.InstallOfficialPluginAsync(m.Vm.OfficialPlugins[0]);
        Assert.Equal(2, m.Dialogs.Count);
        Assert.True(m.Host.CommunityPluginsEnabled);
        Assert.True(m.Vm.CommunityPluginsEnabled);
        Assert.Single(m.Host.Plugins);
    }

    [AvaloniaFact]
    public async Task Install_OfAZipThatDoesNotMatchTheList_InstallsNothing()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false));
        _box.Settings.CommunityPluginsEnabled = true;
        m.Host.LoadAll();
        Publish(m, ZipBytes("1.0.0"), "1.0.0", sha: new string('0', 64));
        await m.Vm.LoadOfficialPluginsAsync();
        var item = m.Vm.OfficialPlugins[0];

        await m.Vm.InstallOfficialPluginAsync(item);

        Assert.Empty(m.Host.Plugins);
        Assert.False(Directory.Exists(m.Host.PluginsDirectory) && Directory.EnumerateFileSystemEntries(m.Host.PluginsDirectory).Any());
        Assert.Equal(Loc.T("Plugins.Get.Mismatch"), item.Error);
        Assert.Equal(Loc.T("Plugins.Get.Retry"), item.ButtonText);
        Assert.True(item.CanClick);
    }

    [AvaloniaFact]
    public async Task Install_OfAnotherPluginsZip_IsRefused()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false));
        _box.Settings.CommunityPluginsEnabled = true;
        m.Host.LoadAll();
        Publish(m, ZipBytes("1.0.0", id: "dev.test.other"), "1.0.0"); // hash matches, contents do not

        await m.Vm.LoadOfficialPluginsAsync();
        await m.Vm.InstallOfficialPluginAsync(m.Vm.OfficialPlugins[0]);

        Assert.Empty(m.Host.Plugins);
        Assert.Equal(Loc.T("Plugins.Get.WrongPackage", "Test Plugin", "1.0.0"), m.Vm.PluginsStatus);
    }

    [AvaloniaFact]
    public async Task Update_ReplacesTheOldVersion_KeepsItsData_WithoutASecondDialog()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false));
        _box.Settings.CommunityPluginsEnabled = true;
        m.Host.LoadAll();
        var old = _box.WriteZip("old.zip",
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: "dev.test.zip", version: "1.0.0"))),
            ("Test.Plugin.dll", new byte[] { 1 }));
        Assert.True(await m.Vm.InstallPluginPackageAsync(old));
        var dataFile = Path.Combine(m.Host.Plugins[0].DataDirectory, "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        File.WriteAllText(dataFile, "x");

        Publish(m, ZipBytes("1.1.0"), "1.1.0");
        await m.Vm.LoadOfficialPluginsAsync();
        var item = m.Vm.OfficialPlugins[0];
        Assert.Equal(OfficialPluginState.Update, item.State);
        Assert.Equal(Loc.T("Plugins.Get.Update"), item.ButtonText);

        await m.Vm.InstallOfficialPluginAsync(item);

        Assert.Equal("1.1.0", Assert.Single(m.Host.Plugins).Version);
        Assert.True(File.Exists(dataFile));
        Assert.Empty(m.Dialogs);
        Assert.Equal(OfficialPluginState.Installed, item.State);
    }

    [AvaloniaFact]
    public async Task List_ShowsWhatThisNoctisCannotRun_AndFallsBackWhenOffline()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false), appVersion: "1.5.8");
        m.Host.LoadAll();
        Publish(m, ZipBytes("1.0.0"), "1.0.0", minApp: "1.5.9");

        await m.Vm.LoadOfficialPluginsAsync();
        var item = m.Vm.OfficialPlugins[0];
        Assert.Equal(OfficialPluginState.NeedsNewerApp, item.State);
        Assert.Equal(Loc.T("Plugins.Get.RequiresApp", "1.5.9"), item.ButtonText);
        Assert.False(item.CanClick);

        // Offline: the built-in copy stands in, quietly.
        var builtIn = PluginCatalog.Parse(PluginCatalogTests.Index(PluginCatalogTests.Entry(id: "dev.test.builtin", minAppVersion: null)));
        m.Vm.PluginCatalogClient = new PluginCatalogClient(new HttpClient(new PluginCatalogTests.FakeHandler()), () => null) { Embedded = () => builtIn };
        await m.Vm.LoadOfficialPluginsAsync();
        Assert.Equal("dev.test.builtin", Assert.Single(m.Vm.OfficialPlugins).Entry.Id);
        Assert.False(m.Vm.IsOfficialPluginsLoading);
    }

    [AvaloniaFact]
    public async Task OpeningThePluginsPage_FetchesOnce_StartupDoesNot()
    {
        using var m = Mount(_ => new ConfirmationResult(true, false));
        Publish(m, ZipBytes("1.0.0"), "1.0.0");
        Assert.Empty(m.Server.Requests);

        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        await WaitFor(() => !m.Vm.IsOfficialPluginsLoading && m.Server.Requests.Count == 1);
        Assert.Single(m.Vm.OfficialPlugins);

        m.Vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        await WaitFor(() => !m.Vm.IsOfficialPluginsLoading);
        Assert.Single(m.Server.Requests); // loaded once this session; Refresh fetches again

        await m.Vm.RefreshOfficialPluginsCommand.ExecuteAsync(null);
        Assert.Equal(2, m.Server.Requests.Count);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
