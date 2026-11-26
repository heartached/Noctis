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
/// Owner 11-26 polish round: pictures of the Settings blocks that changed (the folded Music
/// videos browser row, the animated artwork option card, the Pages arrows, the EQ remove X and
/// the page close X). Only runs with real rendering and NOCTIS_POLISH_SHOTS_DIR set.
/// </summary>
public class SettingsPolish1126ShotsTests
{
    private readonly ITestOutputHelper _o;
    public SettingsPolish1126ShotsTests(ITestOutputHelper o) => _o = o;

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
    public async Task ChangedBlocks_SavePngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_POLISH_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        EnsureAppStyles();
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();

        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var view = new SettingsView { DataContext = vm };
            var window = new Window
            {
                Width = 1100, Height = 1300, Content = view,
                RequestedThemeVariant = ThemeVariant.Dark,
                Background = new SolidColorBrush(Color.Parse("#141414")),
            };
            window.Show();
            Pump(300);

            void Shot(string name, string tab, Action set, Func<Control?> find, bool card = true)
            {
                vm.SelectedSettingsTab = tab;
                Pump(200);
                set();
                Pump(900);
                var target = find();
                Assert.NotNull(target);
                target!.BringIntoView();
                Pump(300);
                // BringIntoView stops with the target on the bottom edge: pull it toward the middle.
                var sv = view.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "SettingsScrollViewer");
                var y = target.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                if (y > window.Height / 2) sv.Offset = new Vector(0, sv.Offset.Y + (y - window.Height / 2));
                Pump(400);
                Control box = card
                    ? target.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("setting-card"))
                    : target;
                var origin = box.TranslatePoint(new Point(0, 0), window)!.Value;
                var full = Path.Combine(dir, $"_{name}-full.png");
                window.CaptureRenderedFrame()!.Save(full);
                var path = Path.Combine(dir, $"{name}.png");
                Crop(full, path, new SKRectI(
                    (int)origin.X - 14, (int)origin.Y - 14,
                    (int)(origin.X + box.Bounds.Width) + 14, (int)(origin.Y + box.Bounds.Height) + 14));
                File.Delete(full);
                _o.WriteLine(path);
            }

            T? Named<T>(string name) where T : Control =>
                view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
            Button? Classed(string cls) =>
                view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains(cls) && b.IsEffectivelyVisible);

            Shot("musicvideos-open", SettingsViewModel.TabLyrics, () => vm.MusicVideoUseBrowserCookies = true,
                () => Named<Control>("MusicVideoBrowserSignInToggle"));
            Shot("musicvideos-shut", SettingsViewModel.TabLyrics, () => vm.MusicVideoUseBrowserCookies = false,
                () => Named<Control>("MusicVideoBrowserSignInToggle"));
            Shot("animart", SettingsViewModel.TabAppearance, () => { vm.EnableAnimatedCovers = true; vm.AnimatedArtworkAutoFetch = true; },
                () => view.GetVisualDescendants().OfType<ProgressBar>().FirstOrDefault(p => p.IsEffectivelyVisible && p.Height == 8));
            Shot("pages-arrows", SettingsViewModel.TabGeneral, () => { }, () => Classed("reorder-btn"));
            Shot("eq", SettingsViewModel.TabAudio, () => vm.EqualizerEnabled = true, () => Classed("eq-remove"));
            Shot("close", SettingsViewModel.TabGeneral, () => { },
                () => Classed("settings-close")?.GetVisualAncestors().OfType<Grid>().First(), card: false);
        });
    }

    /// <summary>The colour picker's hex field with the caret after the last digit, 6x zoom:
    /// "before" is the field as it was (no letter spacing, no padding), "after" as it ships.</summary>
    [AvaloniaFact]
    public void HexCaret_SavePngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_POLISH_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();
        foreach (var (name, spacing, pad, caret) in new[] { ("hex-before-end", 0d, 0d, 7), ("hex-after-end", 2d, 2d, 7), ("hex-before-start", 0d, 0d, 0), ("hex-after-start", 2d, 2d, 0), ("hex-before-mid", 0d, 0d, 4), ("hex-after-mid", 2d, 2d, 4) })
        {
            var box = new TextBox
            {
                Text = "#E74856", Watermark = "#RRGGBB",
                FontFamily = new FontFamily("Consolas, Courier New, monospace"), FontWeight = FontWeight.Bold,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(pad, 0), LetterSpacing = spacing,
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Width = 120, Height = 24, MinHeight = 0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(10),
            };
            var window = new Window
            {
                Width = 160, Height = 48, Content = box, RequestedThemeVariant = ThemeVariant.Dark,
                Background = new SolidColorBrush(Color.Parse("#141414")),
            };
            window.Show();
            Pump(100);
            box.Focus();
            box.CaretIndex = caret;
            Pump(120);
            var full = Path.Combine(dir, $"_{name}.png");
            window.CaptureRenderedFrame()!.Save(full);
            using (var bmp = SKBitmap.Decode(full))
            using (var big = bmp.Resize(new SKImageInfo(bmp.Width * 6, bmp.Height * 6), new SKSamplingOptions(SKFilterMode.Nearest)))
            using (var img = SKImage.FromBitmap(big))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(Path.Combine(dir, $"{name}.png")))
                data.SaveTo(fs);
            File.Delete(full);
            window.Close();
        }
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
