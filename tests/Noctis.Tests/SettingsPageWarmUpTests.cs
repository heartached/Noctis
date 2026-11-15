using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings page warm-up (owner 11-15): while Settings is open and idle, the pages not visited
/// yet are built in small slices so their first click costs about what a revisit does, without
/// the all-at-once build whose collections skipped the audio (#139).
/// </summary>
public class SettingsPageWarmUpTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public SettingsPageWarmUpTests(ITestOutputHelper o) => _o = o;

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
    /// Runs the real dispatcher loop (timers fire in it; RunJobs alone never fires a
    /// DispatcherTimer here), so the warm-up's own scheduling is what runs.
    /// </summary>
    private static void Loop(int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static bool LoopUntil(Func<bool> condition, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < budgetMs) Loop(20);
        return condition();
    }

    private async Task<(SettingsView View, SettingsViewModel Vm, Window Window)> OpenAsync()
    {
        EnsureAppStyles();
        SettingsView.RecordWarmSlices = true; // test-only diagnostics, harmless left on
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

    private static string[] Tabs => SettingsView.TabPanels.Select(t => t.Tab).ToArray();

    private static Control Page(SettingsView view, string tab) =>
        view.FindControl<Control>(SettingsView.TabPanels.Single(t => t.Tab == tab).PanelName)!;

    /// <summary>Every warm-up slice back to back (the idle gating has its own test).</summary>
    private static void WarmEverything(SettingsView view)
    {
        while (view.RunWarmSliceForTests()) Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
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

    /// <summary>Type and name of every logical control under a page, in tree order.</summary>
    private static List<string> Shape(Control page) =>
        page.GetLogicalDescendants().OfType<Control>().Select(c => $"{c.GetType().Name}#{c.Name}").ToList();

    private static void AssertAtRest(SettingsView view, Control expected)
    {
        var pages = ((Panel)view.GeneralTabPanel.Parent!).Children.Where(c => c.Classes.Contains("tab-page")).ToList();
        Assert.Equal(new[] { expected }, pages.Where(p => p.IsVisible));
        foreach (var page in pages)
        {
            Assert.Equal(1, page.Opacity, 3);
            Assert.Equal(0, page.RenderTransform?.Value.M32 ?? 0, 3);
        }
    }

    [AvaloniaFact]
    public async Task WarmedPages_FirstClickCostsAboutARevisit_AndSlicesStaySmall()
    {
        var keep = SettingsView.WarmUpEnabled;
        SettingsView.WarmUpEnabled = false; // driven by hand below, slice by slice
        try
        {
            // JIT every control type first, as the cost test does.
            var (_, coldVm, coldWindow) = await OpenAsync();
            foreach (var tab in Tabs) { coldVm.SelectedSettingsTab = tab; Pump(10); }
            coldWindow.Close();

            var (view, vm, window) = await OpenAsync();
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            WarmEverything(view);
            var slices = view.WarmSlicesForTests;
            Assert.Empty(view.DeferredTabsForTests);
            Assert.Empty(view.PartialTabsForTests);
            Assert.True(view.GeneralTabPanel.IsVisible);
            Assert.All(Tabs.Skip(1), t => Assert.False(Page(view, t).IsVisible));

            var worst = slices.OrderByDescending(s => s.Ms).First();
            _o.WriteLine($"warm-up: {slices.Count} slices, {slices.Sum(s => s.Bytes) / 1048576.0:F1} MB, gcs {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}");
            _o.WriteLine($"longest slice {worst.Ms:F1} ms ({worst.Tab}, {worst.Step}); 99th pct {slices.Select(s => s.Ms).OrderBy(x => x).ElementAt((int)(slices.Count * 0.99)):F1} ms; " +
                         $"most bytes {slices.Max(s => s.Bytes) / 1024.0:F0} KB; median {slices.Select(s => s.Ms).OrderBy(x => x).ElementAt(slices.Count / 2):F1} ms");
            _o.WriteLine($"gc pause in slices: total {slices.Sum(s => s.GcPauseMs):F1} ms, longest {slices.Max(s => s.GcPauseMs):F1} ms; longest slice without its gc pause {slices.Max(s => s.Ms - s.GcPauseMs):F1} ms");
            var work = slices.Select(s => s.Ms - s.GcPauseMs).OrderBy(x => x).ToList();
            _o.WriteLine($"slice work (gc pause taken out): median {work[work.Count / 2]:F1} ms, 90th {work[(int)(work.Count * 0.9)]:F1}, 95th {work[(int)(work.Count * 0.95)]:F1}, 99th {work[(int)(work.Count * 0.99)]:F1}, over 8 ms: {work.Count(x => x > 8)} of {work.Count}");
            foreach (var s in slices.Where(s => s.Ms > 8).OrderByDescending(s => s.Ms).Take(25))
                _o.WriteLine($"   long: {s.Ms,5:F1} ms (gc {s.GcPauseMs,4:F1}) {s.Bytes / 1024.0,6:F0} KB {s.Tab} {s.Step}");
            foreach (var g in slices.GroupBy(s => s.Tab))
                _o.WriteLine($"   {g.Key,-18} {g.Count(),4} slices, longest {g.Max(s => s.Ms),5:F1} ms ({g.OrderByDescending(s => s.Ms).First().Step}), {g.Sum(s => s.Bytes) / 1048576.0,5:F1} MB");

            var total = 0.0;
            foreach (var tab in Tabs.Skip(1))
            {
                var (ms, bytes) = TimeSwitch(vm, window, tab);
                total += ms;
                _o.WriteLine($"first click (warmed) {tab,-18} {ms,6:F1} ms {bytes / 1048576.0,5:F2} MB");
                Dispatcher.UIThread.RunJobs();
                Pump(300);
                AssertAtRest(view, Page(view, tab)); // the glide still lands clean
                Assert.Equal(0, view.SettingsScrollViewer.Offset.Y);
            }
            _o.WriteLine($"first clicks (warmed): total {total:F0} ms");
            // Cold first visits total ~1,000 ms here (TabSwitch_Cost); revisits ~2-12 ms each.
            Assert.True(total < 400, $"warmed first clicks took {total:F0} ms in all");
            // A slice that a collection lands in waits for it (the same collections a click on
            // the page would set off); the slice's own work stays short.
            var worstWork = slices.OrderByDescending(s => s.Ms - s.GcPauseMs).First();
            Assert.True(worstWork.Ms - worstWork.GcPauseMs < 60, $"longest slice {worstWork.Ms:F1} ms ({worstWork.Tab}, {worstWork.Step})");
            window.Close();
        }
        finally
        {
            SettingsView.WarmUpEnabled = keep;
        }
    }

    [AvaloniaFact]
    public async Task WarmedPage_HasTheSameTree_AndLiveBindings_AsAFirstVisit()
    {
        var keep = SettingsView.WarmUpEnabled;
        SettingsView.WarmUpEnabled = false;
        try
        {
            var (warmView, warmVm, warmWindow) = await OpenAsync();
            WarmEverything(warmView);
            var (coldView, coldVm, coldWindow) = await OpenAsync();
            foreach (var tab in Tabs) { coldVm.SelectedSettingsTab = tab; Pump(10); }

            foreach (var tab in Tabs)
            {
                warmVm.SelectedSettingsTab = tab;
                Pump(10);
                Assert.Equal(Shape(Page(coldView, tab)), Shape(Page(warmView, tab)));
            }
            warmVm.SelectedSettingsTab = SettingsViewModel.TabAudio;
            Pump(10);

            // Bindings on a page built hidden follow the view model.
            Assert.Same(warmVm.VisibleEqPresets, warmView.EqPresetCombo.ItemsSource);
            var eqBody = (Control)warmView.EqPresetCombo.Parent!.Parent!;
            var on = warmVm.EqualizerEnabled;
            warmVm.EqualizerEnabled = !on;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(!on, eqBody.IsEnabled);
            warmVm.EqualizerEnabled = on;

            warmWindow.Close();
            coldWindow.Close();
        }
        finally
        {
            SettingsView.WarmUpEnabled = keep;
        }
    }

    [AvaloniaFact]
    public async Task ClickOnAPagePartWayThrough_ShowsTheWholePage()
    {
        var keep = SettingsView.WarmUpEnabled;
        SettingsView.WarmUpEnabled = false;
        try
        {
            var (view, vm, window) = await OpenAsync();
            // General is shown, so Appearance (next down) is warmed first; stop part-way.
            while (!view.PartialTabsForTests.Contains(SettingsViewModel.TabAppearance)) view.RunWarmSliceForTests();
            view.RunWarmSliceForTests();
            Assert.Contains(SettingsViewModel.TabAppearance, view.PartialTabsForTests);

            vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
            Dispatcher.UIThread.RunJobs();
            Pump(400);
            Assert.Empty(view.PartialTabsForTests);
            AssertAtRest(view, view.AppearanceTabPanel);

            var (coldView, coldVm, coldWindow) = await OpenAsync();
            coldVm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
            Pump(50);
            Assert.Equal(Shape(coldView.AppearanceTabPanel), Shape(view.AppearanceTabPanel));
            coldWindow.Close();
            window.Close();
        }
        finally
        {
            SettingsView.WarmUpEnabled = keep;
        }
    }

    [AvaloniaFact]
    public async Task WarmUp_WaitsForIdle_AndYieldsToInput()
    {
        var (view, _, window) = await OpenAsync();
        void Key() => view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.LeftShift });
        view.OnSheetOpened();

        Loop(300); // inside the open's quiet time
        Assert.Empty(view.WarmSlicesForTests);

        // Someone is typing: nothing runs however long it goes on.
        for (var i = 0; i < 12; i++) { Key(); Loop(100); }
        Assert.Empty(view.WarmSlicesForTests);

        // Quiet: it starts, and runs at most about one slice a frame.
        Assert.True(LoopUntil(() => view.WarmSlicesForTests.Count > 0, 3000));
        var started = view.WarmSlicesForTests.Count;
        Loop(500);
        var perSecond = (view.WarmSlicesForTests.Count - started) * 2;
        _o.WriteLine($"{perSecond} slices/s while idle");
        Assert.InRange(perSecond, 1, 70);

        // Input again: no slice inside the quiet time that follows it.
        Key();
        var count = view.WarmSlicesForTests.Count;
        Loop((int)(SettingsView.WarmUpIdle.TotalMilliseconds * 0.6));
        Assert.Equal(count, view.WarmSlicesForTests.Count);

        // Left alone, it finishes every page.
        Assert.True(LoopUntil(() => !view.HasWarmUpWorkForTests, 30000));
        Assert.Empty(view.DeferredTabsForTests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WarmUp_BuildsNothingWhileSettingsIsClosed()
    {
        var (view, _, window) = await OpenAsync();
        view.OnSheetOpened();
        view.IsVisible = false; // the sheet closed (MainWindow hides its overlay)
        var deferred = view.DeferredTabsForTests.Count;
        Loop(1500);
        Assert.Empty(view.WarmSlicesForTests);
        Assert.Equal(deferred, view.DeferredTabsForTests.Count);

        // Open again: it picks up.
        view.IsVisible = true;
        view.OnSheetOpened();
        Assert.True(LoopUntil(() => view.WarmSlicesForTests.Count > 0, 3000));

        // Closed mid-way: it stops.
        view.IsVisible = false;
        Loop(50);
        var count = view.WarmSlicesForTests.Count;
        Loop(800);
        Assert.Equal(count, view.WarmSlicesForTests.Count);
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
