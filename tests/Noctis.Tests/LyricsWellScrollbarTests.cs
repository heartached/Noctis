using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Track = Noctis.Models.Track;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-17: in the metadata editor's lyrics pages the vertical scrollbar ran into the
/// rounded panel's corners and was cut off by the curve (thumb top in Timestamp Lyrics,
/// thumb bottom in Synced Lyrics), and Timestamp Lyrics' last visible row was sliced by the
/// panel's bottom edge. The panel (Border.pill-well, radius 20) clips its children to its
/// rounded shape, so anything that reaches into a corner square is cut. These pin that the
/// bar's track and thumb, at the top and at the bottom of the list, sit inside the rounded
/// shape, and that list rows are never clipped by a corner curve.
/// </summary>
public class LyricsWellScrollbarTests
{
    private readonly ITestOutputHelper _o;
    public LyricsWellScrollbarTests(ITestOutputHelper o) => _o = o;

    private const int LineCount = 46;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static string Lrc()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < LineCount; i++)
            sb.Append('[').Append((i * 4) / 60 % 100).Append(':').Append(((i * 4) % 60).ToString("00")).Append(".17]")
              .Append("She said she leavin' her boyfriend, line ").Append(i + 1).Append('\n');
        return sb.ToString();
    }

    private static string Plain()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++) sb.Append("She said she leavin' her boyfriend, line ").Append(i + 1).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private (MetadataViewModel vm, MetadataWindow win) Open()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Boyfriend", Artist = "A", Album = "X", AlbumId = Guid.NewGuid(),
            FilePath = Path.Combine(Path.GetTempPath(), "NoctisTests", "well-scrollbar.flac"),
            SyncedLyrics = Lrc(), Lyrics = Plain(),
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new NullTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: false, albumTracks: null);
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1000, Height = 760 };
        win.Show();
        var load = vm.InitializeAsync();
        Assert.True(PumpUntil(() => load.IsCompleted));
        return (vm, win);
    }

    private static void SelectTab(Window win, string header)
    {
        var tabs = win.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, header));
        PumpUntil(() => false, 300);
    }

    /// <summary>The page's lyric surface: the rounded shape that clips, and the ScrollViewer in it.</summary>
    private sealed record Surface(Border Shape, ScrollViewer Viewer);

    private static IEnumerable<T> Live<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible && v.Bounds.Width > 0);

    private static Surface TimestampSurface(Window win)
    {
        var well = Live<Border>(win).Single(b => b.Classes.Contains("pill-well")
            && b.GetVisualDescendants().OfType<Border>().Any(r => r.Classes.Contains("synced-row")));
        var sv = Live<ScrollViewer>(well).First(s => s.TemplatedParent is null);
        return new Surface(well, sv);
    }

    private static Surface SyncedFormattedSurface(Window win)
    {
        var card = Live<Border>(win).Single(b => b.Name == "SyncedPreviewCard");
        var sv = Live<ScrollViewer>(card).First(s => s.TemplatedParent is null);
        return new Surface(card, sv);
    }

    private static Surface SyncedRawSurface(Window win)
    {
        var card = Live<Border>(win).Single(b => b.Name == "SyncedPreviewCard");
        var box = Live<TextBox>(card).Single();
        var sv = Live<ScrollViewer>(box).First(s => s.TemplatedParent == box);
        return new Surface(card, sv);
    }

    private static ScrollBar VBar(ScrollViewer sv) =>
        sv.GetVisualDescendants().OfType<ScrollBar>()
          .Single(b => b.TemplatedParent == sv && b.Orientation == Orientation.Vertical);

    /// <summary>Axis-aligned bounds of <paramref name="v"/> (render transform included) in
    /// <paramref name="to"/>'s coordinates.</summary>
    private static Rect RectIn(Visual v, Visual to)
    {
        var m = v.TransformToVisual(to) ?? throw new InvalidOperationException("not in the same tree");
        return new Rect(v.Bounds.Size).TransformToAABB(m);
    }

    /// <summary>True when the point lies in the rounded rect (0,0,size) with corners <paramref name="cr"/>.</summary>
    private static bool InRounded(Point p, Size size, CornerRadius cr, double eps = 0.5)
    {
        if (p.X < -eps || p.Y < -eps || p.X > size.Width + eps || p.Y > size.Height + eps) return false;
        bool Corner(double r, double cx, double cy, bool left, bool top)
        {
            if (r <= 0) return true;
            var inX = left ? p.X < cx : p.X > cx;
            var inY = top ? p.Y < cy : p.Y > cy;
            if (!inX || !inY) return true;
            var dx = p.X - cx; var dy = p.Y - cy;
            return Math.Sqrt(dx * dx + dy * dy) <= r + eps;
        }
        return Corner(cr.TopLeft, cr.TopLeft, cr.TopLeft, true, true)
            && Corner(cr.TopRight, size.Width - cr.TopRight, cr.TopRight, false, true)
            && Corner(cr.BottomRight, size.Width - cr.BottomRight, size.Height - cr.BottomRight, false, false)
            && Corner(cr.BottomLeft, cr.BottomLeft, size.Height - cr.BottomLeft, true, false);
    }

    private static List<string> Outside(string what, Rect r, Border shape)
    {
        var bad = new List<string>();
        foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft })
            if (!InRounded(p, shape.Bounds.Size, shape.CornerRadius))
                bad.Add($"{what} corner {p} lies outside the {shape.Bounds.Size} panel's {shape.CornerRadius} rounded shape");
        return bad;
    }

    private static void ScrollTo(ScrollViewer sv, bool bottom)
    {
        sv.Offset = new Vector(0, bottom ? Math.Max(0, sv.Extent.Height - sv.Viewport.Height) : 0);
        PumpUntil(() => false, 250);
    }

    /// <summary>Bar track + thumb, and every realized row clipped to the viewport, must stay
    /// inside the panel's rounded shape.</summary>
    private List<string> Check(string page, Surface s, bool bottom, string? rowClass)
    {
        ScrollTo(s.Viewer, bottom);
        var where = $"{page} ({(bottom ? "bottom" : "top")})";
        var bar = VBar(s.Viewer);
        Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Height > 0,
            $"{where}: no vertical scrollbar - the list does not overflow (extent {s.Viewer.Extent}, viewport {s.Viewer.Viewport})");
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
        var barRect = RectIn(bar, s.Shape);
        var thumbRect = RectIn(thumb, s.Shape);
        _o.WriteLine($"{where}: panel {s.Shape.Bounds.Size} r={s.Shape.CornerRadius}  track {barRect}  thumb {thumbRect}");

        var bad = new List<string>();
        bad.AddRange(Outside($"{where} track", barRect, s.Shape));
        bad.AddRange(Outside($"{where} thumb", thumbRect, s.Shape));

        if (rowClass != null)
        {
            var presenter = s.Viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().First(p => p.TemplatedParent == s.Viewer);
            var clip = RectIn(presenter, s.Shape);
            foreach (var row in Live<Border>(s.Viewer).Where(b => b.Classes.Contains(rowClass)))
            {
                var seen = RectIn(row, s.Shape).Intersect(clip);
                if (seen.Width <= 0 || seen.Height <= 0) continue;
                bad.AddRange(Outside($"{where} row", seen, s.Shape));
            }
        }
        return bad;
    }

    [AvaloniaFact]
    public void LyricsPages_ScrollbarAndRowsStayInsideTheRoundedPanel()
    {
        EnsureAppStyles();
        var (vm, win) = Open();
        try
        {
            Assert.Equal(LineCount, vm.SyncedLyricLines.Count);
            var bad = new List<string>();

            SelectTab(win, "Timestamp Lyrics");
            foreach (var bottom in new[] { false, true })
                bad.AddRange(Check("Timestamp Lyrics", TimestampSurface(win), bottom, "synced-row"));

            SelectTab(win, "Synced Lyrics");
            foreach (var bottom in new[] { false, true })
                bad.AddRange(Check("Synced Lyrics", SyncedFormattedSurface(win), bottom, null));

            Live<ToggleButton>(win).Single(t => t.Name == "SyncedRawToggle").IsChecked = true;
            PumpUntil(() => false, 300);
            foreach (var bottom in new[] { false, true })
                bad.AddRange(Check("Synced Lyrics (Raw)", SyncedRawSurface(win), bottom, null));

            // Plain Lyrics no longer scrolls inside its box: the page scrolls like Details
            // (owner 10-27), pinned by MetadataWindowLyricsScrollTests.

            foreach (var b in bad) _o.WriteLine(b);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Under real Skia (NOCTIS_TEST_SKIA=1) saves each lyrics page at the top and the
    /// bottom of its list, plus a 3x zoom of the panel's right-hand corners, to
    /// artifacts/scrollbar/&lt;NOCTIS_SHOTS_TAG or "current"&gt;/ for review.</summary>
    [AvaloniaFact]
    public void Probe_SavesLyricsPageShots()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var tag = Environment.GetEnvironmentVariable("NOCTIS_SHOTS_TAG") is { Length: > 0 } t ? t : "current";
        var dir = Path.Combine(RepoRoot(), "artifacts", "scrollbar", tag);
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (_, win) = Open();
            try
            {
                void Shots(string name, Func<Surface> surface)
                {
                    foreach (var bottom in new[] { false, true })
                    {
                        var s = surface();
                        ScrollTo(s.Viewer, bottom);
                        Save(win, s.Shape, $"{name}-{(bottom ? "bottom" : "top")}");
                    }
                }
                SelectTab(win, "Timestamp Lyrics");
                Shots("timestamp", () => TimestampSurface(win));
                SelectTab(win, "Synced Lyrics");
                Shots("synced", () => SyncedFormattedSurface(win));
                Live<ToggleButton>(win).Single(x => x.Name == "SyncedRawToggle").IsChecked = true;
                PumpUntil(() => false, 300);
                Shots("synced-raw", () => SyncedRawSurface(win));
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });

        void Save(Window win, Border shape, string name)
        {
            var full = Path.Combine(dir, name + ".png");
            win.CaptureRenderedFrame()!.Save(full, PngBitmapEncoderOptions.Default);
            // 3x zoom of the panel's top-right and bottom-right 90x90 corners, side by side.
            var r = RectIn(shape, win);
            using var src = SKBitmap.Decode(full);
            const int c = 90, z = 3;
            using var outBmp = new SKBitmap(c * z * 2 + 12, c * z);
            using (var canvas = new SKCanvas(outBmp))
            {
                canvas.Clear(new SKColor(255, 0, 255));
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
                var x0 = (int)Math.Round(r.Right) - c;
                canvas.DrawBitmap(src, new SKRect(x0, (int)r.Top, x0 + c, (int)r.Top + c), new SKRect(0, 0, c * z, c * z), paint);
                canvas.DrawBitmap(src, new SKRect(x0, (int)r.Bottom - c, x0 + c, (int)r.Bottom), new SKRect(c * z + 12, 0, c * z * 2 + 12, c * z), paint);
            }
            using var img = SKImage.FromBitmap(outBmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, name + "-corners.png"), data.ToArray());
            _o.WriteLine(full);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "Noctis.sln"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private sealed class NullTags : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
