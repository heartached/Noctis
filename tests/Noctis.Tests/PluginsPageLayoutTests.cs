using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
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
/// tooltip) holding what the user added, then Make your own plugin (three steps, Read the Guide,
/// Submit a Plugin). With the switch off, a code plugin's row greys out.
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
            // Make your own plugin closes the page.
            Assert.Equal(panel.Children.Count - 1, IndexOf("MakePluginCard"));

            // The hand-installed test plugin is not on the official list: it is a Custom row.
            Assert.Equal("dev.test.code", Assert.Single(vm.CustomPlugins).Id);

            // The official list shows the built-in copy at once (no client = no fetch in tests).
            Assert.NotEmpty(vm.OfficialPlugins);

            Assert.Same(vm.RefreshOfficialPluginsCommand, view.FindControl<Button>("RefreshOfficialPluginsButton")!.Command);
            // Submit a Plugin lives on the Make your own plugin card, no longer in the Official header.
            var submit = view.FindControl<Button>("SubmitPluginButton")!;
            Assert.Same(vm.SubmitPluginCommand, submit.Command);
            Assert.Contains(view.FindControl<Border>("MakePluginCard")!, submit.GetLogicalAncestors());
            Assert.Same(vm.OpenPluginGuideCommand, view.FindControl<Button>("ReadPluginGuideButton")!.Command);
            // Each official row credits its author ("by Noctis" for Mixxx).
            Assert.Contains(view.FindControl<ItemsControl>("OfficialPluginsList")!.GetLogicalDescendants().OfType<TextBlock>(),
                t => t.Text == Loc.T("Plugins.Get.By", "Noctis") && t.IsVisible);
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
            // (Make your own plugin says "Publish it on GitHub" on purpose: a step, not a status line.)
            var makeCard = view.FindControl<Border>("MakePluginCard")!;
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text?.Contains("GitHub") == true && !t.GetVisualAncestors().Contains(makeCard));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MakeYourOwnPlugin_ShowsThreeSteps_InARow_StackedWhenNarrow_GuideThenSubmit()
    {
        var (vm, _, _, persistence) = Mount();
        using var _p = persistence;
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1300, Height = 900, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var card = view.FindControl<Border>("MakePluginCard")!;
            Assert.True(card.IsEffectivelyVisible);
            var texts = card.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            foreach (var key in new[] { "Plugins.Make.Title", "Plugins.Make.Line", "Plugins.Make.Step1Title", "Plugins.Make.Step1",
                         "Plugins.Make.Step2Title", "Plugins.Make.Step2", "Plugins.Make.Step3Title", "Plugins.Make.Step3",
                         "Plugins.Make.Guide", "Plugins.Submit" })
                Assert.Contains(Loc.T(key), texts);

            // Wide: one row, left to right, all three the same top.
            var steps = view.FindControl<Grid>("MakePluginSteps")!;
            Assert.True(steps.Bounds.Width >= SettingsView.MakePluginStepsStackWidth, $"steps {steps.Bounds.Width} wide");
            var boxes = steps.Children.Select(c => c.Bounds).ToList();
            Assert.Equal(3, boxes.Count);
            Assert.All(boxes, b => Assert.Equal(boxes[0].Y, b.Y));
            Assert.True(boxes[0].Right <= boxes[1].X && boxes[1].Right <= boxes[2].X);
            var customActions = view.FindControl<StackPanel>("CustomPluginsActions")!;
            Assert.Equal(0, Grid.GetRow(customActions)); // Custom Category's buttons beside its title

            // Narrow pane (a small window at a large Interface Size): stacked, one under another,
            // each the full width.
            window.Width = 640;
            for (var i = 0; i < 2; i++)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            Assert.True(steps.Bounds.Width < SettingsView.MakePluginStepsStackWidth, $"steps {steps.Bounds.Width} wide");
            boxes = steps.Children.Select(c => c.Bounds).ToList();
            Assert.All(boxes, b => Assert.Equal(boxes[0].X, b.X));
            Assert.True(boxes[0].Bottom <= boxes[1].Y && boxes[1].Bottom <= boxes[2].Y);
            Assert.All(boxes, b => Assert.Equal(steps.Bounds.Width, b.Width, 1));

            // Custom Category's buttons drop under the title, and nothing on the page runs past its
            // right edge (it did: the header's buttons, and "Requires Noctis 1.5.9" on an official row).
            Assert.Equal(1, Grid.GetRow(customActions));
            var page = view.FindControl<StackPanel>("PluginsTabPanel")!;
            foreach (var b in page.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var right = b.TranslatePoint(new Point(b.Bounds.Width, 0), page)!.Value.X;
                Assert.True(right <= page.Bounds.Width + 0.5, $"{b.Name ?? b.GetType().Name} in{string.Join(" < ", b.GetLogicalAncestors().OfType<Control>().Where(a => !string.IsNullOrEmpty(a.Name)).Take(2).Select(a => a.Name))} ends at {right}, page is {page.Bounds.Width}");
            }

            // Read the Guide, then Submit a Plugin: in reading order and in Tab order, both named.
            var guide = view.FindControl<Button>("ReadPluginGuideButton")!;
            var submit = view.FindControl<Button>("SubmitPluginButton")!;
            Assert.Equal(Loc.T("Plugins.Make.Guide"), AutomationProperties.GetName(guide));
            Assert.Equal(Loc.T("Plugins.Submit"), AutomationProperties.GetName(submit));
            Assert.True(guide.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(submit, window.FocusManager?.GetFocusedElement());
            Assert.Contains("accent-pill", submit.Classes);
            Assert.Contains("pill-action", guide.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MoreIntegrations_BrowsePlugins_SwitchesToThePluginsTab()
    {
        var (vm, _, _, persistence) = Mount();
        using var _p = persistence;
        vm.SelectedSettingsTab = SettingsViewModel.TabIntegrations;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 900, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var panel = view.FindControl<StackPanel>("IntegrationsTabPanel")!;
            var card = view.FindControl<Border>("MoreIntegrationsCard")!;
            Assert.Equal(panel.Children.Count - 1, panel.Children.IndexOf(card)); // last on the page
            Assert.True(card.IsEffectivelyVisible);

            var browse = view.FindControl<Button>("BrowsePluginsButton")!;
            Assert.Equal(Loc.T("Integrations.More.Browse"), AutomationProperties.GetName(browse));
            Assert.True(browse.Command!.CanExecute(browse.CommandParameter));
            browse.Command.Execute(browse.CommandParameter);
            Assert.Equal(SettingsViewModel.TabPlugins, vm.SelectedSettingsTab);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void MakeYourOwnPlugin_Strings_HaveNoEmDash_AndTheGuideIsOnTheWebsite()
    {
        string[] keys =
        {
            "Plugins.Make.Title", "Plugins.Make.Line", "Plugins.Make.Step1Title", "Plugins.Make.Step1", "Plugins.Make.Step2Title",
            "Plugins.Make.Step2", "Plugins.Make.Step3Title", "Plugins.Make.Step3", "Plugins.Make.Guide", "Plugins.Submit",
            "Integrations.More.Title", "Integrations.More.Line", "Integrations.More.Browse",
        };
        foreach (var file in new[] { "Strings.resx", "Strings.ru.resx" })
        {
            var doc = System.Xml.Linq.XDocument.Load(PluginSandbox.RepoFile("src/Noctis.UI/Localization/" + file));
            var values = doc.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
            foreach (var key in keys)
            {
                Assert.True(values.TryGetValue(key, out var value) && value.Length > 0, $"{file}: {key} missing");
                Assert.DoesNotContain('—', value!);
            }
        }

        // Read the Guide opens website/src/pages/plugins/make.astro (Astro serves it at /plugins/make).
        Assert.Equal("https://noctisapp.cc/plugins/make", SettingsViewModel.PluginGuideUrl);
        Assert.True(File.Exists(PluginSandbox.RepoFile("website/src/pages/plugins/make.astro")));
    }
}
