using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26 review pictures of the whole Settings › Plugins page with the situation from his
/// screenshot: Mixxx and Chords installed as verified official copies, A-B Loop, Album Roulette and
/// Add from YouTube installed as copies with no install-time check on record (they showed twice),
/// and three community plugins. Rendered by the REAL SettingsView under Skia. Explicit only: runs
/// when NOCTIS_TEST_SKIA=1 and NOCTIS_PLUGINS_PAGE_SHOTS_DIR names the folder; the file names start
/// with plugins-page-{NOCTIS_PLUGINS_PAGE_SHOTS_TAG} (default "after").
/// </summary>
public class PluginsPageShots1126Tests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
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

    /// <summary>A plugin folder from the repo's plugin.json and icon; the DLL is a stub.</summary>
    private static PluginManifest AddRepoPlugin(PluginSandbox box, string folder)
    {
        var src = PluginSandbox.RepoFile("plugins/" + folder);
        var json = File.ReadAllText(Path.Combine(src, "plugin.json"));
        var manifest = PluginManifest.Parse(json);
        box.WriteFolder(folder, json, (manifest.Entry, new byte[] { 1 }), ("icon.png", File.ReadAllBytes(Path.Combine(src, "icon.png"))));
        return manifest;
    }

    [AvaloniaFact]
    public void PluginsPage_DuplicateSituation_SavePngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGINS_PAGE_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        var tag = Environment.GetEnvironmentVariable("NOCTIS_PLUGINS_PAGE_SHOTS_TAG") is { Length: > 0 } t ? t : "after";
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var catalog = PluginCatalog.LoadEmbedded();
        // Verified official copies: the record a Get plugins install writes (PluginHost.RecordOfficialInstall).
        foreach (var folder in new[] { "Noctis.Plugins.Mixxx", "Noctis.Plugins.Chords" })
        {
            var manifest = AddRepoPlugin(box, folder);
            var entry = catalog.Plugins.Single(e => e.Id == manifest.Id);
            box.Settings.VerifiedOfficialPlugins[manifest.Id] = manifest.Version + "|" + entry.Sha256;
        }
        // Copies with an official id and author Noctis but no record (hand-copied builds).
        foreach (var folder in new[] { "Noctis.Plugins.ABLoop", "Noctis.Plugins.AlbumRoulette", "Noctis.Plugins.YouTubeImport" })
            AddRepoPlugin(box, folder);
        foreach (var folder in new[] { "Noctis.Plugins.AlbumTown", "Noctis.Plugins.TrueShuffle", "Noctis.Plugins.LyricMotion" })
            AddRepoPlugin(box, folder);
        var host = box.NewHost(appVersion: "1.7.1");
        host.LoadAll();

        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory()) { Plugins = host };
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins; // the built-in list

        void Shoot(string name, ThemeVariant theme, double width, double height, Action? before = null)
        {
            var dark = theme == ThemeVariant.Dark;
            AccentTestHarness.WithAccent("#3B82F6", theme, () =>
            {
                var view = new SettingsView { DataContext = vm, Width = width, Height = height };
                var window = new Window
                {
                    Width = width + 40, Height = height + 40,
                    RequestedThemeVariant = theme,
                    Background = new SolidColorBrush(Color.Parse(dark ? "#141318" : "#E9E8EE")),
                    Content = new Border { Child = view, Margin = new Thickness(20) },
                };
                window.Show();
                before?.Invoke();
                Pump(900);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"plugins-page-{tag}-{name}.png"));
                window.Close();
                Pump(100);
            });
        }

        Shoot("dark", ThemeVariant.Dark, 1100, 2500);
        Shoot("light", ThemeVariant.Light, 1100, 2500);
        Shoot("narrow", ThemeVariant.Dark, 760, 2900);
        Shoot("search-discord", ThemeVariant.Dark, 1100, 900, () => vm.PluginSearchText = "discord");
        vm.PluginSearchText = "";
        Shoot("abloop-page", ThemeVariant.Dark, 1100, 900,
            () => vm.OpenPluginCardCommand.Execute(vm.OfficialCards.First(c => c.Id == "dev.noctis.plugins.abloop")));
        vm.ClosePluginDetailCommand.Execute(null);
    }
}
