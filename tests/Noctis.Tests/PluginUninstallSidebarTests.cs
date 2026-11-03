using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord "Plugins feedback" (Mistery, 11-03): uninstall for every installed plugin (a view open
/// in place closes, only its folder goes, and it stays gone after a restart even when a file was
/// held), and the screens plugins add get sidebar entries the user can hide per plugin.
/// </summary>
public class PluginUninstallSidebarTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        _box.Dispose();
    }

    private const string Id = "dev.test.town";

    private sealed class View : IPluginViewProvider
    {
        public string Name => "Town";
        public int Created;
        public Control CreateView()
        {
            Created++;
            return new Border { Child = new Button { Content = "Inside" } };
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed record Mounted(SettingsViewModel Vm, PluginHost Host, LoadedPlugin Plugin, View View,
        PluginViewEntry Entry, TestPersistenceService Persistence) : IDisposable
    {
        public void Dispose() => Persistence.Dispose();
    }

    /// <summary>Settings on a host running one plugin ("Town Plugin") with one view ("Town").</summary>
    private Mounted Mount()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.6.6");
        var view = new View();
        var plugin = _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.RegisterView(view) },
            PluginSandbox.Manifest(id: Id, name: "Town Plugin", apiVersion: "1.5"));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        host.ViewPresenter = (_, _) => true;
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory())
        {
            PluginCatalogClient = new PluginCatalogClient(new HttpClient(new PluginCatalogTests.FakeHandler()), () => null)
            {
                Embedded = () => PluginCatalog.Empty,
            },
        };
        vm.Plugins = host;
        vm.ShowPluginDialog = _ => Task.FromResult(new ConfirmationResult(true, false));
        return new Mounted(vm, host, plugin, view, plugin.Views.Single(), persistence);
    }

    private static void Pump(int ms = 60)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
    }

    // ── Uninstall ──

    [AvaloniaFact]
    public async Task Uninstall_WhileItsViewIsOpen_ClosesTheView_DeletesOnlyItsFolder_AndStaysGoneAfterARestart()
    {
        using var m = Mount();
        var other = _box.WriteFolder("dev.test.other", PluginSandbox.Manifest(id: "dev.test.other", name: "Other"),
            ("Test.Plugin.dll", new byte[] { 1 }));
        Directory.CreateDirectory(m.Plugin.DataDirectory);
        File.WriteAllText(Path.Combine(m.Plugin.DataDirectory, "state.json"), "{}");

        Assert.True(m.Vm.ShowPluginViewFromSidebar(m.Entry));
        Assert.Same(m.Entry, m.Vm.OpenPluginViewEntry);

        await m.Vm.RemovePluginCommand.ExecuteAsync(m.Plugin);

        Assert.True(m.Entry.IsWithdrawn);
        Assert.Null(m.Vm.OpenPluginViewEntry);
        Assert.Null(m.Vm.PluginViewContent);
        Assert.Null(m.Vm.SelectedPluginCard); // its card went with it
        Assert.False(Directory.Exists(m.Plugin.Directory));
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(Path.Combine(m.Plugin.DataDirectory, "state.json"))); // data kept unless asked
        // A second click (a stale menu) does nothing.
        var asked = 0;
        m.Vm.ShowPluginDialog = _ => { asked++; return Task.FromResult(new ConfirmationResult(true, false)); };
        await m.Vm.RemovePluginCommand.ExecuteAsync(m.Plugin);
        Assert.Equal(0, asked);

        var restarted = _box.NewHost(appVersion: "1.6.6");
        restarted.LoadAll();
        Assert.Null(restarted.FindById(Id));
        Assert.NotNull(restarted.FindById("dev.test.other"));
    }

    [AvaloniaFact]
    public void LeftoverFolder_MarkedUninstalled_IsDeletedAtTheNextStart_NotLoaded_AndAnInstallCanUseItsName()
    {
        var left = _box.WriteFolder("dev.test.left", PluginSandbox.Manifest(id: "dev.test.left", name: "Left"),
            ("Test.Plugin.dll", new byte[] { 1 }));
        PluginInstaller.MarkUninstalled(left);
        _box.Settings.CommunityPluginsEnabled = true;
        var host = _box.NewHost(appVersion: "1.6.6");
        host.LoadAll();
        Assert.Null(host.FindById("dev.test.left"));
        Assert.False(Directory.Exists(left));

        // Left over again (the next start has not run yet): installing the same id clears it.
        left = _box.WriteFolder("dev.test.left", PluginSandbox.Manifest(id: "dev.test.left", name: "Left"),
            ("Test.Plugin.dll", new byte[] { 1 }));
        PluginInstaller.MarkUninstalled(left);
        var zip = _box.WriteZip("left.zip",
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: "dev.test.left", name: "Left", version: "1.0.1"))),
            ("Test.Plugin.dll", new byte[] { 2 }));
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(left, PluginInstaller.UninstallMarker)));

        // An ordinary folder of that name (no mark) still blocks the install, as before.
        _box.WriteFolder("dev.test.taken", null, ("x.txt", new byte[] { 1 }));
        var taken = _box.WriteZip("taken.zip",
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: "dev.test.taken"))), ("Test.Plugin.dll", new byte[] { 2 }));
        Assert.Equal(PluginInstallOutcome.Failed, host.InstallPackage(taken, allowUpdate: false).Outcome);
    }

    [AvaloniaFact]
    public void Uninstall_WithAFileStillHeld_MarksTheFolder_SoTheNextStartFinishesIt()
    {
        if (!OperatingSystem.IsWindows()) return; // only Windows refuses to delete an open file
        using var m = Mount();
        var held = Path.Combine(m.Plugin.Directory, "held.bin");
        File.WriteAllBytes(held, new byte[] { 1 });
        bool removed;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = m.Host.Remove(m.Plugin, deleteData: false);
            Assert.True(File.Exists(Path.Combine(m.Plugin.Directory, PluginInstaller.UninstallMarker)));
        }
        Assert.False(removed);
        Assert.Null(m.Host.FindById(Id));

        var restarted = _box.NewHost(appVersion: "1.6.6");
        restarted.LoadAll();
        Assert.Null(restarted.FindById(Id));
        Assert.False(Directory.Exists(m.Plugin.Directory));
    }

    // ── Sidebar ──

    [AvaloniaFact]
    public void Sidebar_ListsRunningPluginScreens_AndHideFromSidebarIsSavedPerPlugin()
    {
        using var m = Mount();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService());
        var raised = 0;
        m.Host.ViewsChanged += (_, _) => raised++;
        sidebar.SyncPluginItems(m.Host.Views);

        var item = Assert.Single(sidebar.PluginItems);
        Assert.True(sidebar.HasPluginItems);
        Assert.Equal("Town", item.Label);
        Assert.Same(m.Entry, item.View);
        Assert.StartsWith(PluginNavItem.KeyPrefix, item.Key);
        Assert.Equal("TP", item.Monogram); // no icon in plugin.json: the letter tile
        Assert.True(SidebarViewModel.OpensSheet(item)); // never takes the selection, like Settings

        sidebar.HidePluginItemCommand.Execute(item);
        Assert.False(m.Plugin.ShowInSidebar);
        Assert.Contains(Id, _box.Settings.SidebarHiddenPlugins);
        Assert.Equal(1, raised);
        sidebar.SyncPluginItems(m.Host.Views);
        Assert.Empty(sidebar.PluginItems);
        Assert.False(sidebar.HasPluginItems);

        // Remembered across a restart.
        var restarted = _box.NewHost(appVersion: "1.6.6");
        var again = _box.AddInProcess(restarted, new ScriptedPlugin { OnInit = h => h.RegisterView(new View()) },
            PluginSandbox.Manifest(id: Id, name: "Town Plugin", apiVersion: "1.5"));
        Assert.False(again.ShowInSidebar);

        // Back on from the plugin's page switch.
        m.Plugin.ShowInSidebar = true;
        Assert.DoesNotContain(Id, _box.Settings.SidebarHiddenPlugins);
        sidebar.SyncPluginItems(m.Host.Views);
        Assert.Single(sidebar.PluginItems);

        // The plugin switched off: its screen leaves the sidebar.
        m.Plugin.IsEnabled = false;
        sidebar.SyncPluginItems(m.Host.Views);
        Assert.Empty(sidebar.PluginItems);
    }

    [AvaloniaFact]
    public void Sidebar_RegisteringAViewLater_RaisesViewsChanged()
    {
        using var m = Mount();
        var raised = 0;
        m.Host.ViewsChanged += (_, _) => raised++;
        var later = ((ScriptedPlugin)m.Plugin.Instance!).Host!.RegisterView(new View());
        Assert.Equal(1, raised);
        Assert.Equal(2, m.Host.Views.Count);
        later.Dispose();
        Pump();
        Assert.Equal(2, raised);
        Assert.Single(m.Host.Views);
    }

    [AvaloniaFact]
    public void SidebarEntry_OpensTheScreenInSettings_InPlaceOfItsPage_OrBringsItsWindowForward()
    {
        using var m = Mount();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;

        Assert.True(m.Vm.ShowPluginViewFromSidebar(m.Entry));
        Assert.Equal(SettingsViewModel.TabPlugins, m.Vm.SelectedSettingsTab);
        Assert.Same(m.Plugin, m.Vm.SelectedPluginCard!.Installed);
        Assert.Same(m.Entry, m.Vm.OpenPluginViewEntry);
        Assert.Equal(1, m.View.Created);
        // Clicked again while it is up: the same control, nothing rebuilt.
        Assert.True(m.Vm.ShowPluginViewFromSidebar(m.Entry));
        Assert.Equal(1, m.View.Created);

        // Open in its own window instead: that window comes forward, Settings stays as it is.
        m.Vm.LeavePluginView();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        var raised = 0;
        m.Entry.Reactivate = () => { raised++; return true; };
        Assert.False(m.Vm.ShowPluginViewFromSidebar(m.Entry));
        Assert.Equal(1, raised);
        Assert.Equal(SettingsViewModel.TabGeneral, m.Vm.SelectedSettingsTab);
        m.Entry.Reactivate = null;

        // Plugin Settings from the row's menu: just the page.
        Assert.True(m.Vm.OpenPluginPageFor(m.Plugin));
        Assert.Equal(SettingsViewModel.TabPlugins, m.Vm.SelectedSettingsTab);
        Assert.Same(m.Plugin, m.Vm.SelectedPluginCard!.Installed);
        Assert.Null(m.Vm.OpenPluginViewEntry);
    }

    [AvaloniaFact]
    public void PluginPage_HasAShowInSidebarSwitch_ForAPluginWithAScreen()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var m = Mount();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        _windows.Add(window);
        window.Show();
        Pump();
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == Id));
        Pump(450);

        var toggle = view.GetVisualDescendants().OfType<Noctis.Controls.LottieToggle>().Single(t => t.Name == "PluginPageSidebarToggle");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.True(toggle.IsChecked);
        m.Plugin.ShowInSidebar = false;
        Pump();
        Assert.False(toggle.IsChecked);
        Assert.Contains(Id, _box.Settings.SidebarHiddenPlugins);
    }

    [AvaloniaFact]
    public void SidebarView_ShowsThePluginsSection_OnlyWhileAPluginAddsAScreen()
    {
        using var m = Mount();
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService())
        {
            TopBar = new TopBarViewModel(),
            IsExpanded = true,
        };
        var view = new SidebarView { DataContext = vm };
        var window = new Window { Width = 260, Height = 900, Content = view };
        _windows.Add(window);
        window.Show();
        Pump();
        var section = view.FindControl<StackPanel>("PluginSection")!;
        Assert.False(section.IsVisible);

        vm.SyncPluginItems(m.Host.Views);
        Pump();
        Assert.True(section.IsVisible);
        var row = view.FindControl<ListBox>("PluginList")!.GetVisualDescendants().OfType<ListBoxItem>().Single();
        Assert.Contains("Town", row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.NotNull(row.ContextMenu);

        // A click opens the screen without moving the highlight (the view asks, the selection stays).
        string? asked = null;
        vm.NavigationRequested += (_, key) => asked = key;
        view.FindControl<ListBox>("PluginList")!.SelectedItem = vm.PluginItems[0];
        Pump();
        Assert.Equal(vm.PluginItems[0].Key, asked);
        Assert.Null(vm.SelectedNavItem);
        Assert.Null(view.FindControl<ListBox>("PluginList")!.SelectedItem);
    }
}
