using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: same UI + animation for search metadata. The editor's Find online panel opens
/// and closes on the pill dialog's own curves and timings — fade, short rise, slight grow
/// (0.97 → 1), reversed on every way out — and still backs out on Esc / Cancel / the header
/// pill. Opening the window straight into the panel lets the card's entrance carry it.
/// </summary>
public class MetadataSearchPanelAnimationTests
{
    private readonly ITestOutputHelper _o;
    public MetadataSearchPanelAnimationTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static MetadataViewModel Vm()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "monaco", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "nadie sabe",
            AlbumId = Guid.NewGuid(), TrackNumber = 2, Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(267),
            FilePath = "C:/m/does-not-exist/monaco.flac",
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var search = new MetadataSearchPanelTests.FakeSearch { Result = MetadataSearchPanelShotsTests.TrackResults() };
        return new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: search);
    }

    private static (MetadataWindow win, PillDialogHost host, MetadataSearchPanel view) Show(MetadataViewModel vm)
    {
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var load = vm.InitializeAsync();
        Assert.True(PumpUntil(() => load.IsCompleted));
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        var view = win.GetVisualDescendants().OfType<MetadataSearchPanel>().Single();
        return (win, host, view);
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static Matrix Pose(Visual v) => ((TransformOperations)v.RenderTransform!).Value;

    private static void AssertTiming(Visual panel, TimeSpan fade, TimeSpan move, object easing)
    {
        var fadeT = panel.Transitions!.OfType<DoubleTransition>().Single(t => t.Property == Visual.OpacityProperty);
        var moveT = panel.Transitions!.OfType<TransformOperationsTransition>().Single();
        Assert.Equal(fade, fadeT.Duration);
        Assert.Equal(move, moveT.Duration);
        Assert.Same(easing, fadeT.Easing);
        Assert.Same(easing, moveT.Easing);
    }

    private (MetadataViewModel vm, MetadataWindow win, PillDialogHost host, MetadataSearchPanel view) OpenPanel()
    {
        EnsureAppStyles();
        var vm = Vm();
        var (win, host, view) = Show(vm);
        Assert.True(PumpUntil(() => CardSettledOpen(host)), "dialog open animation never settled");
        vm.OpenSearchPanelCommand.Execute(null);
        Assert.True(PumpUntil(() => view.Opacity > 0.999 && Pose(view).M11 > 0.9999), "panel never settled open");
        return (vm, win, host, view);
    }

    [AvaloniaFact]
    public void Ticks_SitInsideTheirColumn_SoTheCircleIsNotClipped()
    {
        // Owner 10-08: the checked master tick's circle was cut off on the left. The round
        // checkbox wants 38 px (28 circle + 10 label gap); centred in the 30 px tick column it
        // landed at x = -4 and its slot clipped the circle.
        var (_, win, _, view) = OpenPanel();
        try
        {
            var ticks = view.GetVisualDescendants().OfType<CheckBox>()
                .Where(c => c.Classes.Contains("ms-tick") && c.IsEffectivelyVisible).ToList();
            Assert.Contains(ticks, t => t.Name == "MasterTick");
            foreach (var tick in ticks)
            {
                Assert.True(tick.Bounds.X >= 0, $"{tick.Name ?? "row tick"} starts at x={tick.Bounds.X}");
                var circle = tick.GetVisualDescendants().OfType<Border>().First(b => b.Name == "IndicatorBorder");
                var left = circle.TranslatePoint(new Point(0, 0), (Visual)tick.GetVisualParent()!)!.Value.X;
                Assert.True(left >= 0, $"circle starts at x={left} in its column");
            }
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Open_FadesRisesAndGrows_OnTheDialogsCurve()
    {
        EnsureAppStyles();
        var vm = Vm();
        var (win, host, view) = Show(vm);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            vm.OpenSearchPanelCommand.Execute(null);

            // Pinned at the hidden pose first: transparent, 10 px low, at 97 %.
            Assert.True(view.IsVisible);
            Assert.Equal(0, view.Opacity);
            Assert.Equal(0.97, Pose(view).M11, 3);
            Assert.InRange(Pose(view).M32, 9.5, 10.01); // translate then scale: 10 × 0.97

            Dispatcher.UIThread.RunJobs();
            AssertTiming(view, PillDialogHost.OpenFadeDuration, PillDialogHost.OpenMoveDuration, PillDialogHost.OpenEase);

            // Animated, not snapped: some frame on the way has it part-faded and part-grown.
            var midway = false;
            var sw = Stopwatch.StartNew();
            Assert.True(PumpUntil(() =>
            {
                var m = Pose(view);
                if (view.Opacity is > 0.02 and < 0.98 && m.M11 is > 0.9701 and < 0.9999) midway = true;
                return view.Opacity > 0.999 && Math.Abs(m.M11 - 1) < 1e-4 && Math.Abs(m.M32) < 1e-3;
            }), $"panel never settled: opacity {view.Opacity}, pose {Pose(view)}");
            _o.WriteLine($"settled after {sw.ElapsedMilliseconds} ms, seen midway: {midway}");
            Assert.True(midway, "the panel jumped to its shown pose");
            // The caret lands in the query, as before.
            Assert.True(PumpUntil(() => win.FocusManager?.GetFocusedElement() is TextBox { Name: "FirstQueryBox" }));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Starts the panel's exit: it reverses on the close curve while still visible,
    /// collapses once it has played, and the editor stays open.</summary>
    private void AssertAnimatedPanelClose(MetadataWindow win, PillDialogHost host, MetadataSearchPanel view, MetadataViewModel vm, Action trigger)
    {
        var sw = Stopwatch.StartNew();
        trigger();
        Assert.False(vm.SearchPanel.IsOpen);
        Assert.True(win.IsSearchPanelClosing);
        Assert.True(view.IsVisible, "the panel vanished without its exit");
        AssertTiming(view, PillDialogHost.CloseDuration, PillDialogHost.CloseDuration, PillDialogHost.CloseEase);

        Assert.True(PumpUntil(() => !view.IsVisible, 2000), "panel never collapsed");
        _o.WriteLine($"collapsed after {sw.ElapsedMilliseconds} ms at opacity {view.Opacity:0.000}");
        Assert.True(sw.ElapsedMilliseconds >= 150, $"collapsed after {sw.ElapsedMilliseconds} ms: the exit was skipped");
        Assert.True(view.Opacity < 0.1);
        Assert.Equal(0.97, Pose(view).M11, 2);
        Assert.False(win.IsSearchPanelClosing);
        Assert.False(host.IsClosing);
        Assert.True(win.IsVisible);
    }

    [AvaloniaFact]
    public void CancelButton_ReversesThePanel()
    {
        var (vm, win, host, view) = OpenPanel();
        try
        {
            var cancel = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SearchPanel.CloseCommand);
            var centre = cancel.TranslatePoint(new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), win)!.Value;
            AssertAnimatedPanelClose(win, host, view, vm, () =>
            {
                win.MouseDown(centre, MouseButton.Left);
                win.MouseUp(centre, MouseButton.Left);
            });
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void HeaderPill_ReversesThePanel()
    {
        var (vm, win, host, view) = OpenPanel();
        try { AssertAnimatedPanelClose(win, host, view, vm, () => vm.ToggleSearchPanelCommand.Execute(null)); }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Apply_ReversesThePanel()
    {
        var (vm, win, host, view) = OpenPanel();
        try
        {
            Assert.True(vm.SearchPanel.ApplyCommand.CanExecute(null), "nothing to apply");
            AssertAnimatedPanelClose(win, host, view, vm, () => vm.SearchPanel.ApplyCommand.Execute(null));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Escape_BacksOutOfThePanel_ThenASecondEscClosesTheEditor()
    {
        var (vm, win, host, view) = OpenPanel();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            AssertAnimatedPanelClose(win, host, view, vm,
                () => win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(host.IsClosing);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            Assert.Equal(1, closed);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void ReopenDuringTheExit_ReversesFromWhereItIs()
    {
        var (vm, win, _, view) = OpenPanel();
        try
        {
            vm.SearchPanel.CloseCommand.Execute(null);
            PumpUntil(() => view.Opacity < 0.8, 500);
            var from = view.Opacity;
            vm.OpenSearchPanelCommand.Execute(null);
            // No snap back to the hidden pose: it turns around from where the exit had got to.
            Assert.True(view.Opacity >= from - 0.05, $"opacity jumped from {from} to {view.Opacity}");
            Assert.True(PumpUntil(() => view.Opacity > 0.999));
            // The exit's pending collapse must not hide the reopened panel.
            PumpUntil(() => false, PillDialogHost.CloseDuration.Milliseconds + 150);
            Assert.True(view.IsVisible);
            Assert.False(win.IsSearchPanelClosing);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void OpenedStraightIntoThePanel_RidesTheCardsEntrance()
    {
        EnsureAppStyles();
        var vm = Vm();
        vm.OpenSearchPanelCommand.Execute(null);   // before the window exists
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var view = win.GetLogicalDescendants().OfType<MetadataSearchPanel>().Single();
        Assert.True(view.IsVisible, "a panel opened before the window was left hidden");
        Assert.Equal(1, view.Opacity);
        Assert.Null(view.Transitions);
        try
        {
            win.Show();
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            // The card fades and rises in; the panel stays put inside it, no second animation.
            var minPanel = 1.0;
            var sawCardMidway = false;
            Assert.True(PumpUntil(() =>
            {
                minPanel = Math.Min(minPanel, view.Opacity);
                if (host.Card!.Opacity is > 0.02 and < 0.98) sawCardMidway = true;
                return CardSettledOpen(host);
            }));
            _o.WriteLine($"card seen midway: {sawCardMidway}; lowest panel opacity {minPanel}");
            Assert.Equal(1, minPanel);
            Assert.Equal(1, Pose(view).M11, 4);
            Assert.True(PumpUntil(() => win.FocusManager?.GetFocusedElement() is TextBox { Name: "FirstQueryBox" }));

            // And it still backs out with the regular exit.
            AssertAnimatedPanelClose(win, host, view, vm, () => vm.SearchPanel.CloseCommand.Execute(null));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }
}
