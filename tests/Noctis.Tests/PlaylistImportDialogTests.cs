using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Noctis.Controls;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Import Playlist as the rounded pill pop-up (blurred backdrop host, pill
/// fields/buttons), one simple flow: link field + Choose file, rows with a Matched / Missing
/// chip, status left, Cancel + Create playlist right.
/// </summary>
public class PlaylistImportDialogTests
{
    private readonly ITestOutputHelper _o;
    public PlaylistImportDialogTests(ITestOutputHelper o) => _o = o;

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

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static (PlaylistImportViewModel vm, PlaylistImportDialog win, PillDialogHost host) Open(FakeImportService service)
    {
        var vm = new PlaylistImportViewModel(service, new FakeTidal());
        var win = new PlaylistImportDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host);
    }

    [AvaloniaFact]
    public void Dialog_OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open(new FakeImportService());
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                var all = win.GetVisualDescendants().ToList();

                // Link field: the filled pill (PillDialog.axaml resolved), bound to LinkText.
                var link = all.OfType<TextBox>().Single(b => b.Name == "LinkBox");
                Assert.Contains("pill-field", link.Classes);
                var chrome = link.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                link.Text = "not a link";
                Assert.Equal("not a link", vm.LinkText);

                // Beside it: the round accent import button and the quiet Choose file pill.
                var go = all.OfType<Button>().Single(b => b.Command == vm.ImportLinkCommand);
                Assert.Contains("pill-primary", go.Classes);
                Assert.False(go.IsEnabled);
                var choose = all.OfType<Button>().Single(b => b.Name == "ChooseFileButton");
                Assert.Contains("pill-secondary", choose.Classes);
                Assert.True(choose.Bounds.Width > 40 && choose.IsEffectivelyVisible);

                // Start state: the drop panel, no list, no name field, no help.
                Assert.True(all.OfType<Border>().Single(b => b.Name == "DropZone").IsEffectivelyVisible);
                // (Hidden, its ScrollViewer has no template yet, so it is found by name, not in the visual tree.)
                var rowsScroller = Assert.IsType<ScrollViewer>(win.FindControl<ItemsControl>("RowsList")!.Parent);
                Assert.False(rowsScroller.IsVisible);

                // Footer: Cancel (quiet) and Create playlist (solid accent, off until a match).
                var create = all.OfType<Button>().Single(b => b.Command == vm.CreateCommand);
                PillDialogHostTests.AssertSolidAccent(create);
                Assert.False(create.IsEnabled);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
                Assert.Equal("Cancel", cancel.Content);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public async Task LoadedFile_ShowsRowsWithChips_StatusAndEnablesCreate()
    {
        EnsureAppStyles();
        var service = new FakeImportService();
        var (vm, win, host) = Open(service);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            await vm.LoadFileAsync("C:/x.csv");
            PumpUntil(() => false, 100);

            Assert.Equal("2 of 3 matched", vm.StatusMessage);
            Assert.True(vm.CanCreate);
            Assert.False(vm.ShowStart);
            Assert.Equal("Road Trip", vm.PlaylistName);
            // Missing first, then the matched ones; "Artist – Title" split for the two lines.
            Assert.Equal(new[] { "Stairway", "Hello", "Yellow" }, vm.Rows.Select(r => r.Title));
            Assert.Equal("Led Zeppelin", vm.Rows[0].Artist);
            Assert.False(vm.Rows[0].IsMatched);

            var all = win.GetVisualDescendants().ToList();
            Assert.False(all.OfType<Border>().Single(b => b.Name == "DropZone").IsEffectivelyVisible);
            var chips = all.OfType<Border>().Where(b => b.Classes.Contains("pi-chip") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(3, chips.Count);
            Assert.Single(chips, c => c.Classes.Contains("missing"));
            Assert.True(all.OfType<Button>().Single(b => b.Command == vm.CreateCommand).IsEnabled);

            await vm.CreateCommand.ExecuteAsync(null);
            Assert.Equal("Road Trip", service.CreatedName);
            Assert.Equal(2, service.CreatedIds!.Count);
            Assert.Equal("Created “Road Trip” with 2 tracks.", vm.StatusMessage);
            Assert.Equal("Done", vm.CancelLabel);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Escape_ClosesAnimated_ExactlyOnce()
    {
        EnsureAppStyles();
        var (_, win, host) = Open(new FakeImportService());
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    /// <summary>Real Skia only: PNGs of the start and loaded states for a visual check.</summary>
    [AvaloniaFact]
    public void Probe_SavesStartAndLoadedStates()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        const string dir = @"D:\NoctisLyricsLab\pill-dialog\shots";
        System.IO.Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open(new FakeImportService());
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)));
                PumpUntil(() => false, 150);
                win.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "import-01-start.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.LinkText = "https://open.spotify.com/playlist/abc";
                PumpUntil(() => false, 150);
                win.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "import-02-help.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.LinkText = string.Empty;
                // The fake completes synchronously, so this does not block the UI thread.
                vm.LoadFileAsync("C:/x.csv").GetAwaiter().GetResult();
                PumpUntil(() => false, 300);
                win.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "import-03-loaded.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    /// <summary>
    /// Owner 10-27: "make the arrow center middle inside of the circle button". Real Skia
    /// only: measures what the round link button actually draws. The accent fill's drawn
    /// extent gives the circle's centre; the chevron's ink (pixels brighter than the fill)
    /// gives the arrow's. The arrow's ink box must sit within 1px of the circle centre on
    /// both axes, and the button must really be a circle (as tall as it is wide).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void LinkButton_ChevronIsCentredInTheCircle(double scale)
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (_, win, host) = Open(new FakeImportService());
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)));
                if (scale != 1.0)
                {
                    // The owner's display is 125%; the headless window takes a scale like a monitor change.
                    var impl = win.PlatformImpl!;
                    impl.GetType().GetProperty("RenderScaling")!.SetValue(impl, scale);
                    ((Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl))?.Invoke(scale);
                }
                PumpUntil(() => false, 150);
                Assert.Equal(scale, win.RenderScaling);
                var button = win.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImportLinkButton");
                var origin = button.TranslatePoint(new Point(0, 0), win)!.Value;
                var frame = win.CaptureRenderedFrame()!;
                var shotDir = Environment.GetEnvironmentVariable("NOCTIS_IMPORT_ARROW_SHOTS");
                var m = MeasureChevron(frame, new PixelRect((int)Math.Floor(origin.X * scale) - 4, (int)Math.Floor(origin.Y * scale) - 4,
                    (int)Math.Ceiling(button.Bounds.Width * scale) + 8, (int)Math.Ceiling(button.Bounds.Height * scale) + 8), shotDir, scale);
                _o.WriteLine($"scale {scale}: button {button.Bounds.Width}x{button.Bounds.Height}; fill box {m.Fill}; ink box {m.Ink}; " +
                             $"circle centre ({m.Cx:0.00},{m.Cy:0.00}); ink-box centre ({m.Ix:0.00},{m.Iy:0.00}); " +
                             $"ink centroid ({m.Gx:0.00},{m.Gy:0.00}); box offset ({m.Ix - m.Cx:+0.00;-0.00},{m.Iy - m.Cy:+0.00;-0.00})");
                Assert.Equal(button.Bounds.Width, button.Bounds.Height);
                Assert.InRange(Math.Abs(m.Fill.Width - m.Fill.Height), 0, 1);
                Assert.InRange(Math.Abs(m.Ix - m.Cx), 0, 1.0);
                Assert.InRange(Math.Abs(m.Iy - m.Cy), 0, 1.0);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    private readonly record struct ChevronMeasure(PixelRect Fill, PixelRect Ink, double Cx, double Cy, double Ix, double Iy, double Gx, double Gy);

    // BGRA frame. Fill = saturated pixels (the accent disc on the grey card); ink = pixels whose
    // weakest channel rises well above the fill's (the light chevron), inside the disc only.
    private static ChevronMeasure MeasureChevron(Avalonia.Media.Imaging.Bitmap frame, PixelRect r, string? shotDir, double scale)
    {
        int fw = frame.PixelSize.Width, fh = frame.PixelSize.Height;
        var px = new byte[fw * fh * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(frame.PixelSize), handle.AddrOfPinnedObject(), px.Length, fw * 4); }
        finally { handle.Free(); }
        (int b, int g, int rr) At(int x, int y) { int i = (y * fw + x) * 4; return (px[i], px[i + 1], px[i + 2]); }

        if (!string.IsNullOrEmpty(shotDir))
        {
            System.IO.Directory.CreateDirectory(shotDir);
            using var crop = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var fb = crop.Lock())
                for (var y = 0; y < r.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(px, ((r.Y + y) * fw + r.X) * 4, fb.Address + y * fb.RowBytes, r.Width * 4);
            crop.Save(System.IO.Path.Combine(shotDir, $"link-button-{scale * 100:0}pct.png"));
        }
        int fx0 = int.MaxValue, fy0 = int.MaxValue, fx1 = int.MinValue, fy1 = int.MinValue;
        for (var y = r.Y; y < r.Bottom; y++)
        for (var x = r.X; x < r.Right; x++)
        {
            var (b, g, red) = At(x, y);
            if (Math.Max(red, Math.Max(g, b)) - Math.Min(red, Math.Min(g, b)) < 30) continue;
            fx0 = Math.Min(fx0, x); fy0 = Math.Min(fy0, y); fx1 = Math.Max(fx1, x); fy1 = Math.Max(fy1, y);
        }
        Assert.True(fx0 != int.MaxValue, $"no accent fill found in {r} of a {frame.PixelSize} frame; centre pixel {At(r.X + r.Width / 2, r.Y + r.Height / 2)}");
        double cx = (fx0 + fx1 + 1) / 2.0, cy = (fy0 + fy1 + 1) / 2.0, rad = (fx1 - fx0 + 1) / 2.0;
        // Fill tone: median green just inside the rim, away from the chevron.
        var rim = new List<int>();
        for (var a = 0; a < 360; a += 10)
        {
            var t = a * Math.PI / 180;
            var (rb, rg, rr) = At((int)(cx + Math.Cos(t) * (rad - 5)), (int)(cy + Math.Sin(t) * (rad - 5)));
            rim.Add(Math.Min(rb, Math.Min(rg, rr)));
        }
        rim.Sort();
        var fillMin = rim[rim.Count / 2];

        int ix0 = int.MaxValue, iy0 = int.MaxValue, ix1 = int.MinValue, iy1 = int.MinValue;
        double sw = 0, sx = 0, sy = 0;
        for (var y = fy0; y <= fy1; y++)
        for (var x = fx0; x <= fx1; x++)
        {
            var dx = x + 0.5 - cx; var dy = y + 0.5 - cy;
            if (dx * dx + dy * dy > (rad - 3) * (rad - 3)) continue;
            var (pb, pg, pr) = At(x, y);
            var w = Math.Min(pb, Math.Min(pg, pr)) - fillMin;
            if (w < 12) continue;
            sw += w; sx += w * (x + 0.5); sy += w * (y + 0.5);
            if (w < 40) continue;
            ix0 = Math.Min(ix0, x); iy0 = Math.Min(iy0, y); ix1 = Math.Max(ix1, x); iy1 = Math.Max(iy1, y);
        }
        return new ChevronMeasure(
            new PixelRect(fx0, fy0, fx1 - fx0 + 1, fy1 - fy0 + 1),
            new PixelRect(ix0, iy0, ix1 - ix0 + 1, iy1 - iy0 + 1),
            cx, cy, (ix0 + ix1 + 1) / 2.0, (iy0 + iy1 + 1) / 2.0, sx / sw, sy / sw);
    }

    [Fact]
    public void ImportRow_SplitsArtistAndTitle()
    {
        var r = PlaylistImportViewModel.ImportRow.From("Adele – Hello", matched: true);
        Assert.Equal(("Hello", "Adele", true), (r.Title, r.Artist, r.IsMatched));
        var bare = PlaylistImportViewModel.ImportRow.From("Untitled", matched: false);
        Assert.Equal(("Untitled", "", false), (bare.Title, bare.Artist, bare.HasArtist));
    }

    private sealed class FakeImportService : IPlaylistImportService
    {
        public string? CreatedName;
        public IReadOnlyList<Guid>? CreatedIds;

        private static PlaylistImportPreview Preview() => new()
        {
            SuggestedName = "Road Trip",
            MatchedTrackIds = new[] { Guid.NewGuid(), Guid.NewGuid() },
            MatchedLabels = new[] { "Adele – Hello", "Coldplay – Yellow" },
            MissingLabels = new[] { "Led Zeppelin – Stairway" },
        };

        public Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default) => Task.FromResult(Preview());
        public Task<PlaylistImportPreview> AnalyzeLinkAsync(string url, CancellationToken ct = default) => Task.FromResult(Preview());
        public Task<Guid> CreateAsync(string name, IReadOnlyList<Guid> matchedTrackIds, CancellationToken ct = default)
        {
            CreatedName = name;
            CreatedIds = matchedTrackIds;
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class FakeTidal : ITidalAuthService
    {
        public bool IsConnected => false;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> LoginAsync(CancellationToken ct = default) => Task.FromResult(false);
        public void Disconnect() { }
    }
}
