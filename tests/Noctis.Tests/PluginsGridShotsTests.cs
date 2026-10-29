using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner review pictures of the Plugins grid and a plugin's page, rendered by the REAL
/// SettingsView under Skia: a 2560 x 1440 screen at 125 % (the Settings card at 1000 x 720 DIP,
/// drawn 1.25x). Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_PLUGIN_SHOTS_DIR names
/// the output folder (preview-grid.png, preview-detail.png).
/// </summary>
public class PluginsGridShotsTests
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

    /// <summary>A community plugin folder from the repo's plugin.json and icon (the DLL is a stub:
    /// the plugin waits for approval, so nothing loads).</summary>
    private static void AddRepoPlugin(PluginSandbox box, string folder, string dll)
    {
        var src = PluginSandbox.RepoFile("plugins/" + folder);
        box.WriteFolder(folder, File.ReadAllText(Path.Combine(src, "plugin.json")),
            (dll, new byte[] { 1 }), ("icon.png", File.ReadAllBytes(Path.Combine(src, "icon.png"))));
    }

    /// <summary>
    /// The simpler plugin page and its Remove dialog (owner 10-29), on a 2560 x 1440 screen at
    /// 125 %: the window is 2048 x 1152 DIP rendered at scaling 1.25. Album Roulette carries a
    /// switch, a number (the Settings slider) and a list. NOCTIS_PLUGIN_POLISH_DIR names the
    /// folder (polish-detail.png, polish-remove.png).
    /// </summary>
    [AvaloniaFact]
    public void PluginPagePolish_SavePreviewPngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGIN_POLISH_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        AddRepoPlugin(box, "Noctis.Plugins.AlbumRoulette", "Noctis.Plugins.AlbumRoulette.dll");
        var host = box.NewHost(appVersion: "1.6.6");
        host.LoadAll();

        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory()) { Plugins = host };
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;

        AccentTestHarness.WithAccent("#E86A9A", ThemeVariant.Dark, () =>
        {
            const double scale = 1.25;
            var view = new SettingsView { DataContext = vm, Width = 1000, Height = 1000 };
            var card = new Border
            {
                Child = view,
                CornerRadius = new CornerRadius(22),
                ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 24 60 0 #80000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var window = new Window
            {
                Width = 2560 / scale, Height = 1440 / scale,
                RequestedThemeVariant = ThemeVariant.Dark,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#1B1A20"), 0), new GradientStop(Color.Parse("#0C0C0F"), 1) },
                },
                Content = card,
            };
            window.Show();
            window.SetRenderScaling(scale);
            vm.OpenPluginCardCommand.Execute(vm.CommunityCards.First(c => c.Id == "dev.noctis.plugins.albumroulette"));
            Pump(900);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "polish-detail.png"));

            var dialog = new ConfirmationDialog(new ConfirmationRequest(
                Noctis.Localization.Loc.T("Plugins.RemoveBody"),
                Title: Noctis.Localization.Loc.T("Plugins.RemoveTitle", "Album Roulette"),
                ConfirmText: Noctis.Localization.Loc.T("Plugins.Remove"),
                OptionText: Noctis.Localization.Loc.T("Plugins.RemoveDeleteData"),
                Width: 420))
            {
                Width = window.Width, Height = window.Height, RequestedThemeVariant = ThemeVariant.Dark,
            };
            _ = dialog.ShowDialog(window);
            dialog.SetRenderScaling(scale);
            Pump(900);
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(dir, "polish-remove.png"));
            dialog.Close();
            Pump(300);
            window.Close();
        });
    }

    /// <summary>
    /// The final plugin page (owner 10-29) at 125 %: Album Roulette not installed (Install top-right,
    /// no "..."), installed ("..." top-right, Open under the description, the Settings slider), its
    /// Animation list open on the page, and True Shuffle's Mode list open in its window. Explicit
    /// only: NOCTIS_TEST_SKIA=1 and NOCTIS_PLUGIN_FINAL_DIR (final-*.png).
    /// </summary>
    [AvaloniaFact]
    public async Task PluginPageFinal_SavePreviewPngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGIN_FINAL_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();
        AddToPlaylistDialogTests.EnsureAppStyles();
        Noctis.Helpers.ComboBoxDropDownAnimator.Install();
        const double scale = 1.25;

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var src = PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumRoulette");
        var manifestJson = File.ReadAllText(Path.Combine(src, "plugin.json"));
        var description = System.Text.Json.Nodes.JsonNode.Parse(manifestJson)!["description"]!.GetValue<string>();
        var host = box.NewHost(appVersion: "1.6.6");
        host.LoadAll();

        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory()) { Plugins = host };
        // The official list with Album Roulette on it (not installed yet).
        var server = new PluginCatalogTests.FakeHandler();
        const string zipUrl = "https://github.com/heartached/Noctis/releases/download/plugin-albumroulette-v1.0.0/Noctis.Plugins.AlbumRoulette-1.0.0.zip";
        server[Noctis.Services.Plugins.PluginCatalog.DefaultUrl] = System.Text.Encoding.UTF8.GetBytes(PluginCatalogTests.Index(
            PluginCatalogTests.Entry(id: "dev.noctis.plugins.albumroulette", name: "Album Roulette", version: "1.0.0", download: zipUrl,
                minAppVersion: "1.6.6", extra: new (string, object?)[]
                {
                    ("description", description),
                    ("homepage", "https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.AlbumRoulette"),
                    ("icon", "Noctis.Plugins.AlbumRoulette/icon.png"),
                })));
        vm.PluginCatalogClient = new Noctis.Services.Plugins.PluginCatalogClient(new HttpClient(server), () => null)
        {
            Embedded = () => Noctis.Services.Plugins.PluginCatalog.Empty,
        };
        await vm.LoadOfficialPluginsAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;

        Window NewStage(Control content) => new()
        {
            Width = 2048 / scale, Height = 1152 / scale,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1B1A20"), 0), new GradientStop(Color.Parse("#0C0C0F"), 1) },
            },
            Content = content,
        };

        void HoverSecondRow(Window window, ComboBox list)
        {
            if (list.GetRealizedContainers().OfType<ComboBoxItem>().Skip(1).FirstOrDefault() is not { } row) return;
            var p = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, p);
        }

        var shots = new List<string>();
        AccentTestHarness.WithAccent("#E86A9A", ThemeVariant.Dark, () =>
        {
            var view = new SettingsView { DataContext = vm, Width = 1000, Height = 760 };
            var card = new Border
            {
                Child = view,
                CornerRadius = new CornerRadius(22),
                ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 24 60 0 #80000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var window = NewStage(card);
            window.Show();
            window.SetRenderScaling(scale);
            vm.OpenPluginCardCommand.Execute(vm.OfficialCards.Single(c => c.Id == "dev.noctis.plugins.albumroulette"));
            Pump(900);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "final-detail-not-installed.png"));
            shots.Add("final-detail-not-installed.png");

            // Installed: the same plugin from its folder (the DLL is a stub, so it waits for approval).
            vm.ClosePluginDetailCommand.Execute(null);
            AddRepoPlugin(box, "Noctis.Plugins.AlbumRoulette", "Noctis.Plugins.AlbumRoulette.dll");
            host.LoadAll();
            Pump(300);
            var installed = vm.CommunityCards.Concat(vm.OfficialCards).First(c => c.Id == "dev.noctis.plugins.albumroulette" && c.IsInstalled);
            vm.OpenPluginCardCommand.Execute(installed);
            Pump(900);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "final-detail-installed.png"));
            shots.Add("final-detail-installed.png");

            // Its Animation list open (drawn in the window so the capture has it), a row hovered.
            var list = view.FindControl<StackPanel>("PluginDetailPage")!.GetVisualDescendants().OfType<ComboBox>()
                .First(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Animation");
            list.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "PART_Popup").ShouldUseOverlayLayer = true;
            list.IsDropDownOpen = true;
            Pump(700);
            HoverSecondRow(window, list);
            Pump(200);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "final-dropdown-page.png"));
            shots.Add("final-dropdown-page.png");
            list.IsDropDownOpen = false;
            Pump(400);
            window.Close();

            // True Shuffle's window with its Mode list open.
            box.Approve(("dev.noctis.plugins.trueshuffle", new[] { "playback.control", "library.read", "menu.commands", "notifications" }));
            var shuffleHost = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
            var shuffleManifest = Noctis.Services.Plugins.PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
            var shuffleDir = Path.Combine(box.PluginsDir, shuffleManifest.Id);
            Directory.CreateDirectory(shuffleDir);
            var plugin = new Noctis.Plugins.TrueShuffle.TrueShufflePlugin();
            var loaded = shuffleHost.AddInProcess(shuffleDir, shuffleManifest, () => plugin);
            Control? shuffleView = null;
            // The way Noctis opens it: a PluginViewWindow at the size the plugin asks for.
            shuffleHost.ViewPresenter = (entry, content) => { shuffleView = content; return PluginViewWindow.Present(entry, content); };
            Assert.True(loaded.Views.Single().Open());
            var shuffle = (Window)TopLevel.GetTopLevel(shuffleView!)!;
            shuffle.RequestedThemeVariant = ThemeVariant.Dark;
            shuffle.SetRenderScaling(scale);
            Pump(500);
            var mode = shuffleView!.GetVisualDescendants().OfType<ComboBox>().First();
            Noctis.Helpers.MenuOpenAnimation.ForgetLastPress();
            mode.IsDropDownOpen = true;
            Pump(700);
            HoverSecondRow(shuffle, mode);
            Pump(200);
            shuffle.CaptureRenderedFrame()!.Save(Path.Combine(dir, "final-dropdown-trueshuffle.png"));
            shots.Add("final-dropdown-trueshuffle.png");
            mode.IsDropDownOpen = false;
            Pump(400);
            shuffle.Close();
        });
        Assert.Equal(4, shots.Count);
    }

    [AvaloniaFact]
    public void PluginsGridAndPage_SavePreviewPngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGIN_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        AddRepoPlugin(box, "Noctis.Plugins.AlbumTown", "Noctis.Plugins.AlbumTown.dll");
        // True Shuffle and Lyric Motion are built on another branch: stand-in manifests with their icons.
        foreach (var (folder, id, name, desc) in new[]
                 {
                     ("Noctis.Plugins.TrueShuffle", "dev.alfamin.trueshuffle", "True Shuffle", "Shuffle that never repeats an artist or album back to back, and still feels random."),
                     ("Noctis.Plugins.LyricMotion", "dev.alfamin.lyricmotion", "Lyric Motion", "Lyrics that flow in word by word, with gentle motion on every line."),
                 })
        {
            box.WriteFolder(folder, PluginSandbox.Manifest(id: id, name: name, author: "Alfamin",
                    extra: $"  \"description\": \"{desc}\",\n  \"icon\": \"icon.png\""),
                ("Test.Plugin.dll", new byte[] { 1 }),
                ("icon.png", File.ReadAllBytes(PluginSandbox.RepoFile($"plugins/{folder}/icon.png"))));
        }
        box.WriteFolder("dev.someone.nowplaying", PluginSandbox.Manifest(id: "dev.someone.nowplaying", name: "Now Playing Text",
            author: "Mira K", extra: "  \"description\": \"Writes the song you are listening to into a text file for stream overlays.\""),
            ("Test.Plugin.dll", new byte[] { 1 }));

        var host = box.NewHost(appVersion: "1.6.6");
        host.LoadAll();

        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory()) { Plugins = host };
        vm.SelectedSettingsTab = SettingsViewModel.TabPlugins;

        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            const double scale = 1.25;
            var view = new SettingsView { DataContext = vm, Width = 1000, Height = 720 };
            var card = new Border
            {
                Child = view,
                CornerRadius = new CornerRadius(22),
                ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 24 60 0 #80000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var window = new Window
            {
                Width = 2560, Height = 1440,
                RequestedThemeVariant = ThemeVariant.Dark,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#1B1A20"), 0), new GradientStop(Color.Parse("#0C0C0F"), 1) },
                },
                Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = card },
            };
            window.Show();
            Pump(900);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "preview-grid.png"));

            vm.OpenPluginCardCommand.Execute(vm.CommunityCards.First(c => c.Id == "dev.noctis.plugins.albumtown"));
            Pump(900);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "preview-detail.png"));
            window.Close();
        });
    }
}
