using System;
using System.IO;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for Settings › Accent: the page rendered under Skia on Light and Ink, saved as
/// PNGs. Runs only with NOCTIS_TEST_SKIA=1 and NOCTIS_ACCENT_SHOTS=&lt;dir&gt;.
/// </summary>
public class MobileAccentShotsTests
{
    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(5);
        }
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task Shots_AccentPage()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ACCENT_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var stack = new MobileThemeSweepTests.PhoneStack();
        // The page as the tester saw it (Cerulean), then with Match Artwork on over a deep red
        // cover: the switch and the footnote in the cover's tamed colour.
        foreach (var (theme, artwork) in new[] { ("Light", false), ("Ink", false), ("Light", true), ("Ink", true) })
        {
            var cover = Avalonia.Media.Color.FromRgb(0x7A, 0x12, 0x14);
            stack.Apply(theme, artwork ? PhoneThemes.EffectiveAccent(theme, "#2A7FCF", false, true, cover) : "#2A7FCF");
            using var rig = MobileFixtures.MakeRig(seed: async p =>
            {
                var s = await p.LoadSettingsAsync();
                s.AccentColorHex = "#2A7FCF";
                s.AccentPresetName = "Cerulean";
                s.MobileAppearance = theme == "Light" ? "Light" : "Dark";
                s.MobileThemeName = "Ink";
                s.MobileAccentFollowsArtwork = artwork;
                await p.SaveSettingsAsync(s);
            });
            var window = MobileFixtures.Mount(rig.Shell, out _);
            try
            {
                rig.Shell.OpenSettingsCommand.Execute(null);
                var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
                await settings.Loaded;
                settings.OpenPaneCommand.Execute(SettingsPane.Accent);
                Pump(600);
                MobileFixtures.Land(window);
                Pump(200);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"accent-{theme}{(artwork ? "-match-artwork" : "")}.png"));
            }
            finally
            {
                window.Close();
            }
        }
    }
}
