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
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: the Settings > Lyrics Audio Visualizer block, rendered by the REAL SettingsView
/// under Skia, frame by frame through an open, a full close, and a close that lands while the
/// open is still running (the case that used to vanish). Separate PNGs, never a video.
/// Explicit only: NOCTIS_TEST_SKIA=1 and NOCTIS_GLIDE_SHOTS_DIR = output folder.
/// </summary>
public class VisualizerGlideShotsTests(ITestOutputHelper output)
{
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
    public async Task AudioVisualizerBlock_OpenAndClose_SavePngFrames()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_GLIDE_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.LyricsVisualizerEnabled = false;
            vm.SelectedSettingsTab = SettingsViewModel.TabLyrics;

            AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
            {
                var view = new SettingsView { DataContext = vm };
                var window = new Window
                {
                    Width = 920, Height = 900, Content = view,
                    RequestedThemeVariant = ThemeVariant.Dark,
                    Background = new SolidColorBrush(Color.Parse("#141414")),
                };
                window.Show();
                Pump(500);

                var block = view.GetVisualDescendants().OfType<CollapsibleContent>()
                    .Single(c => c.GetVisualDescendants().OfType<RadioButton>().Any(r => r.GroupName == "LyricsVisualizerStyle"));
                // the block is shut, so find its row through the logical tree
                var row = block.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("setting-row"));
                row.BringIntoView();
                Pump(300);

                var origin = row.TranslatePoint(default, window)!.Value;
                // Fixed crop that fits the open row, so every frame shows the same window region.
                var crop = new SKRectI((int)origin.X - 12, (int)origin.Y - 12,
                    (int)(origin.X + row.Bounds.Width) + 12, (int)origin.Y + 330);

                var n = 0;
                void Frame(string phase)
                {
                    var full = Path.Combine(dir, "_full.png");
                    window.CaptureRenderedFrame()!.Save(full);
                    var path = Path.Combine(dir, $"{++n:00}-{phase}-h{block.Bounds.Height:0}-vis{(block.IsVisible ? 1 : 0)}.png");
                    Crop(full, path, crop);
                    File.Delete(full);
                    output.WriteLine($"{path}  reveal={block.Reveal:F2} visible={block.IsVisible} rowH={row.Bounds.Height:F0}");
                }

                // A capture takes longer than a frame, so each PNG is its own run of the motion,
                // stopped at a later point: open fully, start the close, wait d ms, capture.
                var delays = new[] { 0, 40, 80, 120, 160, 200, 300 };
                Frame("shut");
                foreach (var d in delays)
                {
                    vm.LyricsVisualizerEnabled = false; Pump(400);
                    vm.LyricsVisualizerEnabled = true; Pump(d);
                    Frame($"open-at{d}ms");
                }
                foreach (var d in delays)
                {
                    vm.LyricsVisualizerEnabled = true; Pump(600);
                    vm.LyricsVisualizerEnabled = false; Pump(d);
                    Frame($"close-at{d}ms");
                }
                // The owner's case: switched off again while the open is still gliding (110 ms in).
                foreach (var d in delays)
                {
                    vm.LyricsVisualizerEnabled = false; Pump(400);
                    vm.LyricsVisualizerEnabled = true; Pump(110);
                    vm.LyricsVisualizerEnabled = false; Pump(d);
                    Frame($"close-midopen-at{d}ms");
                }

                window.Close();
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
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
}
