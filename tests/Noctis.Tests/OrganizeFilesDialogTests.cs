using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Track = Noctis.Models.Track;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Organize Files in the rounded pill pop-up of the Metadata editor / Find
/// Metadata (blurred app behind, filled pill fields, solid accent Apply, quiet Close), with
/// fewer words: tag chips instead of the token list, a live example path under the pattern.
/// </summary>
public class OrganizeFilesDialogTests
{
    private readonly ITestOutputHelper _o;
    public OrganizeFilesDialogTests(ITestOutputHelper o) => _o = o;

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

    private static Track[] Tracks() => Enumerable.Range(1, 3)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        })
        .ToArray();

    [AvaloniaFact]
    public void OrganizeFiles_OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            using var persistence = new TestPersistenceService();
            var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
            var root = TestPaths.Primary("Organized");
            settings.OrganizeTargetRoot = root;
            var vm = new OrganizeFilesViewModel(Tracks(), new PlannerOrganizer(), settings);
            Assert.True(PumpUntil(() => vm.Rows.Count == 3), "preview never filled");

            var win = new OrganizeFilesDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
                    "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                // The old outlined / text-button chrome is gone.
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("text-btn") || b.Classes.Contains("accent-btn")
                                                                 || b.Classes.Contains("pill-outline") || b.Classes.Contains("dialog-close"));

                // Filled pill fields, resolved from PillDialog.axaml (pattern + destination).
                var fields = all.OfType<TextBox>().Where(t => t.Classes.Contains("pill-field")).ToList();
                Assert.Equal(2, fields.Count);
                foreach (var field in fields)
                {
                    var chrome = field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                    Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                    Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                }

                // Footer: Apply is the solid accent pill, Close the quiet one; both bound.
                var apply = all.OfType<Button>().Single(b => b.Command == vm.ApplyCommand);
                PillDialogHostTests.AssertSolidAccent(apply);
                Assert.True(apply.IsEffectivelyEnabled, "3 files to move, yet Apply is disabled");
                var close = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", close.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(close.Background));
                Assert.Contains("pill-secondary", all.OfType<Button>().Single(b => b.Command == vm.PreviewCommand).Classes);
                var undo = all.OfType<Button>().Single(b => b.Command == vm.UndoLastCommand);
                Assert.False(undo.IsEffectivelyEnabled, "Undo enabled with nothing to undo");

                // The preview sits in a filled well and its rows are realized.
                var list = all.OfType<ItemsControl>().Single(ic => ReferenceEquals(ic.ItemsSource, vm.Rows));
                Assert.Contains(list.GetVisualAncestors().OfType<Border>(), b => b.Classes.Contains("pill-well"));
                Assert.Equal(3, list.GetRealizedContainers().Count());

                // Localized footer count.
                Assert.Equal(Loc.T("OrganizeFiles.Summary", 3, 0), vm.StatusMessage);

                // Live example follows the pattern, before any Update preview.
                Assert.Equal(Path.Combine("Bad Bunny", "Album", "01 Song 1") + ".flac", vm.SampleTarget);
                vm.Pattern = "{Artist}/{Title}";
                Assert.Equal(Path.Combine("Bad Bunny", "Song 1") + ".flac", vm.SampleTarget);
                PumpUntil(() => false, 50);
                var sample = win.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.SampleTarget);
                Assert.True(sample.IsEffectivelyVisible);
                _o.WriteLine($"sample: {sample.Text}");

                // A tag chip adds its token (appended until the pattern has had the caret).
                var year = win.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.DataContext is OrganizeFilesViewModel.PatternToken { Label: "Year" });
                Assert.Contains("pill-secondary", year.Classes);
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // Spaced from the tag before it, so the values don't glue ("Song 12024").
                Assert.Equal("{Artist}/{Title} {Year}", vm.Pattern);
                // After a separator it goes in as is.
                vm.Pattern = "{Artist}/";
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("{Artist}/{Year}", vm.Pattern);

                // Esc closes like Close: animated, exactly once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Owner 10-27: the Preview list's scrollbar ran into the well's rounded corners
    /// (thumb half outside the top-right curve at the top of the list, into the bottom-right
    /// one at the end). The well (Border.pill-well, radius 20) clips to its rounded shape and
    /// the bar spans the ScrollViewer's full height at its right edge, so its ends sat in the
    /// corner squares. Pins that the track and thumb, and every visible row, stay inside the
    /// rounded shape at the top and at the bottom of the list.</summary>
    [AvaloniaFact]
    public void Preview_ScrollbarAndRowsStayInsideTheRoundedWell()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win) = OpenScrolling();
            try
            {
                var bad = new List<string>();
                foreach (var bottom in new[] { false, true })
                    bad.AddRange(CheckPreview(win, vm, bottom));
                foreach (var b in bad) _o.WriteLine(b);
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): the dialog with the Preview list at its
    /// top and at its bottom, plus a 3x zoom of the well's right-hand corners, saved to
    /// NOCTIS_SHOTS_DIR (default: the system temp folder) as organize-&lt;NOCTIS_SHOTS_TAG&gt;-*.png.</summary>
    [AvaloniaFact]
    public void Probe_SavesThePreviewScrollbarAtTopAndBottom()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Environment.GetEnvironmentVariable("NOCTIS_SHOTS_DIR") is { Length: > 0 } d ? d : Path.GetTempPath();
        var tag = Environment.GetEnvironmentVariable("NOCTIS_SHOTS_TAG") is { Length: > 0 } t ? t : "current";
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win) = OpenScrolling();
            try
            {
                foreach (var bottom in new[] { false, true })
                {
                    var (well, sv) = PreviewSurface(win, vm);
                    ScrollTo(sv, bottom);
                    var full = Path.Combine(dir, $"organize-{tag}-{(bottom ? "bottom" : "top")}.png");
                    win.CaptureRenderedFrame()!.Save(full, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    // 3x zoom of the well's top-right and bottom-right 90x90 corners, side by side.
                    var r = RectIn(well, win);
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
                    File.WriteAllBytes(Path.ChangeExtension(full, null) + "-corners.png", data.ToArray());
                    _o.WriteLine(full);
                }
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>The dialog over 40 planned moves: more rows than the well shows.</summary>
    private static (OrganizeFilesViewModel vm, OrganizeFilesDialog win) OpenScrolling()
    {
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistory());
        settings.OrganizeTargetRoot = TestPaths.Primary("Organized");
        var tracks = Enumerable.Range(1, 40).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "nadie sabe lo que va a pasar mañana", TrackNumber = i,
            FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        }).ToArray();
        var vm = new OrganizeFilesViewModel(tracks, new PlannerOrganizer(), settings);
        Assert.True(PumpUntil(() => vm.Rows.Count == 40), "preview never filled");
        var win = new OrganizeFilesDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
        PumpUntil(() => false, 150);
        return (vm, win);
    }

    /// <summary>The Preview well (the rounded shape that clips) and the ScrollViewer in it.</summary>
    private static (Border well, ScrollViewer sv) PreviewSurface(Window win, OrganizeFilesViewModel vm)
    {
        var list = win.GetVisualDescendants().OfType<ItemsControl>().Single(ic => ReferenceEquals(ic.ItemsSource, vm.Rows));
        var sv = list.GetVisualAncestors().OfType<ScrollViewer>().First();
        var well = sv.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("pill-well"));
        return (well, sv);
    }

    private static void ScrollTo(ScrollViewer sv, bool bottom)
    {
        sv.Offset = new Vector(0, bottom ? Math.Max(0, sv.Extent.Height - sv.Viewport.Height) : 0);
        PumpUntil(() => false, 250);
    }

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

    /// <summary>Bar track + thumb, and every visible row clipped to the viewport, must stay
    /// inside the well's rounded shape.</summary>
    private List<string> CheckPreview(Window win, OrganizeFilesViewModel vm, bool bottom)
    {
        var (well, sv) = PreviewSurface(win, vm);
        ScrollTo(sv, bottom);
        var where = bottom ? "bottom" : "top";
        var bar = sv.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.TemplatedParent == sv && b.Orientation == Orientation.Vertical);
        Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Height > 0,
            $"{where}: no vertical scrollbar (extent {sv.Extent}, viewport {sv.Viewport})");
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
        var bad = new List<string>();
        void Inside(string what, Rect r)
        {
            foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft })
                if (!InRounded(p, well.Bounds.Size, well.CornerRadius))
                    bad.Add($"{where} {what} corner {p} lies outside the {well.Bounds.Size} well's {well.CornerRadius} rounded shape");
        }
        var barRect = RectIn(bar, well);
        var thumbRect = RectIn(thumb, well);
        _o.WriteLine($"{where}: well {well.Bounds.Size} r={well.CornerRadius}  track {barRect}  thumb {thumbRect}");
        Inside("track", barRect);
        Inside("thumb", thumbRect);
        var presenter = sv.GetVisualDescendants().OfType<ScrollContentPresenter>().First(p => p.TemplatedParent == sv);
        var clip = RectIn(presenter, well);
        foreach (var row in sv.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("of-row") && b.IsEffectivelyVisible))
        {
            var seen = RectIn(row, well).Intersect(clip);
            if (seen.Width <= 0 || seen.Height <= 0) continue;
            Inside("row", seen);
        }
        return bad;
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): a PNG of the dialog over a busy owner,
    /// for eyeballing the layout. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheOrganizeDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            using var persistence = new TestPersistenceService();
            var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
            settings.OrganizeTargetRoot = TestPaths.Primary("Organized");
            var tracks = Enumerable.Range(1, 30).Select(i => new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
                Album = "nadie sabe lo que va a pasar mañana", TrackNumber = i,
                FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
            }).ToArray();
            var vm = new OrganizeFilesViewModel(tracks, new PlannerOrganizer(), settings);
            Assert.True(PumpUntil(() => vm.Rows.Count == 30));
            var win = new OrganizeFilesDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            _ = win.ShowDialog(owner);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                var path = Path.Combine(Path.GetTempPath(), "noctis-organize-files-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>The real planner with no files on disk; nothing is ever moved.</summary>
    private sealed class PlannerOrganizer : IFileOrganizerService
    {
        public IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, string pattern, string targetRoot)
            => FileOrganizePlanner.Plan(tracks, pattern, targetRoot, _ => false);
        public Task<OrganizeResult> ApplyAsync(IReadOnlyList<OrganizeMove> moves, CancellationToken ct = default)
            => throw new NotSupportedException();
        public bool CanUndo => false;
        public Task<OrganizeResult> UndoLastAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
