using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-10: the About card's update states (idle, checking, up to date, update available,
/// downloading, ready, error) rendered by the REAL SettingsView under Skia, cropped to the card.
/// Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_UPDATE_SHOTS names the file prefix
/// (e.g. "before" / "after"); NOCTIS_UPDATE_SHOTS_DIR overrides the output folder.
/// </summary>
[Collection("AutoUpdate mode")]
public class AboutUpdateCardShotsTests
{
    private readonly ITestOutputHelper _o;
    public AboutUpdateCardShotsTests(ITestOutputHelper o) => _o = o;

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

    [AvaloniaFact]
    public async Task UpdateCardStates_SaveCroppedPngs()
    {
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_UPDATE_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(prefix)) return;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_UPDATE_SHOTS_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "NoctisUpdateShots");
        Directory.CreateDirectory(dir);

        EnsureAppStyles();
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        UpdateService.AutoModeOverride = AutoUpdateMode.InstallAtLaunch; // show the toggle row
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabAbout;

            AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
            {
                var view = new SettingsView { DataContext = vm };
                var window = new Window
                {
                    Width = 920, Height = 720, Content = view,
                    RequestedThemeVariant = ThemeVariant.Dark,
                    Background = new SolidColorBrush(Color.Parse("#141414")),
                };
                window.Show();
                Pump(400);

                void Reset()
                {
                    vm.IsCheckingForUpdate = false;
                    vm.IsUpToDate = false;
                    vm.IsUpdateAvailable = false;
                    vm.IsDownloadingUpdate = false;
                    vm.DownloadProgress = 0;
                    vm.IsReadyToInstall = false;
                    vm.UpdateStatusText = "";
                    vm.LatestVersionTag = "";
                }

                void Shot(string state, Action set)
                {
                    Reset();
                    Pump(60);
                    set();
                    Pump(900);
                    var panel = view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "AboutTabPanel");
                    var card = panel.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("setting-card"));
                    var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
                    var full = Path.Combine(dir, $"_{prefix}-{state}-full.png");
                    window.CaptureRenderedFrame()!.Save(full);
                    var path = Path.Combine(dir, $"{prefix}-{state}.png");
                    Crop(full, path, new SKRectI(
                        (int)origin.X - 12, (int)origin.Y - 12,
                        (int)(origin.X + card.Bounds.Width) + 12, (int)(origin.Y + card.Bounds.Height) + 12));
                    File.Delete(full);
                    _o.WriteLine(path);
                }

                Shot("idle", () => { });
                Shot("checking", () => vm.IsCheckingForUpdate = true);
                Shot("uptodate", () => vm.IsUpToDate = true);
                Shot("available", () =>
                {
                    vm.LatestVersionTag = "v1.6.1";
                    vm.UpdateStatusText = "v1.6.1 is available.";
                    vm.IsUpdateAvailable = true;
                });
                Shot("starting", () =>
                {
                    vm.IsDownloadingUpdate = true;
                    vm.UpdateStatusText = "Downloading update...";
                });
                Shot("downloading", () =>
                {
                    vm.LatestVersionTag = "v1.6.1";
                    vm.IsDownloadingUpdate = true;
                    vm.UpdateStatusText = "Downloading v1.6.1 in the background.";
                    vm.DownloadProgress = 42;
                });
                Shot("verifying", () =>
                {
                    vm.LatestVersionTag = "v1.6.1";
                    vm.IsDownloadingUpdate = true;
                    vm.UpdateStatusText = "Downloading v1.6.1 in the background.";
                    vm.DownloadProgress = 100;
                });
                Shot("ready", () =>
                {
                    vm.LatestVersionTag = "v1.6.1";
                    vm.UpdateStatusText = "Update ready to install.";
                    vm.IsReadyToInstall = true;
                });
                Shot("error", () =>
                {
                    vm.UpdateStatusText = "Download failed. Try again.";
                    vm.UpdateStatusIsError = true;
                });

                window.Close();
            });
        }
        finally
        {
            UpdateService.AutoModeOverride = null;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>While downloading, the top-right Check pill gives way to the download pill, whose
    /// SmoothProgressBar is bound to the 0..1 fraction, sweeps until the first bytes, and shows the
    /// percentage; the old Fluent ProgressBar row is gone.</summary>
    [AvaloniaFact]
    public async Task Downloading_SwapsTheCheckPillForTheDownloadPill()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabAbout;
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Width = 920, Height = 720, Content = view };
            window.Show();
            window.UpdateLayout();

            var panel = view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "AboutTabPanel");
            var check = panel.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("update-pill"));
            var pill = panel.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("update-download-pill"));
            var bar = pill.GetVisualDescendants().OfType<Noctis.Controls.SmoothProgressBar>().Single();
            Assert.Empty(panel.GetVisualDescendants().OfType<ProgressBar>());
            Assert.True(check.IsVisible);
            Assert.False(pill.IsVisible);

            vm.IsDownloadingUpdate = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(check.IsVisible);
            Assert.True(pill.IsVisible);
            Assert.True(bar.IsIndeterminate); // no bytes yet
            Assert.Equal("Starting...", vm.DownloadPillText);

            vm.DownloadProgress = 42;
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(0.42, bar.Value, 3);
            Assert.Equal("42%", vm.DownloadPercentText);

            vm.DownloadProgress = 100;
            Assert.Equal("Verifying...", vm.DownloadPillText);
            Assert.True(vm.IsDownloadVerifying);

            vm.IsDownloadingUpdate = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(check.IsVisible);
            Assert.False(pill.IsVisible);
            window.Close();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Crop(string src, string dst, SKRectI rect)
    {
        using var bmp = SKBitmap.Decode(src);
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, bmp.Width, bmp.Height));
        using var sub = new SKBitmap(rect.Width, rect.Height);
        bmp.ExtractSubset(sub, rect);
        using var img = SKImage.FromBitmap(sub);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(dst);
        data.SaveTo(fs);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
