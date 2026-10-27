using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #145: Settings ▸ Appearance ▸ Interface Size. A layout transform spliced into every
/// window's template (UiScale/UiScaleHost), popups through Popup.InheritsTransform, a fit cap
/// that keeps a window's designed size on screen. Sizes are measured in window coordinates
/// (what the user sees), not in the transformed control's own units.
/// </summary>
public class UiScaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly List<Window> _windows = new();

    public UiScaleTests() => UiScale.Install();

    public void Dispose()
    {
        foreach (var w in _windows) { try { w.Close(); } catch { } }
        UiScale.SetFactor(1.0); // process-wide
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private T Track<T>(T w) where T : Window { _windows.Add(w); return w; }

    private static void Pump(int ms = 60)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(2);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A control's box as the window draws it.</summary>
    private static Rect OnScreen(Visual v, Visual root)
        => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(root)!.Value);

    /// <summary>Equal within a device pixel (layout rounding at a fractional scale).</summary>
    private static void AssertCovers(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, tolerance: 1.0);
        Assert.Equal(expected.Y, actual.Y, tolerance: 1.0);
        Assert.Equal(expected.Width, actual.Width, tolerance: 1.0);
        Assert.Equal(expected.Height, actual.Height, tolerance: 1.0);
    }

    private static UiScaleHost Host(Window w) => w.GetVisualDescendants().OfType<UiScaleHost>().Single();

    [Fact]
    public void Choices_AndSnapping()
    {
        Assert.Equal(100, new AppSettings().UiScalePercent);
        Assert.Equal(new[] { 90, 100, 110, 125, 150, 175, 200 }, AppSettings.UiScaleChoices);
        Assert.Equal(150, AppSettings.SnapUiScale(140));
        Assert.Equal(200, AppSettings.SnapUiScale(900));
        Assert.Equal(90, AppSettings.SnapUiScale(-5));
        var s = new AppSettings { UiScalePercent = 133 };
        s.ClampToValidRanges();
        Assert.Equal(125, s.UiScalePercent);
    }

    [Fact]
    public void Effective_CapsAtTheFitButNeverBelowOneHundredPercent()
    {
        var room = new Size(1280, 800);
        Assert.Equal(2.0, UiScale.Effective(2.0, room, default));
        Assert.Equal(1.6, UiScale.Effective(2.0, room, new Size(780, 500)));      // 800/500
        Assert.Equal(1.0, UiScale.Effective(2.0, room, new Size(2000, 2000)));    // floor 100%
        Assert.Equal(0.9, UiScale.Effective(0.9, room, new Size(2000, 2000)));    // a shrink stays
        Assert.Equal(1.25, UiScale.Effective(1.25, room, new Size(double.NaN, 500)));
    }

    /// <summary>100% must be pixel-identical: the real Settings ▸ Appearance page laid out in a
    /// window with the host and in one opted out of it gives the same box for every element.</summary>
    [AvaloniaFact]
    public async Task Default_LayoutIsIdenticalToNoHost()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;

        List<(string, Rect)> Layout(bool exclude)
        {
            var view = new SettingsView { DataContext = vm };
            var w = Track(new Window { Width = 1100, Height = 760, Content = view });
            UiScale.SetExclude(w, exclude);
            w.Show();
            Pump();
            Assert.Equal(!exclude, w.GetVisualDescendants().OfType<UiScaleHost>().Any());
            if (!exclude) Assert.Null(Host(w).LayoutTransform);
            return view.GetVisualDescendants().Where(v => v.IsVisible)
                .Select(v => (v.GetType().Name, OnScreen(v, w))).ToList();
        }

        var plain = Layout(exclude: true);
        var hosted = Layout(exclude: false);
        Assert.True(plain.Count > 500, $"only {plain.Count} visuals");
        Assert.Equal(plain, hosted);
    }

    /// <summary>Applied live: the same controls are drawn 1.5× / 2× as large, and back.</summary>
    [AvaloniaFact]
    public void Scale_AppliesLive_AndScalesEverythingProportionally()
    {
        // Left-aligned: a stretched control fills the window at every scale.
        var label = new TextBlock { Text = "Interface Size", FontSize = 15, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var button = new Button { Content = "Play", Padding = new Thickness(14, 8) };
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(1, 50).Select(i => $"Song {i}").ToList(),
            Width = 320, Height = 300, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        var panel = new StackPanel { Spacing = 8, Children = { label, button, list } };
        var w = Track(new Window { Width = 1600, Height = 1200, Content = panel });
        w.Show();
        Pump();
        var before = new[] { label, button, (Control)list }.Select(c => OnScreen(c, w)).ToArray();
        var rowBefore = OnScreen(list.GetVisualDescendants().OfType<ListBoxItem>().First(), w);

        foreach (var f in new[] { 1.5, 2.0 })
        {
            UiScale.SetFactor(f);
            Pump();
            Assert.Equal(f, UiScale.GetApplied(w));
            var after = new[] { label, button, (Control)list }.Select(c => OnScreen(c, w)).ToArray();
            for (var i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i].Width * f, after[i].Width, 1);
                Assert.Equal(before[i].Height * f, after[i].Height, 1);
                Assert.Equal(before[i].Y * f, after[i].Y, 1);
            }
            var rowAfter = OnScreen(list.GetVisualDescendants().OfType<ListBoxItem>().First(), w);
            Assert.Equal(rowBefore.Height * f, rowAfter.Height, 1);

            // Hit-testing follows the transform: the middle of the scaled button is the button.
            var mid = OnScreen(button, w).Center;
            Assert.Contains(button, w.GetVisualsAt(mid).SelectMany(v => v.GetSelfAndVisualAncestors()));
        }

        UiScale.SetFactor(1.0);
        Pump();
        Assert.Null(Host(w).LayoutTransform);
        Assert.Equal(before[1], OnScreen(button, w));
    }

    /// <summary>Menus, tooltips and flyouts live in their own popup hosts outside the window's
    /// transform; while scaled they inherit their target's scale, at 100% they do not.</summary>
    [AvaloniaFact]
    public void Popups_FollowTheScale()
    {
        var button = new Button { Content = "More", Width = 100, Height = 30 };
        var menu = new ContextMenu { ItemsSource = new[] { "Play Next", "Add to Playlist" } };
        button.ContextMenu = menu;
        var tip = new TextBlock { Text = "tip" };
        ToolTip.SetTip(button, tip);
        var flyoutText = new TextBlock { Text = "flyout" };
        var flyout = new Flyout { Content = flyoutText };
        var w = Track(new Window { Width = 800, Height = 600, Content = new StackPanel { Children = { button } } });
        w.Show();
        Pump();

        static double PopupScale(Visual inside)
            => inside.GetVisualAncestors().OfType<LayoutTransformControl>()
                   .FirstOrDefault(l => l is not UiScaleHost)?.LayoutTransform?.Value.M11 ?? 1.0;

        foreach (var f in new[] { 1.0, 1.5 })
        {
            UiScale.SetFactor(f);
            Pump();
            menu.Open(button);
            Pump();
            Assert.Equal(f != 1.0, ((Avalonia.Controls.Primitives.Popup)menu.Parent!).InheritsTransform);
            Assert.Equal(f, PopupScale(menu), 3);
            menu.Close();

            ToolTip.SetIsOpen(button, true);
            Pump();
            Assert.Equal(f, PopupScale(tip), 3);
            ToolTip.SetIsOpen(button, false);

            flyout.ShowAt(button);
            Pump();
            Assert.Equal(f, PopupScale(flyoutText), 3);
            flyout.Hide();
            Pump();
        }
    }

    /// <summary>The main window never lays out below its designed minimum (780×500): the
    /// minimum grows with the scale, and whatever size the window ends up at, the content has at
    /// least 780×500 to lay out in. Back at 100% the minimum is the designed one again.</summary>
    [AvaloniaFact]
    public void MainWindow_NeverLaysOutBelowItsDesignedMinimum()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var w = Track(new MainWindow { Width = 780, Height = 500 });
        w.Show();
        Pump();
        Assert.Equal(780, w.MinWidth);

        foreach (var (f, width, height) in new[] { (2.0, 780.0, 500.0), (2.0, 1300.0, 900.0), (1.5, 1600.0, 1000.0) })
        {
            UiScale.SetFactor(f);
            w.Width = width;
            w.Height = height;
            Pump();
            Assert.Equal(780 * f, w.MinWidth);                  // headless: no screen to cap it
            Assert.Equal(500 * f, w.MinHeight);
            var content = UiScale.ContentSize(w);
            Assert.True(content.Width >= 780 - 0.5 && content.Height >= 500 - 0.5,
                $"{f}: client {w.ClientSize}, applied {UiScale.GetApplied(w)}, content {content}");
            // The content fills the window exactly at the applied scale.
            Assert.Equal(w.ClientSize.Width, OnScreen((Control)w.Content!, w).Width, 0);
        }

        UiScale.SetFactor(1.0);
        Pump();
        Assert.Equal(780, w.MinWidth);
        Assert.Equal(500, w.MinHeight);
        Assert.Equal(1.0, UiScale.GetApplied(w));
    }

    /// <summary>The Settings sheet (where Interface Size is picked) must stay whole when the
    /// content has only its designed minimum width — at 200% that is a normal 1560 px window.
    /// It was a fixed 1000 px card, cut off on both sides (close button and rail edge) below
    /// 1000 px of room.</summary>
    [AvaloniaTheory]
    [InlineData(1.0, 780.0, 500.0)]
    [InlineData(2.0, 1560.0, 1000.0)]
    [InlineData(1.25, 1280.0, 800.0)]
    [InlineData(1.0, 1280.0, 800.0)]
    public void SettingsSheet_FitsTheWindow(double f, double width, double height)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        UiScale.SetFactor(f);
        var w = Track(new MainWindow { Width = width, Height = height });
        w.Show();
        w.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
        Pump();
        foreach (var name in new[] { "SettingsCard", "SettingsGlass" })
        {
            var box = OnScreen(w.FindControl<Control>(name)!, w);
            Assert.True(box.X >= 0 && box.Right <= w.ClientSize.Width + 0.5 && box.Y >= 0 && box.Bottom <= w.ClientSize.Height + 0.5,
                $"{name} at {f}: {box} in {w.ClientSize}");
            Assert.Equal(box.X, w.ClientSize.Width - box.Right, 0.5); // centred
            // With room for it, the card keeps its designed 1000 px, as before.
            var card = w.FindControl<Control>(name)!;
            if (UiScale.ContentSize(w).Width >= 1048) Assert.Equal(1000, card.Bounds.Width);
        }
    }

    /// <summary>A pop-up's card stays on screen: the window scales only as far as the card
    /// (plus a gutter) fits; the backdrop still covers the owner 1:1.</summary>
    [AvaloniaFact]
    public void PillDialog_ScalesOnlyAsFarAsTheCardFits_AndTheBackdropStaysOneToOne()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var card = new Grid { Width = 852, Height = 740 };
        var host = new PillDialogHost { Content = card };
        var w = Track(new Window { Width = 1280, Height = 800, Content = host });
        UiScale.SetFactor(2.0);
        w.Show();
        Pump();

        // (852+48) × (740+48) in 1280×800: 800/788 → 1.02.
        Assert.Equal(1.02, UiScale.GetApplied(w));
        var box = OnScreen(card, w);
        Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= 1280 && box.Bottom <= 800, box.ToString());

        // An owner-sized snapshot placed at window (0,0) must cover exactly the window.
        var image = host.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "PART_Backdrop");
        image.Source = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(16, 10), new Vector(96, 96));
        host.PlaceBackdrop(new Rect(0, 0, 1280, 800));
        Pump();
        AssertCovers(new Rect(0, 0, 1280, 800), OnScreen(image, w));

        // A big enough window takes the whole 200%, and the backdrop follows the new scale.
        w.Width = 2000;
        w.Height = 1700;
        Pump();
        Assert.Equal(2.0, UiScale.GetApplied(w));
        Assert.Equal(852 * 2, OnScreen(card, w).Width, tolerance: 1.0); // the open pose may still be settling
        host.PlaceBackdrop(new Rect(0, 0, 2000, 1700));
        Pump();
        AssertCovers(new Rect(0, 0, 2000, 1700), OnScreen(image, w));
    }

    /// <summary>The setting persists, loads, applies the scale, and a hand-edited value snaps.</summary>
    [AvaloniaFact]
    public async Task Setting_PersistsAndApplies()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { UiScalePercent = 175 });
        Assert.Equal(175, PersistenceService.ReadIntSettingAtLaunch(_root, nameof(AppSettings.UiScalePercent), 100));
        Assert.Equal(100, PersistenceService.ReadIntSettingAtLaunch(Path.Combine(_root, "missing"), nameof(AppSettings.UiScalePercent), 100));

        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        Assert.Equal(175, vm.UiScalePercent);
        Assert.Equal(1.75, UiScale.Factor);
        Assert.Equal(175, vm.SelectedUiScaleOption!.Percent);
        Assert.Equal(7, vm.UiScaleOptions.Count);

        vm.SelectedUiScaleOption = vm.UiScaleOptions.Single(o => o.Percent == 125);
        Assert.Equal(1.25, UiScale.Factor);
        await vm.SaveAsync();
        Assert.Equal(125, (await persistence.LoadSettingsAsync()).UiScalePercent);

        vm.UiScalePercent = 160;                     // not a choice: snaps
        Assert.Equal(150, vm.UiScalePercent);
        vm.SelectedUiScaleOption = null;             // ComboBox rebuild: ignored
        Assert.Equal(150, vm.UiScalePercent);
    }

    /// <summary>Settings search finds the card, on the Appearance page.</summary>
    [AvaloniaFact]
    public async Task SettingsSearch_FindsInterfaceSize()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        var view = new SettingsView { DataContext = vm };
        var w = Track(new Window { Width = 920, Height = 720, Content = view });
        w.Show();
        var box = view.FindControl<TextBox>("SettingsSearchBox")!;
        box.Text = "interface size";
        Pump();
        Assert.Equal(SettingsViewModel.TabAppearance, vm.SelectedSettingsTab);
        Assert.True(vm.Sections.Single(s => s.Key == SettingsViewModel.TabAppearance).MatchCount >= 1);
        var combo = view.FindControl<ComboBox>("UiScaleCombo")!;
        Assert.Equal("Interface Size", Avalonia.Automation.AutomationProperties.GetName(combo));
        box.Text = "";
    }

    /// <summary>Eyeball captures: Settings ▸ Appearance (with its picker open) and the main
    /// window at 100/150/200%. Explicit only: NOCTIS_TEST_SKIA=1 and NOCTIS_UI145_SHOTS = folder.</summary>
    [AvaloniaFact]
    public async Task Shots_SettingsAndMainWindow()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_UI145_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        AddToPlaylistDialogTests.EnsureAppStyles();
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;

        foreach (var pct in new[] { 100, 150, 200 })
        {
            vm.UiScalePercent = pct;
            var view = new SettingsView { DataContext = vm };
            var sw = Track(new Window { Width = 1600, Height = 1000, Content = view, Background = Avalonia.Media.Brushes.Black });
            sw.Show();
            Pump(400);
            var combo = view.FindControl<ComboBox>("UiScaleCombo")!;
            combo.BringIntoView();
            Pump(200);
            sw.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"settings-appearance-{pct}.png"));
            combo.IsDropDownOpen = true;
            Pump(400);
            sw.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"settings-picker-open-{pct}.png"));
            combo.IsDropDownOpen = false;
            sw.Close();

            var main = Track(new MainWindow { Width = 1600, Height = 1000 });
            main.Show();
            Pump(400);
            main.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"main-window-{pct}.png"));
            main.Close();
        }
    }
}
