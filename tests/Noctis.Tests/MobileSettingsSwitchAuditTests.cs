using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Every switch and slider in every Settings page, pressed through the view: each has a name a
/// screen reader says, each change reaches the model at once (Settings closed and opened again
/// shows it), is written to settings.json, and is read back by a new app over the same data (a
/// restart).
/// </summary>
public class MobileSettingsSwitchAuditTests(ITestOutputHelper output)
{
    private static void Pump(Window window, int frames = 4)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    /// <summary>A second app over <paramref name="root"/>'s data, as after a restart.</summary>
    private static ShellViewModel Restart(string root)
    {
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var history = new FakeHistoryLog();
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, history, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), history, marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())))
        {
            TintFactory = () => new PageTint(_ => null, work => Task.FromResult(work())),
            ArtworkAccentSource = _ => null,
            RunArtworkAccent = work => Task.FromResult(work()),
        };
        Task.Run(shell.Library.InitializeAsync).GetAwaiter().GetResult();
        return shell;
    }

    private static readonly SettingsPane[] Panes =
        Enum.GetValues<SettingsPane>().Where(p => p is not (SettingsPane.WhatsNew or SettingsPane.Licenses)).ToArray();

    /// <summary>The switches and sliders the open page shows (and takes a touch on).</summary>
    private static List<Control> Controls(SettingsPage page, bool enabledOnly = true) =>
        page.GetVisualDescendants().OfType<Control>()
            .Where(c => (c is ToggleButton t && t.Classes.Contains("switch-row")) || c is SmoothSlider)
            .Where(c => c.IsEffectivelyVisible && (!enabledOnly || c.IsEffectivelyEnabled) && !string.IsNullOrEmpty(c.Name))
            .ToList();

    private static string Key(SettingsPane pane, Control c) =>
        $"{pane}/{c.Name}" + (c is ToggleButton { DataContext: TabBarChoice choice } ? ":" + choice.Tab : "");

    private static object? Value(Control c) => c switch
    {
        ToggleButton t => t.IsChecked,
        SmoothSlider s => Math.Round(s.SteppedValue, 3),
        _ => null,
    };

    private static async Task<SettingsPageViewModel> OpenSettings(ShellViewModel shell, Window window)
    {
        shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)shell.CurrentPage!;
        await vm.Loaded;
        MobileFixtures.Land(window);
        Pump(window);
        return vm;
    }

    private static void ShowPane(SettingsPageViewModel vm, SettingsPane pane, Window window)
    {
        vm.OpenPaneCommand.Execute(pane);
        MobileFixtures.Land(window);
        Pump(window);
    }

    /// <summary>Every save the pages may still have in flight, then settings.json left still.</summary>
    private static async Task Saved(ShellViewModel shell, SettingsPageViewModel vm, string root, Window window)
    {
        var path = Path.Combine(root, "settings.json");
        string? last = null;
        var still = 0;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && still < 8)
        {
            Pump(window, 1);
            await Task.WhenAll(vm.PendingSave, shell.PendingPlayerBarSave, shell.Lyrics.PendingSave);
            await Task.Delay(60);
            var now = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            still = now == last ? still + 1 : 0;
            last = now;
        }
    }

    /// <param name="blurredCover">Now Playing on Blurred Cover, whose Blur and Dim sliders show only then.</param>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySwitchAndSlider_IsNamed_AppliesLive_Saves_AndLoadsAfterARestart(bool blurredCover)
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        await rig.Shell.InitializeAsync();
        if (blurredCover)
        {
            rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
            await rig.Shell.PendingPlayerBarSave;
        }
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var expected = new Dictionary<string, object?>();
        var problems = new List<string>();
        try
        {
            // ── Press each one, once, in every page ──
            var vm = await OpenSettings(rig.Shell, window);
            var page = MobileFixtures.Find<SettingsPage>(view);
            foreach (var pane in Panes)
            {
                ShowPane(vm, pane, window);
                // Asked again after each press: a press can rebuild the rows (Tab Bar) or show others.
                // From the bottom up, so a switch that turns off the ones under it (Reduce Glass
                // greys Liquid Glass Tab) comes after them.
                while (Controls(page).LastOrDefault(c => !expected.ContainsKey(Key(pane, c))) is { } control)
                {
                    var key = Key(pane, control);
                    if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)))
                        problems.Add($"{key}: no accessible name");
                    switch (control)
                    {
                        case ToggleButton toggle:
                            toggle.IsChecked = !(toggle.IsChecked ?? false);
                            break;
                        case SmoothSlider slider:
                            // One step off where it is, inside its range.
                            var step = slider.Step > 0 ? slider.Step : (slider.Maximum - slider.Minimum) / 10;
                            slider.Value = slider.SteppedValue + step <= slider.Maximum ? slider.SteppedValue + step : slider.SteppedValue - step;
                            break;
                    }
                    Pump(window);
                    // Read back from the row now showing that setting (Tab Bar's rows move as tabs go on).
                    var now = page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == control.Name && Key(pane, c) == key && c.IsEffectivelyVisible);
                    expected[key] = Value(now ?? control);
                }
            }
            output.WriteLine($"pressed {expected.Count}: {string.Join(", ", expected.Keys)}");
            await Saved(rig.Shell, vm, rig.Root, window);

            // ── Live: Settings closed and opened again reads the model ──
            for (var i = 0; i < 4 && rig.Shell.CurrentPage is SettingsPageViewModel; i++) rig.Shell.TryHandleBack();
            Assert.IsNotType<SettingsPageViewModel>(rig.Shell.CurrentPage);
            MobileFixtures.Land(window);
            Pump(window);
            vm = await OpenSettings(rig.Shell, window);
            page = MobileFixtures.Find<SettingsPage>(view);
            Check("live", vm, page);
        }
        finally
        {
            window.Close();
        }

        // ── Restart: a new app over the same data ──
        var again = Restart(rig.Root);
        await again.InitializeAsync();
        var window2 = MobileFixtures.Mount(again, out var view2);
        try
        {
            var vm2 = await OpenSettings(again, window2);
            Check("after restart", vm2, MobileFixtures.Find<SettingsPage>(view2));
        }
        finally
        {
            window2.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        void Check(string when, SettingsPageViewModel settings, SettingsPage shown)
        {
            var seen = new HashSet<string>();
            foreach (var pane in Panes)
            {
                ShowPane(settings, pane, (Window)TopLevel.GetTopLevel(shown)!);
                foreach (var control in Controls(shown, enabledOnly: false))
                {
                    var key = Key(pane, control);
                    if (!expected.TryGetValue(key, out var want) || !seen.Add(key)) continue;
                    if (!Equals(want, Value(control))) problems.Add($"{key} {when}: {Value(control)}, pressed to {want}");
                }
            }
            foreach (var key in expected.Keys.Where(k => !seen.Contains(k)))
                output.WriteLine($"{key} {when}: not shown (another switch hides it)");
        }
    }
}
