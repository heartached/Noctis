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
/// Settings → Plugins layout: the Community plugins switch first, then Official Plugins, then
/// Custom Category (Install from file plus folder and reload icon buttons, the folder path in a
/// tooltip) holding what the user added. With the switch off, a code plugin's row greys out.
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
    public void SwitchOff_GreysOutCodePlugins_WithoutARestrictedBadge_AndTurningItOnAsksFirst()
    {
        var (vm, host, dialogs, persistence) = Mount(confirm: true);
        using var _p = persistence;
        AddContentPack(host); // data only: works with the switch off
        AddCodePlugin(host);
        Assert.False(host.CommunityPluginsEnabled);

        var code = host.FindById("dev.test.code")!;
        Assert.True(code.IsRestricted);       // the row greys out (StackPanel.plugin-body.restricted)
        Assert.False(code.ShowStatusBadge);   // instead of a "Restricted" pill
        Assert.False(code.CanToggle);
        Assert.False(host.Plugins.Single(p => p.IsContentPack).IsRestricted);

        vm.CommunityPluginsEnabled = true;

        Assert.Equal(Loc.T("Plugins.TurnOnTitle"), Assert.Single(dialogs).Title);
        Assert.True(host.CommunityPluginsEnabled);
        code = host.FindById("dev.test.code")!; // the switch reloads every plugin (LoadAll)
        Assert.False(code.IsRestricted);
        Assert.False(code.ShowStatusBadge); // on/off is the switch, not a pill

        code.Status = PluginStatus.Failed;  // a problem still gets one
        Assert.True(code.ShowStatusBadge);
    }

    [AvaloniaFact]
    public void Page_IsTheSwitch_ThenOfficial_ThenCustom_WithEveryCommandWired()
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
            Assert.True(IndexOf("CommunityPluginsCard") < official);
            Assert.True(official < IndexOf("CustomPluginsCard"));

            // The hand-installed test plugin is not on the official list: it is a Custom row.
            Assert.Equal("dev.test.code", Assert.Single(vm.CustomPlugins).Id);

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
