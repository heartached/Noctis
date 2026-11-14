using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: "when i hover over the rounded circle buttons, it clips inside". The round
/// check (CheckBox.metadata-pill-checkbox, Styles.axaml) grows to scale(1.06) on hover, but
/// Fluent's CheckBox theme clips to bounds: a row tick with no label is only as tall as its
/// 22 px circle, so the grown ring lost its top and bottom (Scan ReplayGain's rows, Find
/// Metadata's, Fetch Lyrics'...). With NOCTIS_TEST_SKIA=1 the ring's pixels are measured on
/// all four sides and zoomed PNGs are saved (NOCTIS_RENDER_OUT or D:\NoctisWT\art-1114\checkhover).
/// </summary>
public class RoundCheckHoverClipTests
{
    private readonly ITestOutputHelper _o;
    public RoundCheckHoverClipTests(ITestOutputHelper o) => _o = o;

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

    private static List<Track> Tracks(int n) => Enumerable.Range(1, n)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "X 100PRE", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"rgc{i}.flac"),
        })
        .ToList();

    private sealed class NoScanner : IReplayGainScannerService
    {
        public bool IsAvailable => true;
        public System.Threading.Tasks.Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new ScanSummary());
    }

    private static Border Indicator(CheckBox c) =>
        c.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "IndicatorBorder");

    private static (ReplayGainScannerDialog win, List<CheckBox> checks) Mount()
    {
        EnsureAppStyles();
        var vm = new ReplayGainScannerViewModel(Tracks(3), new NoScanner(), new FakeLibraryService(),
            path => new Track { FilePath = path, Title = path });
        var win = new ReplayGainScannerDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 900, Height = 820 };
        win.Show();
        Pump(900);
        var checks = win.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Classes.Contains("metadata-pill-checkbox")).ToList();
        return (win, checks);
    }

    [AvaloniaFact]
    public void TheRoundCheck_DoesNotClipItsHoverGrowth_EvenWhenOnlyAsTallAsTheCircle()
    {
        var (win, checks) = Mount();
        try
        {
            Assert.True(checks.Count >= 2, "the Album gain check and a row check");
            // The row tick has no label: the box is the circle's height, so a clip cuts the grown ring.
            var row = checks[1];
            Assert.Null(row.Content);
            Assert.Equal(Indicator(row).Bounds.Height, row.Bounds.Height, 1);
            Assert.All(checks, c => Assert.False(c.ClipToBounds, "the round check clips its own hover growth"));
            // Nothing between the circle and the check clips either.
            Assert.All(checks, c => Assert.DoesNotContain(Indicator(c).GetVisualAncestors().TakeWhile(a => a != c), a => a.ClipToBounds));
        }
        finally { win.Close(); }
    }

    /// <summary>Sum of the ring's (or disc's edge) brightness over the background along the centre row/column on
    /// each side (top, bottom, left, right): a clipped side loses its outer pixel.</summary>
    private static double[] RingSides(int[] px, int stride, Rect box, Color bg)
    {
        int cx = (int)Math.Floor(box.Center.X), cy = (int)Math.Floor(box.Center.Y), r = (int)Math.Ceiling(box.Width / 2) + 3;
        double Lum(int x, int y)
        {
            var v = px[y * stride + x];
            int b = v & 0xFF, g = (v >> 8) & 0xFF, rr = (v >> 16) & 0xFF;
            return Math.Max(0, (rr + g + b) / 3.0 - (bg.R + bg.G + bg.B) / 3.0);
        }
        double Sum(Func<int, (int x, int y)> at, int from, int to)
        {
            var s = 0.0;
            for (var i = from; i < to; i++) { var (x, y) = at(i); s += Lum(x, y); }
            return s;
        }
        // Centre pixel row/column pair averaged so an even-sized circle reads the same both ways.
        double Col(int from, int to) => (Sum(i => (cx, cy + i), from, to) + Sum(i => (cx - 1, cy + i), from, to)) / 2;
        double Row(int from, int to) => (Sum(i => (cx + i, cy), from, to) + Sum(i => (cx + i, cy - 1), from, to)) / 2;
        // Only the outer band (the tick mark stays within 5 px of the centre), so a filled disc reads too.
        return new[] { Col(-r, -7), Col(7, r), Row(-r, -7), Row(7, r) };
    }

    private static int[] Pixels(WriteableBitmap frame, out int stride, out int height)
    {
        using var fb = frame.Lock();
        stride = fb.RowBytes / 4;
        height = fb.Size.Height;
        var px = new int[stride * height];
        Marshal.Copy(fb.Address, px, 0, px.Length);
        return px;
    }

    private static void SaveZoom(int[] px, int stride, Rect region, string path, int zoom = 8)
    {
        int x0 = (int)region.X, y0 = (int)region.Y, w = (int)region.Width, h = (int)region.Height;
        var outBmp = new WriteableBitmap(new PixelSize(w * zoom, h * zoom), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var ob = outBmp.Lock())
        {
            var o = new int[w * zoom * h * zoom];
            for (var y = 0; y < h * zoom; y++)
                for (var x = 0; x < w * zoom; x++)
                    o[y * w * zoom + x] = px[(y0 + y / zoom) * stride + x0 + x / zoom] | unchecked((int)0xFF000000);
            Marshal.Copy(o, 0, ob.Address, o.Length);
        }
        outBmp.Save(path);
    }

    [AvaloniaFact]
    public void Probe_HoveredRoundCheck_KeepsTheWholeRing()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        var env = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        var outDir = string.IsNullOrWhiteSpace(env) ? @"D:\NoctisWT\art-1114\checkhover" : env;
        Directory.CreateDirectory(outDir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (win, checks) = Mount();
            try
            {
                var failures = new List<string>();
                foreach (var (check, name) in new[] { (checks[0], "albumgain"), (checks[1], "row") })
                    foreach (var ticked in new[] { false, true })
                    {
                        check.IsChecked = ticked;
                        win.MouseMove(new Point(1, 1));
                        Pump(300);
                        var ind = Indicator(check);
                        var box = new Rect(ind.TranslatePoint(new Point(0, 0), win)!.Value, ind.Bounds.Size);
                        var region = box.Inflate(8);
                        var tag = $"{name}-{(ticked ? "checked" : "unchecked")}";

                        var px = Pixels(win.CaptureRenderedFrame()!, out var stride, out _);
                        var bgPx = px[(int)(box.Top - 4) * stride + (int)(box.Right + 4)];
                        var bg = Color.FromRgb((byte)((bgPx >> 16) & 0xFF), (byte)((bgPx >> 8) & 0xFF), (byte)(bgPx & 0xFF));
                        var normal = RingSides(px, stride, box, bg);
                        SaveZoom(px, stride, region, Path.Combine(outDir, $"{tag}-normal.png"));

                        win.MouseMove(box.Center);
                        Pump(400);
                        Assert.True(check.IsPointerOver, $"{name}: pointer not over the check");
                        px = Pixels(win.CaptureRenderedFrame()!, out stride, out _);
                        var hover = RingSides(px, stride, box, bg);
                        SaveZoom(px, stride, region, Path.Combine(outDir, $"{tag}-hover.png"));
                        _o.WriteLine($"{tag} normal T/B/L/R={string.Join("/", normal.Select(v => v.ToString("0")))} hover={string.Join("/", hover.Select(v => v.ToString("0")))}");

                        // The grown ring is as complete as at rest, and the same on all four sides.
                        if (hover.Min() < normal.Min() * 0.95 || hover.Max() - hover.Min() > hover.Max() * 0.08)
                            failures.Add($"{tag}: hovered ring T/B/L/R {string.Join("/", hover.Select(v => v.ToString("0")))} vs at rest {string.Join("/", normal.Select(v => v.ToString("0")))}");
                    }
                Assert.True(failures.Count == 0, string.Join("\n", failures));
            }
            finally { win.Close(); }
        });
    }
}
