using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings page switches (owner 11-14: "a clean and smooth animation when I click through
/// the options"): what one switch costs, and that the page glide always lands clean.
/// </summary>
public class SettingsTabSwitchAnimationTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public SettingsTabSwitchAnimationTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Runs the switch's frame (a first visit's layout pass can take a few hundred ms here, and
    /// the motion only starts after it), then lets the motion finish.
    /// </summary>
    private static void Land()
    {
        Dispatcher.UIThread.RunJobs();
        Pump(400);
    }

    private async Task<(SettingsView View, SettingsViewModel Vm, Window Window)> OpenAsync()
    {
        EnsureAppStyles();
        var library = new FakeLibraryService();
        for (var i = 0; i < 3; i++)
            library.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"/music/{i}.flac" });
        var vm = new SettingsViewModel(new PersistenceService(_root), library, new NoPlays());
        await vm.LoadAsync();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Pump(50);
        return (view, vm, window);
    }

    /// <summary>
    /// Wall time and allocation of one switch: the selection change plus the layout pass that
    /// brings the page on screen (the frame the user waits for). First visits are the best of
    /// three fresh views after a JIT warm-up view (the machine's load moves single runs a lot);
    /// allocation is the UI thread's own. Prints; asserts nothing about the numbers.
    /// </summary>
    [AvaloniaFact]
    public async Task TabSwitch_Cost()
    {
        var tabs = SettingsView.TabPanels.Select(t => t.Tab).Where(t => t != SettingsViewModel.TabGeneral).ToList();
        var (_, coldVm, coldWindow) = await OpenAsync();
        foreach (var tab in tabs) { coldVm.SelectedSettingsTab = tab; Pump(10); }
        coldWindow.Close();

        var best = new Dictionary<string, (double Ms, long Bytes)>();
        SettingsView? view = null;
        SettingsViewModel? vm = null;
        Window? window = null;
        for (var round = 0; round < 3; round++)
        {
            window?.Close();
            (view, vm, window) = await OpenAsync();
            foreach (var tab in tabs)
            {
                var (ms, bytes) = TimeSwitch(vm, window, tab);
                if (!best.TryGetValue(tab, out var b) || ms < b.Ms) best[tab] = (ms, bytes);
                Pump(30);
            }
            vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
            Pump(30);
        }
        var total = 0.0;
        foreach (var tab in tabs)
        {
            var visuals = view!.FindControl<Control>(SettingsView.TabPanels.Single(t => t.Tab == tab).PanelName)!.GetVisualDescendants().Count();
            _o.WriteLine($"first visit  {tab,-18} {best[tab].Ms,7:F1} ms {best[tab].Bytes / 1048576.0,6:F2} MB {visuals,5} visuals");
            total += best[tab].Ms;
        }
        _o.WriteLine($"first visit: total {total:F0} ms");

        var worst = 0.0;
        foreach (var tab in tabs.Append(SettingsViewModel.TabGeneral))
        {
            var (ms, _) = TimeSwitch(vm!, window!, tab);
            worst = Math.Max(worst, ms);
            Pump(30);
        }
        _o.WriteLine($"revisit: worst {worst:F1} ms");
        window!.Close();
    }

    private static (double Ms, long Bytes) TimeSwitch(SettingsViewModel vm, Window window, string tab)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        vm.SelectedSettingsTab = tab;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }

    private static List<Control> RealizedPages(SettingsView view) =>
        ((Panel)view.GeneralTabPanel.Parent!).Children.Where(c => c.Classes.Contains("tab-page")).ToList();

    private static double OffsetY(Visual v) => v.RenderTransform?.Value.M32 ?? 0;

    /// <summary>Exactly <paramref name="expected"/> showing, and every page at rest.</summary>
    private static void AssertClean(SettingsView view, Control expected)
    {
        Assert.Equal(new[] { expected }, RealizedPages(view).Where(p => p.IsVisible));
        foreach (var page in RealizedPages(view))
        {
            Assert.Equal(1, page.Opacity, 3);
            Assert.Equal(0, OffsetY(page), 3);
        }
    }

    [AvaloniaFact]
    public async Task Switch_GlidesInFromBelow_AfterItsFirstLayout_AndEndsAtRest()
    {
        var (view, vm, window) = await OpenAsync();

        vm.SelectedSettingsTab = SettingsViewModel.TabAudio; // further down the rail
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render); // the frame that lays the page out
        Assert.True(view.AudioTabPanel.IsVisible);
        Assert.False(view.GeneralTabPanel.IsVisible); // the old page goes at once
        Assert.Equal(0, view.AudioTabPanel.Opacity);
        Assert.Equal(SettingsView.PageEnterOffset, OffsetY(view.AudioTabPanel), 3);

        Dispatcher.UIThread.RunJobs(); // after layout: the glide is armed, no frame has moved it yet
        Assert.Equal(2, view.AudioTabPanel.Transitions?.Count);
        Assert.True(view.AudioTabPanel.Opacity < 0.05);

        Land();
        AssertClean(view, view.AudioTabPanel);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switch_ToAnEarlierPage_GlidesInFromAbove()
    {
        var (view, vm, window) = await OpenAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabAbout;
        Land();

        vm.SelectedSettingsTab = SettingsViewModel.TabLyrics;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        Assert.Equal(-SettingsView.PageEnterOffset, OffsetY(view.LyricsTabPanel), 3);
        Land();
        AssertClean(view, view.LyricsTabPanel);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RapidSwitches_EndOnTheLastPage_WithNothingLeftHalfway()
    {
        var (view, vm, window) = await OpenAsync();

        // A -> B -> C with C clicked while B is still gliding in.
        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        Pump(40);
        vm.SelectedSettingsTab = SettingsViewModel.TabLibrary;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        Assert.Equal(1, view.PlayerTabPanel.Opacity, 3); // B settled the moment it left
        Assert.Null(view.PlayerTabPanel.Transitions);
        Land();
        AssertClean(view, view.LibraryTabPanel);

        // Three clicks inside one frame: only the last page ever glides.
        vm.SelectedSettingsTab = SettingsViewModel.TabAudio;
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
        vm.SelectedSettingsTab = SettingsViewModel.TabAdvanced;
        Land();
        AssertClean(view, view.AdvancedTabPanel);
        Assert.Null(view.AudioTabPanel.Transitions);
        Assert.Null(view.AppearanceTabPanel.Transitions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReduceMotion_SwitchesInstantly()
    {
        var (view, vm, window) = await OpenAsync();
        Noctis.Controls.TransportGlyph.SetReduceMotion(window, true);
        Assert.Contains("motion-off", view.Classes); // the rail rows' colour fade is off too

        vm.SelectedSettingsTab = SettingsViewModel.TabAudio;
        Dispatcher.UIThread.RunJobs();
        AssertClean(view, view.AudioTabPanel);
        Assert.Null(view.AudioTabPanel.Transitions);
        AssertPillOn(view, SettingsViewModel.TabAudio); // no slide either
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switch_WhileSettingsIsHidden_IsInstant()
    {
        // A deep link picks the tab before the sheet opens.
        var (view, vm, window) = await OpenAsync();
        view.IsVisible = false;
        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        Dispatcher.UIThread.RunJobs();
        view.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        AssertClean(view, view.PlayerTabPanel);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_JumpingToAnotherPage_EndsAtRest()
    {
        var (view, vm, window) = await OpenAsync();
        view.SettingsSearchBox.Text = "Developer";
        Land();
        Assert.Equal(SettingsViewModel.TabAdvanced, vm.SelectedSettingsTab);
        AssertClean(view, view.AdvancedTabPanel);
        window.Close();
    }

    private static Button RailRow(SettingsView view, string tab) =>
        view.RailHost.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("rail-item") && b.DataContext is SettingsSection s && s.Key == tab);

    private static void AssertPillOn(SettingsView view, string tab)
    {
        var row = RailRow(view, tab);
        var at = row.TranslatePoint(default, view.RailHost)!.Value;
        var pill = view.RailSelectionPill;
        Assert.Equal(1, pill.Opacity);
        Assert.Equal(at.X, pill.RenderTransform!.Value.M31, 1);
        Assert.Equal(at.Y, pill.RenderTransform.Value.M32, 1);
        Assert.Equal(row.Bounds.Width, pill.Width, 1);
        Assert.Equal(row.Bounds.Height, pill.Height, 1);
    }

    [AvaloniaFact]
    public async Task RailPill_SlidesFromTheOldRowToTheNewOne()
    {
        var (view, vm, window) = await OpenAsync();
        AssertPillOn(view, SettingsViewModel.TabGeneral);
        var fromY = view.RailSelectionPill.RenderTransform!.Value.M32;

        vm.SelectedSettingsTab = SettingsViewModel.TabAbout;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render); // the frame that lays the new page out
        Assert.Equal(fromY, view.RailSelectionPill.RenderTransform!.Value.M32, 1); // waits for it
        Dispatcher.UIThread.RunJobs(); // then the slide starts from the old row
        var toY = RailRow(view, SettingsViewModel.TabAbout).TranslatePoint(default, view.RailHost)!.Value.Y;
        Assert.True(view.RailSelectionPill.RenderTransform!.Value.M32 < toY - 100);
        Land();
        AssertPillOn(view, SettingsViewModel.TabAbout);

        // The selected row draws no fill of its own: the pill is the highlight.
        var presenter = RailRow(view, SettingsViewModel.TabAbout).GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)presenter.Background!).Color);

        // Retargeted mid-slide, it still lands on the last row.
        vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        Pump(40);
        vm.SelectedSettingsTab = SettingsViewModel.TabLibrary;
        Land();
        AssertPillOn(view, SettingsViewModel.TabLibrary);
        window.Close();
    }

    private sealed class NoPlays : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
