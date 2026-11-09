using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Open &lt;Name&gt;" on a plugin's page (owner 10-29: the pop-up window looked choppy) swaps the page
/// for the plugin's own view inside the Settings card: no window, a Back pill to the page, a pop-out
/// button for the old window. The view leaves (its control released) on Back, pop-out, the plugin
/// switching off or withdrawing it, another tab or plugin, and Settings closing; the card grows to
/// the size the view asks for, within the main window.
/// </summary>
public class PluginViewInPlaceTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        _box.Dispose();
    }

    private const string Id = "dev.test.inplace";

    private sealed class View : IPluginViewProvider
    {
        public string Name { get; set; } = "Town";
        public Size? Preferred;
        public Size? Min;
        public int Created;
        public readonly List<Control> Controls = new();
        public Control CreateView()
        {
            Created++;
            var c = new Border { Child = new Button { Content = "Inside" } };
            Controls.Add(c);
            return c;
        }
        public Size? PreferredWindowSize => Preferred;
        public Size? MinWindowSize => Min;
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed record Mounted(SettingsViewModel Vm, PluginHost Host, LoadedPlugin Plugin, ScriptedPlugin Script,
        PluginViewEntry Entry, TestPersistenceService Persistence) : IDisposable
    {
        public void Dispose() => Persistence.Dispose();
    }

    /// <summary>Settings on a host running one plugin with one view; windows the view opens are counted.</summary>
    private Mounted Mount(View view, List<Control>? windowed = null)
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.6.6");
        var script = new ScriptedPlugin { OnInit = h => h.RegisterView(view) };
        var plugin = _box.AddInProcess(host, script, PluginSandbox.Manifest(id: Id, name: "Town Plugin", apiVersion: "1.5"));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        host.ViewPresenter = (_, c) => { windowed?.Add(c); return true; };
        var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory())
        {
            PluginCatalogClient = new PluginCatalogClient(new HttpClient(new PluginCatalogTests.FakeHandler()), () => null)
            {
                Embedded = () => PluginCatalog.Empty,
            },
        };
        vm.Plugins = host;
        return new Mounted(vm, host, plugin, script, plugin.Views.Single(), persistence);
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

    /// <summary>Settings on the Plugins tab with the plugin's page open, in a window.</summary>
    private (SettingsView View, Window Window) ShowPage(Mounted m)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        var view = new SettingsView { DataContext = m.Vm };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        _windows.Add(window);
        window.Show();
        Pump();
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == Id));
        Pump(450);
        return (view, window);
    }

    private static Button OpenButton(SettingsView view, PluginViewEntry entry)
        => view.FindControl<ContentControl>("PluginDetailHost")!.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.DataContext, entry));

    [AvaloniaFact]
    public void Open_SwapsThePageForTheView_InPlace_WithoutAWindow()
    {
        var view = new View();
        var windowed = new List<Control>();
        using var m = Mount(view, windowed);
        var (settings, window) = ShowPage(m);

        OpenButton(settings, m.Entry).Command!.Execute(m.Entry);
        Pump(450);

        Assert.Empty(windowed);
        Assert.Equal(1, view.Created);
        Assert.True(m.Vm.IsPluginViewOpen);
        Assert.False(m.Vm.IsPluginPageVisible);
        var control = Assert.Single(view.Controls);
        Assert.Same(control, m.Vm.PluginViewContent);
        Assert.Same(window, TopLevel.GetTopLevel(control)); // in the Settings card, not a window of its own
        Assert.True(control.IsEffectivelyVisible);
        Assert.Null(control.DataContext); // as in its window: nothing of Settings' view model leaks in
        Assert.False(settings.FindControl<ScrollViewer>("SettingsScrollViewer")!.IsVisible);
        Assert.False(settings.FindControl<StackPanel>("PluginDetailPage")!.IsVisible);

        // The header: "< Town Plugin" (read as "Back to Town Plugin") focused, and the pop-out button.
        var back = settings.FindControl<Button>("PluginViewBackButton")!;
        Assert.True(back.IsFocused);
        Assert.Equal(Loc.T("Plugins.View.Back", "Town Plugin"), AutomationProperties.GetName(back));
        Assert.Contains(back.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Town Plugin");
        Assert.Equal(Loc.T("Plugins.View.PopOut"), AutomationProperties.GetName(settings.FindControl<Button>("PluginViewPopOutButton")!));

        // The plugin-window theme, hosted here.
        Assert.True(PluginViewTheme.IsApplied(settings.FindControl<ContentControl>("PluginViewHost")!));

        // One live control: the plugin's own OpenView finds the view already up.
        Assert.True(m.Script.Host!.OpenView(view));
        Assert.Equal(1, view.Created);
        Assert.Empty(windowed);
    }

    [AvaloniaFact]
    public void Back_ReturnsToThePage_AndReleasesTheControl()
    {
        var view = new View();
        using var m = Mount(view);
        var (settings, _) = ShowPage(m);
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Pump(450);
        var control = view.Controls.Single();

        settings.FindControl<Button>("PluginViewBackButton")!.Command!.Execute(null);
        Pump(450);

        Assert.False(m.Vm.IsPluginViewOpen);
        Assert.True(m.Vm.IsPluginPageVisible);
        Assert.Null(m.Vm.PluginViewContent);
        Assert.Null(control.Parent);                 // detached, as a closed window's
        Assert.Null(TopLevel.GetTopLevel(control));
        Assert.Null(m.Entry.Reactivate);
        Assert.True(settings.FindControl<ScrollViewer>("SettingsScrollViewer")!.IsVisible);
        var page = settings.FindControl<StackPanel>("PluginDetailPage")!;
        Assert.True(page.IsEffectivelyVisible);
        Assert.Equal(1, page.Opacity, 3);            // slid back in (from the left)
        Assert.True(OpenButton(settings, m.Entry).IsFocused);

        // Opening again builds a fresh control (the old one is gone).
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Assert.Equal(2, view.Created);
    }

    [AvaloniaFact]
    public void PopOut_OpensTheWindow_AndClosesTheInPlaceView_ThenOpenBringsThatWindowForward()
    {
        var view = new View();
        using var m = Mount(view);
        m.Host.ViewPresenter = PluginViewWindow.Present;
        var (settings, _) = ShowPage(m);
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Pump(450);
        var inPlace = view.Controls.Single();

        settings.FindControl<Button>("PluginViewPopOutButton")!.Command!.Execute(null);
        Pump();

        Assert.False(m.Vm.IsPluginViewOpen);
        Assert.Null(inPlace.Parent);
        Assert.Equal(2, view.Created);
        var popped = Assert.IsType<PluginViewWindow>(TopLevel.GetTopLevel(view.Controls[1]));
        _windows.Add(popped);
        Assert.NotNull(m.Entry.Reactivate);

        // While the window is open, Open brings it forward: no second control, nothing in place.
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Assert.Equal(2, view.Created);
        Assert.False(m.Vm.IsPluginViewOpen);

        popped.Close();
        Assert.Null(m.Entry.Reactivate);
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Assert.True(m.Vm.IsPluginViewOpen); // in place again
        Assert.Equal(3, view.Created);
    }

    [AvaloniaFact]
    public void TheView_Leaves_WhenThePluginIsSwitchedOff()
    {
        var view = new View();
        using var m = Mount(view);
        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        var control = view.Controls.Single();
        Assert.True(m.Vm.IsPluginViewOpen);

        m.Host.SetEnabled(m.Plugin, false);

        Assert.False(m.Vm.IsPluginViewOpen);
        Assert.Null(m.Vm.PluginViewContent);
        Assert.Null(control.Parent);
    }

    [AvaloniaFact]
    public void TheView_Leaves_WhenSettingsCloses_OrMovesToAnotherTabOrPlugin()
    {
        var view = new View();
        using var m = Mount(view);
        void Open()
        {
            m.Vm.OpenPluginViewCommand.Execute(m.Entry);
            Assert.True(m.Vm.IsPluginViewOpen);
        }

        Open();
        m.Vm.LeavePluginView(); // what MainWindowViewModel does when the sheet closes
        Assert.False(m.Vm.IsPluginViewOpen);
        Assert.Null(m.Entry.Reactivate);

        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        Open();
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
        Assert.False(m.Vm.IsPluginViewOpen);

        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == Id));
        Open();
        m.Vm.ClosePluginDetailCommand.Execute(null); // another plugin goes through the grid
        Assert.False(m.Vm.IsPluginViewOpen);
        Assert.Equal(3, view.Created);
        Assert.All(view.Controls, c => Assert.Null(c.Parent));
    }

    // ── The card's size ──

    [AvaloniaTheory]
    [InlineData(1800.0, 1200.0)]
    [InlineData(1280.0, 800.0)]
    public void TheCard_GrowsToTheSizeTheViewAsksFor_WithinTheWindow_AndShrinksBack(double width, double height)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var view = new View { Preferred = new Size(1200, 800), Min = new Size(1100, 760) };
        using var m = Mount(view);
        var w = new MainWindow { Width = width, Height = height };
        _windows.Add(w);
        w.Show();
        w.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
        w.EnsureSettingsViewLoaded();
        var host = w.FindControl<ContentControl>("SettingsViewHost")!;
        host.DataContext = m.Vm;
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == Id));
        Pump(200);
        var card = w.FindControl<Border>("SettingsCard")!;
        var glass = w.FindControl<Control>("SettingsGlass")!;
        var normal = card.Bounds.Size;

        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Pump(600);

        var frame = ((SettingsView)host.Content!).FindControl<Border>("PluginViewFrame")!;
        var room = new Size(w.ClientSize.Width - 48, w.ClientSize.Height - 48); // the overlay's 24 px margin
        if (room.Width >= card.MaxWidth && room.Height >= card.MaxHeight)
        {
            // Room for it: the view gets exactly what it asked for.
            Assert.Equal(1100, frame.Bounds.Width, 0);
            Assert.Equal(760, frame.Bounds.Height, 0);
        }
        Assert.True(card.Bounds.Width > normal.Width || card.Bounds.Width >= room.Width - 0.5, $"{card.Bounds} from {normal}");
        Assert.True(card.Bounds.Width <= room.Width + 0.5 && card.Bounds.Height <= room.Height + 0.5, $"{card.Bounds} in {room}");
        Assert.Equal(card.Bounds.Size, glass.Bounds.Size); // the frost follows the card

        m.Vm.ClosePluginViewCommand.Execute(null);
        Pump(600);
        Assert.Equal((1000, 720), (card.MaxWidth, card.MaxHeight));
        Assert.Equal(normal, card.Bounds.Size);
    }

    [AvaloniaFact]
    public void TheCard_KeepsItsOwnSize_ForAViewThatFitsIt()
    {
        // Discord "Fix. That. Modal." (11-06): a view with no size hint (every official plugin)
        // grew Settings to 1000 x 680 plus the rail, wider than every other Settings page.
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var m = Mount(new View());
        var w = new MainWindow { Width = 1800, Height = 1200 };
        _windows.Add(w);
        w.Show();
        w.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
        w.EnsureSettingsViewLoaded();
        var host = w.FindControl<ContentControl>("SettingsViewHost")!;
        host.DataContext = m.Vm;
        m.Vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;
        m.Vm.OpenPluginCardCommand.Execute(m.Vm.CommunityCards.Single(c => c.Id == Id));
        Pump(200);
        var card = w.FindControl<Border>("SettingsCard")!;
        var normal = card.Bounds.Size;

        m.Vm.OpenPluginViewCommand.Execute(m.Entry);
        Pump(600);

        Assert.Equal((1000, 720), (card.MaxWidth, card.MaxHeight));
        Assert.Equal(normal, card.Bounds.Size);
        var frame = ((SettingsView)host.Content!).FindControl<Border>("PluginViewFrame")!;
        Assert.True(frame.Bounds.Width >= 480 && frame.Bounds.Height >= 360, $"{frame.Bounds}");
    }

    [AvaloniaFact]
    public void ViewSize_FollowsTheHint_TheDefaults_AndSizeToContent()
    {
        // In place the view needs only its minimum (the preferred size is the pop-out window's):
        // the default 480 x 360, the hint's minimum, or the preferred size when that is smaller.
        Assert.Equal(new Size(480, 360), SettingsView.PluginViewSize(PluginViewSizeHint.None, null));
        Assert.Equal(new Size(380, 320), SettingsView.PluginViewSize(new PluginViewSizeHint(new Size(460, 500), new Size(380, 320), SizeToContent.Manual), null));
        Assert.Equal(new Size(300, 200), SettingsView.PluginViewSize(new PluginViewSizeHint(new Size(300, 200), null, SizeToContent.Manual), null));
        Assert.Equal(new Size(500, 400), SettingsView.PluginViewSize(new PluginViewSizeHint(new Size(300, 200), new Size(500, 400), SizeToContent.Manual), null));
        // Size to content on the height: the control's own height, never under the minimum.
        var content = new Border { Height = 240 };
        Assert.Equal(new Size(300, 240), SettingsView.PluginViewSize(new PluginViewSizeHint(new Size(600, 900), new Size(300, 200), SizeToContent.Height), content));
        Assert.Equal(new Size(300, 300), SettingsView.PluginViewSize(new PluginViewSizeHint(new Size(600, 900), new Size(300, 300), SizeToContent.Height), content));
    }
}
