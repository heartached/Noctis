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
using Avalonia.Input;
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
/// Discord 11-25 ("Albums Hover Edge Bug"): the real Albums grid under Skia with Album Hover Zoom
/// on: an edge tile hovered the first and the second time, a middle tile hovered, and a hovered
/// Ctrl-selected tile, in the dark and light themes. Explicit only: runs when NOCTIS_TEST_SKIA=1
/// and NOCTIS_HOVER_SHOTS names the file prefix ("before" / "after"); NOCTIS_HOVER_SHOTS_DIR
/// overrides the output folder.
/// </summary>
public class AlbumHoverShotsTests
{
    private readonly ITestOutputHelper _o;
    public AlbumHoverShotsTests(ITestOutputHelper o) => _o = o;

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

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static string MakeCover(string dir, int i)
    {
        var colours = new[] { "#F2F2F2", "#C0392B", "#1F3A5F", "#2E7D32", "#F4D03F", "#6C3483", "#111111", "#E67E22" };
        var path = Path.Combine(dir, $"cover{i}.png");
        using var bmp = new SKBitmap(256, 256);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColor.Parse(colours[i % colours.Length]));
            using var p = new SKPaint { Color = SKColor.Parse(i % 2 == 0 ? "#40000000" : "#40FFFFFF"), IsAntialias = true };
            c.DrawCircle(128, 128, 70, p);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
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

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public void HoverStates_SavePngs(string theme)
    {
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_HOVER_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(prefix)) return;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_HOVER_SHOTS_DIR") ?? Path.Combine(Path.GetTempPath(), "NoctisHoverShots");
        Directory.CreateDirectory(dir);
        var art = Path.Combine(Path.GetTempPath(), "NoctisTests", "hover-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(art);

        EnsureAppStyles();
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentTestHarness.WithAccent("#E74856", variant, () =>
        {
            var lib = new FakeLibraryService();
            for (var i = 0; i < 40; i++)
                ((List<Album>)lib.Albums).Add(new Album
                {
                    Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020,
                    ArtworkPath = MakeCover(art, i), Tracks = new List<Track>(),
                });
            var persistence = new TestPersistenceService();
            var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
            var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
            settings.AlbumHoverZoom = true;
            var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
            vm.AlbumSortMode = "title";
            var view = new LibraryAlbumsView { DataContext = vm };
            var win = new Window
            {
                Width = 1200, Height = 760, Content = view, RequestedThemeVariant = variant,
                Background = new SolidColorBrush(Color.Parse(theme == "dark" ? "#141414" : "#F5F5F5")),
            };
            win.Show();
            vm.IsActive = true;
            Pump(1500);

            var tiles = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible)
                .OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
            var cols = vm.GridColumns;
            var away = new Point(600, 755);

            void Shot(string name, Button around, int ms)
            {
                Pump(ms);
                var o = around.TranslatePoint(default, win)!.Value;
                var full = Path.Combine(dir, $"_{prefix}-{theme}-{name}-full.png");
                win.CaptureRenderedFrame()!.Save(full);
                var path = Path.Combine(dir, $"{prefix}-{theme}-{name}.png");
                Crop(full, path, new SKRectI((int)o.X - 60, (int)o.Y - 50,
                    (int)(o.X + around.Bounds.Width) + 60, (int)(o.Y + around.Bounds.Height) + 30));
                File.Delete(full);
                _o.WriteLine(path);
            }
            void Hover(Button t) => win.MouseMove(t.TranslatePoint(new Point(t.Bounds.Width / 2, 40), win)!.Value);

            var edge = tiles[cols];      // left column, second row
            Hover(edge); Shot("edge-hover1-early", edge, 60);
            Shot("edge-hover1", edge, 500);
            win.MouseMove(away); Pump(500);
            Hover(edge); Shot("edge-hover2", edge, 500);
            win.MouseMove(away); Pump(500);

            var mid = tiles[cols + 2];
            Hover(mid); Shot("middle-hover", mid, 500);
            win.MouseMove(away); Pump(400);

            var p = mid.TranslatePoint(new Point(mid.Bounds.Width / 2, 40), win)!.Value;
            win.MouseDown(p, MouseButton.Left, TestKeys.Primary);
            win.MouseUp(p, MouseButton.Left, TestKeys.Primary);
            win.MouseMove(p); Shot("selected-hover", mid, 600);
            win.MouseMove(away); Shot("selected", mid, 600);
            win.Close();
        });
    }
}
