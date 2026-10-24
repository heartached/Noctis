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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Appearance › App Icon (owner 10-24): ten generated icons, picked live for the
/// window/taskbar/tray, persisted as AppSettings.AppIcon. Also guards the icon files themselves:
/// every DPI frame Windows asks for (16/24/32 x 100-300 %) must be present at its exact size,
/// otherwise the shell stretches a neighbouring frame and the taskbar icon goes soft.
/// </summary>
public class AppIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _o;
    public AppIconTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { AppIconService.Apply(AppIconService.DefaultId); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly int[] RequiredFrames =
        { 16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 60, 64, 72, 80, 96, 128, 256 };

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
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

    /// <summary>(width, height, isPng, png pixel width, png pixel height) per ICO entry.</summary>
    private static List<(int W, int H, bool Png, int PngW, int PngH)> ReadIco(byte[] d)
    {
        Assert.Equal(0, BitConverter.ToUInt16(d, 0));
        Assert.Equal(1, BitConverter.ToUInt16(d, 2));
        int n = BitConverter.ToUInt16(d, 4);
        var list = new List<(int, int, bool, int, int)>();
        for (int i = 0; i < n; i++)
        {
            int e = 6 + 16 * i;
            int w = d[e] == 0 ? 256 : d[e], h = d[e + 1] == 0 ? 256 : d[e + 1];
            int off = BitConverter.ToInt32(d, e + 12);
            bool png = d[off] == 0x89 && d[off + 1] == (byte)'P';
            int pw = 0, ph = 0;
            if (png)
            {
                pw = (d[off + 16] << 24) | (d[off + 17] << 16) | (d[off + 18] << 8) | d[off + 19];
                ph = (d[off + 20] << 24) | (d[off + 21] << 16) | (d[off + 22] << 8) | d[off + 23];
            }
            list.Add((w, h, png, pw, ph));
        }
        return list;
    }

    private static byte[] ReadAsset(string uri)
    {
        using var s = AssetLoader.Open(new Uri(uri));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void AssertAllFrames(string label, byte[] ico)
    {
        var frames = ReadIco(ico);
        foreach (var size in RequiredFrames)
        {
            var f = frames.Where(x => x.W == size).ToList();
            Assert.True(f.Count == 1, $"{label}: expected one {size}px frame, found {f.Count}");
            Assert.True(f[0].Png && f[0].PngW == size && f[0].PngH == size,
                $"{label}: {size}px entry holds a {f[0].PngW}x{f[0].PngH} image");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void Normalize_FallsBackToClassic()
    {
        Assert.Equal("classic", AppIconService.Normalize(null));
        Assert.Equal("classic", AppIconService.Normalize(""));
        Assert.Equal("classic", AppIconService.Normalize("rainbow"));
        Assert.Equal("neon", AppIconService.Normalize(" Neon "));
        Assert.Equal(10, AppIconService.Ids.Count);
        Assert.Equal("classic", AppIconService.Ids[0]);
        Assert.Equal("classic", new AppSettings().AppIcon);
    }

    [AvaloniaFact]
    public void EveryIcon_CarriesEveryDpiFrame_AndAPreview()
    {
        foreach (var id in AppIconService.Ids)
        {
            AssertAllFrames(id, ReadAsset(AppIconService.IconUri(id)));
            var png = ReadAsset(AppIconService.PreviewUri(id)); // header: the headless stub decodes everything as 1x1
            Assert.Equal(0x89, png[0]);
            Assert.Equal(96, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.NotNull(AppIconService.Load(id));
        }
        // Classic copies: the window/tray fallback and the exe/installer icon.
        AssertAllFrames("Noctis.UI Noctis.ico", ReadAsset("avares://Noctis.UI/Assets/Icons/Noctis.ico"));
        AssertAllFrames("exe Noctis.ico", File.ReadAllBytes(Path.Combine(FindRepoRoot(), "src", "Noctis", "Assets", "Icons", "Noctis.ico")));
    }

    [AvaloniaFact]
    public void Apply_SwapsTheIconOfOpenWindows_AndNewWindowsOpenWithIt()
    {
        AppIconService.Attach();
        AppIconService.Apply("classic");
        var changes = 0;
        EventHandler onChanged = (_, _) => changes++;
        AppIconService.Changed += onChanged;
        var main = new Window { Width = 200, Height = 100 };
        var mini = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            mini.Show();
            Assert.Same(AppIconService.Load("classic"), main.Icon);

            AppIconService.Apply("neon");
            Assert.Equal("neon", AppIconService.CurrentId);
            Assert.Same(AppIconService.Load("neon"), main.Icon);
            Assert.Same(AppIconService.Load("neon"), mini.Icon);
            Assert.Equal(1, changes);

            AppIconService.Apply("NEON"); // same icon: no change event
            Assert.Equal(1, changes);

            var dialog = new Window { Width = 100, Height = 100 };
            dialog.Show();
            Assert.Same(AppIconService.Load("neon"), dialog.Icon);
            dialog.Close();

            AppIconService.Apply("bogus");
            Assert.Equal("classic", AppIconService.CurrentId);
            Assert.Same(AppIconService.Load("classic"), mini.Icon);
            Assert.Equal(2, changes);
        }
        finally
        {
            AppIconService.Changed -= onChanged;
            main.Close();
            mini.Close();
        }
    }

    [AvaloniaFact]
    public async Task Setting_PersistsAndIsAppliedOnLoad()
    {
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        Assert.Equal("classic", vm.AppIcon);
        Assert.True(vm.AppIconOptions.Single(o => o.IsSelected).Id == "classic");

        vm.SetAppIconCommand.Execute("sunset");
        Assert.Equal("sunset", vm.AppIcon);
        Assert.Equal("sunset", AppIconService.CurrentId);
        Assert.Equal(new[] { "sunset" }, vm.AppIconOptions.Where(o => o.IsSelected).Select(o => o.Id));
        await vm.SaveAsync();
        Assert.Equal("sunset", (await persistence.LoadSettingsAsync()).AppIcon);

        AppIconService.Apply("classic");
        var reloaded = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        await reloaded.LoadAsync();
        Assert.Equal("sunset", reloaded.AppIcon);
        Assert.Equal("sunset", AppIconService.CurrentId); // applied at startup

        // A hand-edited / future id lands on classic instead of a blank icon.
        var s = await persistence.LoadSettingsAsync();
        s.AppIcon = "rainbow";
        await persistence.SaveSettingsAsync(s);
        var odd = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        await odd.LoadAsync();
        Assert.Equal("classic", odd.AppIcon);
        Assert.Equal("classic", AppIconService.CurrentId);
    }

    [AvaloniaFact]
    public async Task AppearanceTab_ShowsTenRealIconTiles_AndATapSwapsTheWindowIcon()
    {
        AppIconService.Attach();
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        try
        {
            Pump(150);
            var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "AppIconList");
            var tiles = list.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(10, tiles.Count);
            Assert.All(tiles, t => Assert.NotNull(t.GetVisualDescendants().OfType<Image>().Single().Source));
            Assert.Single(tiles, t => t.Classes.Contains("active"));
            Assert.Equal("Classic", Avalonia.Automation.AutomationProperties.GetName(tiles[0]));

            var violet = tiles[4];
            violet.Command!.Execute(violet.CommandParameter);
            Pump(50);
            Assert.Equal("violet", vm.AppIcon);
            Assert.True(violet.Classes.Contains("active"));
            Assert.Single(tiles, t => t.Classes.Contains("active"));
            Assert.Same(AppIconService.Load("violet"), window.Icon);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Evidence probe (explicit: NOCTIS_TEST_SKIA=1 and NOCTIS_ICON_PROBE=dir): renders logo
    /// PNGs listed in NOCTIS_ICON_PROBE_FILES (';'-separated) through a real Image control at
    /// 40 and 60 px (the About logo at 100 % / 150 %), saving what Skia draws.
    /// </summary>
    [AvaloniaFact]
    public void Probe_RenderLogoSourcesThroughImage()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ICON_PROBE");
        var files = Environment.GetEnvironmentVariable("NOCTIS_ICON_PROBE_FILES");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(files)) return;
        Directory.CreateDirectory(dir);
        foreach (var file in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var bmp = new Bitmap(file);
            foreach (var px in new[] { 28, 40, 48, 60 })
            {
                var image = new Image { Source = bmp, Width = px, Height = px, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                var window = new Window { Width = px, Height = px, Background = Brushes.White, Content = image };
                window.Show();
                Pump(80);
                var outPath = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}@{px}.png");
                window.CaptureRenderedFrame()!.Save(outPath);
                window.Close();
                _o.WriteLine(outPath);
            }
        }
    }
}
