using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Plugins layout: Get plugins, then Installed (Install from file plus folder and
/// reload icon buttons, the folder path in a tooltip), then the Community plugins switch with
/// one line. The "Plugins are off" notice shows only when it blocks an installed code plugin.
/// </summary>
public class PluginsPageLayoutTests : IDisposable
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

    private (SettingsViewModel Vm, PluginHost Host, List<ConfirmationRequest> Dialogs, TestPersistenceService Persistence) Mount(bool confirm = false)
    {
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        var dialogs = new List<ConfirmationRequest>();
        vm.ShowPluginDialog = r => { dialogs.Add(r); return Task.FromResult(new ConfirmationResult(confirm, false)); };
        var host = _box.NewHost();
        vm.Plugins = host;
        return (vm, host, dialogs, persistence);
    }

    private void AddCodePlugin(PluginHost host)
    {
        // A first LoadAll that already finds a plugin keeps it running (installed before the switch existed).
        _box.Settings.CommunityPluginsEnabled ??= false;
        _box.WriteFolder("dev.test.code", PluginSandbox.Manifest(id: "dev.test.code"), ("Test.Plugin.dll", new byte[] { 1 }));
        host.LoadAll();
    }

    private void AddContentPack(PluginHost host)
    {
        _box.WriteFolder("dev.test.pack", ContentPackKit.Manifest(), ("t.json", PluginSandbox.Utf8(ContentPackKit.Theme)));
        host.LoadAll();
    }

    [AvaloniaFact]
    public void OffNotice_OnlyWhenCommunityPluginsAreOff_AndACodePluginIsInstalled()
    {
        var (vm, host, _, persistence) = Mount();
        using var _p = persistence;
        host.LoadAll(); // fresh: restricted, nothing installed
        Assert.False(host.CommunityPluginsEnabled);
        Assert.False(vm.ShowPluginsOffNotice);

        AddContentPack(host); // data only: works with the switch off
        Assert.False(vm.ShowPluginsOffNotice);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        AddCodePlugin(host);
        Assert.True(vm.ShowPluginsOffNotice);
        Assert.Contains(nameof(SettingsViewModel.ShowPluginsOffNotice), changed);

        host.SetCommunityPluginsEnabled(true);
        Assert.False(vm.ShowPluginsOffNotice);
    }

    [AvaloniaFact]
    public void TurnOn_GoesThroughTheSwitchsConfirmation()
    {
        var (vm, host, dialogs, persistence) = Mount(confirm: true);
        using var _p = persistence;
        AddCodePlugin(host);
        Assert.True(vm.ShowPluginsOffNotice);

        vm.TurnOnCommunityPluginsCommand.Execute(null);

        Assert.Equal(Loc.T("Plugins.TurnOnTitle"), Assert.Single(dialogs).Title);
        Assert.True(host.CommunityPluginsEnabled);
        Assert.False(vm.ShowPluginsOffNotice);
    }

    [AvaloniaFact]
    public void Page_IsGetPlugins_ThenInstalled_ThenTheSwitch_WithEveryCommandWired()
    {
        var (vm, host, _, persistence) = Mount();
        using var _p = persistence;
        AddCodePlugin(host);
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 900, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var panel = view.FindControl<StackPanel>("PluginsTabPanel")!;
            int IndexOf(string name) => panel.Children.IndexOf(view.FindControl<Control>(name)!);
            var official = panel.Children.IndexOf(view.FindControl<ItemsControl>("OfficialPluginsList")!.Parent as Control ?? throw new InvalidOperationException());
            Assert.True(official >= 0);
            Assert.True(official < IndexOf("InstalledPluginsCard"));
            Assert.True(IndexOf("InstalledPluginsCard") < IndexOf("CommunityPluginsCard"));

            // The official list shows the built-in copy at once (no client = no fetch in tests).
            Assert.NotEmpty(vm.OfficialPlugins);

            Assert.Same(vm.RefreshOfficialPluginsCommand, view.FindControl<Button>("RefreshOfficialPluginsButton")!.Command);
            Assert.Same(vm.InstallPluginFromFileCommand, view.FindControl<Button>("InstallPluginFromFileButton")!.Command);
            Assert.Same(vm.ReloadPluginsCommand, view.FindControl<Button>("ReloadPluginsButton")!.Command);
            Assert.Equal(Loc.T("Plugins.Reload"), ToolTip.GetTip(view.FindControl<Button>("ReloadPluginsButton")!));

            var folder = view.FindControl<Button>("OpenPluginsFolderButton")!;
            Assert.Same(vm.OpenPluginsFolderCommand, folder.Command);
            var tip = Assert.IsType<string>(ToolTip.GetTip(folder));
            Assert.StartsWith(Loc.T("Plugins.OpenFolder"), tip);
            Assert.EndsWith(host.PluginsDirectory, tip);

            Assert.True(view.FindControl<Border>("PluginsOffNotice")!.IsVisible);
            Assert.Same(vm.TurnOnCommunityPluginsCommand, view.FindControl<Button>("TurnOnCommunityPluginsButton")!.Command);
            Assert.NotNull(view.FindControl<LottieToggle>("CommunityPluginsToggle"));

            // The path is no longer a line of its own on the page.
            Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == host.PluginsDirectory);

            // No hairline above a list's first row (it showed above Mixxx); none on the status line either.
            var firstRow = view.FindControl<ItemsControl>("OfficialPluginsList")!
                .GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("setting-row"));
            Assert.Equal(new Thickness(0), firstRow.BorderThickness);
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text?.Contains("GitHub") == true);
        }
        finally { window.Close(); }
    }
}
