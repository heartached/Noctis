using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings → Plugins flows: the approval dialog's content, the community switch,
/// install/update/remove confirmations.</summary>
public class PluginSettingsFlowTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private (SettingsViewModel Vm, PluginHost Host, List<ConfirmationRequest> Dialogs, TestPersistenceService Persistence) Mount(Func<ConfirmationRequest, ConfirmationResult> answer)
    {
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        var dialogs = new List<ConfirmationRequest>();
        vm.ShowPluginDialog = r => { dialogs.Add(r); return Task.FromResult(answer(r)); };
        var host = _box.NewHost();
        vm.Plugins = host;
        return (vm, host, dialogs, persistence);
    }

    [AvaloniaFact]
    public void FirstEnable_ShowsThePermissions_AndAnHonestSafetyNote()
    {
        var (vm, host, dialogs, persistence) = Mount(_ => new ConfirmationResult(false, false));
        using var _ = persistence;
        _box.Settings.CommunityPluginsEnabled = true;
        var plugin = _box.AddInProcess(host, new ScriptedPlugin(),
            PluginSandbox.Manifest(id: "dev.test.plugin", permissions: new[] { "playback.control", "network" }));

        plugin.IsEnabled = true;

        var d = Assert.Single(dialogs);
        Assert.Equal(Loc.T("Plugins.EnableTitle", "Test Plugin"), d.Title);
        Assert.Contains("Test Plugin 1.0.0 by Tests", d.Message);
        Assert.Equal(new[] { Loc.T("Plugins.Perm.PlaybackControl"), Loc.T("Plugins.Perm.Network") }, d.Details);
        Assert.Contains("same access", d.Note);
        Assert.Contains("not a sandbox", d.Note);
        Assert.False(plugin.IsRunning);
        Assert.False(plugin.IsEnabled);
    }

    [AvaloniaFact]
    public void CommunitySwitch_AsksBeforeTurningOn_AndFollowsTheHost()
    {
        var confirm = false;
        var (vm, host, dialogs, persistence) = Mount(_ => new ConfirmationResult(confirm, false));
        using var _ = persistence;
        host.LoadAll(); // fresh: restricted
        Assert.False(vm.CommunityPluginsEnabled);

        vm.CommunityPluginsEnabled = true;
        Assert.Single(dialogs);
        Assert.False(host.CommunityPluginsEnabled);
        Assert.False(vm.CommunityPluginsEnabled); // switch snaps back

        confirm = true;
        vm.CommunityPluginsEnabled = true;
        Assert.True(host.CommunityPluginsEnabled);
        Assert.True(vm.CommunityPluginsEnabled);

        vm.CommunityPluginsEnabled = false; // turning off never asks
        Assert.Equal(2, dialogs.Count);
        Assert.False(host.CommunityPluginsEnabled);
    }

    [AvaloniaFact]
    public async Task Install_Update_Remove_GoThroughConfirmations()
    {
        var removeData = false;
        var (vm, host, dialogs, persistence) = Mount(r => new ConfirmationResult(true, r.OptionText is not null && removeData));
        using var _ = persistence;
        _box.Settings.CommunityPluginsEnabled = true;
        host.LoadAll();
        string Zip(string v) => _box.WriteZip($"p-{v}.zip",
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: "dev.test.zip", version: v))),
            ("Test.Plugin.dll", new byte[] { 1 }));

        await vm.InstallPluginPackageAsync(Zip("1.0.0"));
        Assert.Empty(dialogs);
        Assert.Equal(Loc.T("Plugins.Installed", "Test Plugin", "1.0.0"), vm.PluginsStatus);

        await vm.InstallPluginPackageAsync(Zip("1.0.0"));
        Assert.Empty(dialogs);
        Assert.Contains("already installed", vm.PluginsStatus);

        await vm.InstallPluginPackageAsync(Zip("1.1.0"));
        Assert.Equal(Loc.T("Plugins.UpdateTitle", "Test Plugin", "1.0.0", "1.1.0"), Assert.Single(dialogs).Title);
        Assert.Equal("1.1.0", host.Plugins.Single().Version);

        var plugin = host.Plugins.Single();
        Directory.CreateDirectory(plugin.DataDirectory);
        await vm.RemovePluginCommand.ExecuteAsync(plugin);
        Assert.Equal(Loc.T("Plugins.RemoveDeleteData"), dialogs[^1].OptionText);
        Assert.Empty(host.Plugins);
        Assert.True(Directory.Exists(plugin.DataDirectory)); // box left unticked: data kept

        await vm.InstallPluginPackageAsync(_box.WriteZip("bad.zip", ("x.txt", new byte[1])));
        Assert.StartsWith(Loc.T("Plugins.InstallFailed", "").TrimEnd(), vm.PluginsStatus);
    }
}
