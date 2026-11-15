using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: "inside pop-ups that I have to scroll, sometimes the scroll can feel really
/// choppy — sometimes 30 fps, sometimes 60, sometimes very smooth, sometimes 10 fps."
///
/// Benchmark, explicit only: NOCTIS_PERF_HARNESS=1 with NOCTIS_TEST_SKIA=1 (real Skia, raster).
/// A full-window probe (<see cref="ReplayProbe"/>) on top counts the device pixels each frame
/// repaints; a probe under the card counts how much of the page behind is re-drawn.
/// GlassBackdropCache.UnreadableForTests stands in for the GPU backend, whose surface copy cannot
/// be read back. 11-14, 1400×900 at 100%, wheel notch = 50 px then 3 idle frames:
///
///   Pop-up card (PillDialogHost, list under a header), per notch:
///     solid card 0.47 Mpx / 1 frame; glass card before 1.34 Mpx / 2 frames (raster and GPU),
///     after 0.47 Mpx / 1 frame (raster and GPU).
///   Real Settings sheet over the app with something animating in it (the empty library's
///   spinner standing in for lyrics, a playing title or the progress bar):
///     Liquid Glass off (live blur): 10.1 Mpx per notch, and 2.52 Mpx EVERY idle frame — Avalonia
///       dirties the whole effect node for any change under a BlurEffect, so the whole window is
///       re-rendered and re-blurred for as long as anything moves (not changed here).
///     Liquid Glass on, GPU: before 4.38 Mpx per notch and 0.97 Mpx every idle frame (the whole
///       frosted card for as long as anything moves under the opaque scrim); after 0.50 / 0.00.
///   Synthetic card over a 48-tile page (Harness_ScrollOverEachBackdrop): the page behind the
///     card is re-drawn under the card's viewport on every scrolled frame whatever the backdrop
///     (Avalonia has no occlusion culling); software frame time roughly doubles with the live blur
///     or the frost.
/// </summary>
public class PopupScrollCostTests
{
    private readonly ITestOutputHelper _o;
    public PopupScrollCostTests(ITestOutputHelper o) => _o = o;

    private static bool Harness => Environment.GetEnvironmentVariable("NOCTIS_PERF_HARNESS") == "1";

    /// <summary>Counts how often its drawing is replayed by the compositor and how many device
    /// pixels each replay's clip covered (render thread; headless renders on the test thread).</summary>
    internal sealed class ReplayProbe : Control
    {
        public int Replays;
        public long Pixels;

        public ReplayProbe() => IsHitTestVisible = false;

        public override void Render(DrawingContext context) => context.Custom(new Op(new Rect(Bounds.Size), this));

        private sealed class Op : ICustomDrawOperation
        {
            private readonly ReplayProbe _owner;
            public Op(Rect bounds, ReplayProbe owner) { Bounds = bounds; _owner = owner; }
            public Rect Bounds { get; }
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => other is Op o && o.Bounds == Bounds && ReferenceEquals(o._owner, _owner);
            public void Dispose() { }
            public void Render(ImmediateDrawingContext context)
            {
                Interlocked.Increment(ref _owner.Replays);
                var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (lease is null) return;
                using var api = lease.Lease();
                var clip = api.SkCanvas.DeviceClipBounds;
                Interlocked.Add(ref _owner.Pixels, (long)clip.Width * clip.Height);
            }
        }
    }

    internal enum Backdrop { Dim, LiveBlur, SnapshotGlass }

    internal sealed class Scene
    {
        public required Window Window;
        public required Panel App;
        public required ReplayProbe AppProbe;
        public required ScrollViewer Scroll;
        public required TranslateTransform Lyric;
        public required ReplayProbe Top;
        public GlassPanel? Glass;
    }

    /// <summary>A page of 48 shadowed tiles (the Albums grid's cost shape) with a moving lyric
    /// line, and over it a backdrop and a 1000×720 card holding 160 scrollable rows.</summary>
    internal static Scene Build(Backdrop backdrop)
    {
        var tiles = new WrapPanel { Margin = new Thickness(20) };
        for (var i = 0; i < 48; i++)
        {
            tiles.Children.Add(new Border
            {
                Width = 150, Height = 190, Margin = new Thickness(10), CornerRadius = new CornerRadius(10),
                BoxShadow = BoxShadows.Parse("0 6 18 0 #70000000"),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromRgb((byte)(i * 37), 80, 160), 0), new GradientStop(Color.FromRgb(20, (byte)(i * 53), 90), 1) },
                },
                Child = new TextBlock { Text = $"Album {i}", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Foreground = Brushes.White },
            });
        }
        var lyric = new TranslateTransform();
        var lyricLine = new TextBlock
        {
            Text = "A lyric line that moves every frame", FontSize = 34, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(80, 400, 0, 0), RenderTransform = lyric,
        };
        var probe = new ReplayProbe();
        var app = new Panel { Background = new SolidColorBrush(Color.FromRgb(18, 16, 22)), Children = { probe, tiles, lyricLine } };

        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(24) };
        for (var i = 0; i < 160; i++)
        {
            rows.Children.Add(new Border
            {
                Height = 52, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = $"Setting row {i} — some descriptive text", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0), Foreground = Brushes.White },
            });
        }
        var scroll = new ScrollViewer { Content = rows };
        var card = new Border
        {
            Width = 1000, Height = 720, CornerRadius = new CornerRadius(22), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = backdrop == Backdrop.SnapshotGlass ? null : new SolidColorBrush(Color.FromRgb(30, 28, 36)),
            Child = scroll,
        };

        var root = new Panel { Children = { app } };
        GlassPanel? glass = null;
        switch (backdrop)
        {
            case Backdrop.Dim:
                root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)), IsHitTestVisible = false });
                break;
            case Backdrop.LiveBlur:
                app.Effect = new ImmutableBlurEffect(BlurredBackdrop.LiveRadiusFor(BackdropSnapshot.DefaultBlurRadius));
                root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), IsHitTestVisible = false });
                break;
            case Backdrop.SnapshotGlass:
                var bitmap = new WriteableBitmap(new PixelSize(350, 225), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bitmap.Lock())
                {
                    var px = new int[fb.RowBytes / 4 * 225];
                    for (var y = 0; y < 225; y++) for (var x = 0; x < 350; x++) px[y * fb.RowBytes / 4 + x] = unchecked((int)0xFF000000) | (x * 0x10101 ^ y * 0x305);
                    System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
                }
                root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false });
                root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), IsHitTestVisible = false });
                glass = new GlassPanel
                {
                    Width = 1000, Height = 720, CornerRadius = new CornerRadius(22), BlurRadius = 22, EdgeThickness = 0,
                    UseAppGlass = false, IsGlassActive = true, IsHitTestVisible = false,
                    Background = new SolidColorBrush(Color.FromRgb(30, 28, 36)), GlassTintOpacity = 0.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                root.Children.Add(glass);
                break;
        }
        root.Children.Add(card);
        var top = new ReplayProbe();
        root.Children.Add(top);
        var window = new Window { Width = 1400, Height = 900, Content = root, Background = Brushes.Black };
        window.Show();
        Frame();
        Frame();
        return new Scene { Window = window, App = app, AppProbe = probe, Scroll = scroll, Lyric = lyric, Top = top, Glass = glass };
    }

    internal static void Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal readonly record struct Result(int Frames, int Replays, double PagePixelsPerFrame, double DirtyPixelsPerFrame,
        double MeanMs, double MaxMs, double AllocKbPerFrame);

    /// <summary>Scrolls <paramref name="frames"/> frames of 40 px; the lyric line moves too when
    /// <paramref name="lyricsPlaying"/>.</summary>
    internal static Result ScrollFrames(Scene s, int frames, bool lyricsPlaying, bool scroll = true)
    {
        s.AppProbe.Replays = 0;
        s.AppProbe.Pixels = 0;
        s.Top.Pixels = 0;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var times = new List<double>();
        var y = s.Scroll.Offset.Y;
        for (var i = 0; i < frames; i++)
        {
            if (scroll)
            {
                y += 40;
                if (y > s.Scroll.Extent.Height - s.Scroll.Viewport.Height) y = 0;
                s.Scroll.Offset = new Vector(0, y);
            }
            if (lyricsPlaying) s.Lyric.X = (i * 7) % 300;
            var sw = Stopwatch.StartNew();
            Frame();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        alloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
        return new Result(frames, s.AppProbe.Replays, s.AppProbe.Pixels / (double)frames, s.Top.Pixels / (double)frames,
            times.Average(), times.Max(), alloc / 1024.0 / frames);
    }

    [AvaloniaFact]
    public void Harness_ScrollOverEachBackdrop()
    {
        if (!Harness || !HeadlessTestApp.RealRendering) return;
        foreach (var mode in new[] { Backdrop.Dim, Backdrop.LiveBlur, Backdrop.SnapshotGlass })
        foreach (var lyrics in new[] { false, true })
        {
            var s = Build(mode);
            try
            {
                ScrollFrames(s, 10, lyrics); // warm-up
                var r = ScrollFrames(s, 60, lyrics);
                var idle = ScrollFrames(s, 30, lyrics, scroll: false);
                _o.WriteLine($"{mode,-14} lyrics={lyrics,-5} {Line("scroll", r)} | {Line("idle", idle)}");
            }
            finally { s.Window.Close(); }
        }
    }

    internal sealed class PillScene
    {
        public required Window Window;
        public required PillDialogHost Host;
        public required ScrollViewer Scroll;
        public required ReplayProbe Top;
    }

    /// <summary>A pop-up window as the app opens one: PillDialogHost over a backdrop snapshot,
    /// its card holding 160 scrollable rows, and a probe over everything for the frame's dirty area.</summary>
    internal static PillScene BuildPill()
    {
        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(24) };
        for (var i = 0; i < 160; i++)
            rows.Children.Add(new Border
            {
                Height = 52, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = $"Row {i} — some descriptive text", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0) },
            });
        // A header and a button row around the list, as every pop-up has: the list scrolls a
        // part of the card, not all of it.
        var scroll = new ScrollViewer { Content = rows };
        Grid.SetRow(scroll, 1);
        var host = new PillDialogHost
        {
            Content = new Grid { Width = 900, Height = 680, RowDefinitions = RowDefinitions.Parse("80,*,80"), Children = { scroll } },
        };
        var top = new ReplayProbe();
        var window = new Window { Width = 1400, Height = 900, Background = Brushes.Transparent, Content = new Panel { Children = { host, top } } };
        window.Show();
        var bitmap = new WriteableBitmap(new PixelSize(350, 225), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            var px = new int[fb.RowBytes / 4 * 225];
            for (var y = 0; y < 225; y++) for (var x = 0; x < 350; x++) px[y * fb.RowBytes / 4 + x] = unchecked((int)0xFF000000) | (x * 0x10101 ^ y * 0x305);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
        }
        host.ShowBackdrop(bitmap);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && !(host.Card is { Opacity: > 0.999 } && host.CardGlass is { Fade: > 0.999 }))
        {
            Frame();
            Thread.Sleep(5);
        }
        for (var i = 0; i < 5; i++) Frame();
        return new PillScene { Window = window, Host = host, Scroll = scroll, Top = top };
    }

    /// <summary>Wheel notches: one 50 px step, then <paramref name="settle"/> frames with no input.
    /// Returns the dirty device pixels summed over all frames, per notch.</summary>
    internal static double DirtyPerNotch(PillScene s, int notches, int settle)
    {
        s.Top.Pixels = 0;
        s.Top.Replays = 0;
        var y = s.Scroll.Offset.Y;
        for (var n = 0; n < notches; n++)
        {
            y += 50;
            if (y > s.Scroll.Extent.Height - s.Scroll.Viewport.Height) y = 0;
            s.Scroll.Offset = new Vector(0, y);
            Frame();
            for (var i = 0; i < settle; i++) Frame();
        }
        return s.Top.Pixels / (double)notches;
    }

    [AvaloniaFact]
    public void Harness_PillDialogGlassCardScroll()
    {
        if (!Harness || !HeadlessTestApp.RealRendering) return;
        try
        {
            foreach (var glass in new[] { false, true })
            foreach (var gpu in new[] { false, true })
            {
                if (glass) AppGlass.Set(true, Colors.Black, Colors.Black); else AppGlass.Clear();
                GlassBackdropCache.UnreadableForTests = gpu;
                var s = BuildPill();
                try
                {
                    Assert.Equal(glass, s.Host.IsGlassCard);
                    DirtyPerNotch(s, 5, 3);
                    var perNotch = DirtyPerNotch(s, 30, 3);
                    var frames = s.Top.Replays;
                    _o.WriteLine($"pill glass={glass,-5} gpu={gpu,-5} wheel notch: {perNotch / 1e6:0.00} Mpx repainted per notch, {frames} frames drawn for 30 notches");
                }
                finally { s.Window.Close(); }
            }
        }
        finally
        {
            AppGlass.Clear();
            GlassBackdropCache.UnreadableForTests = false;
        }
    }

    private sealed class NoOpPlayHistory : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public System.Threading.Tasks.Task PreloadAsync() => System.Threading.Tasks.Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Settings scrim modes: the live blur (Liquid Glass off), without and with the hold
    /// while scrolling, and the opaque blurred snapshot (Liquid Glass on), the latter with and
    /// without the frost told so.</summary>
    internal enum SheetMode { LiveBlur, LiveBlurHeld, SnapshotFrost, SnapshotFrostBeneathWhole }

    /// <summary>The real Settings sheet in the real MainWindow (Appearance tab) over the app with
    /// something animating in it (the empty library's spinner, as lyrics or a playing title would),
    /// scrolled by wheel notches: device pixels repainted per notch and per idle frame.</summary>
    [AvaloniaFact]
    public void Harness_RealSettingsSheetScroll()
    {
        if (!Harness || !HeadlessTestApp.RealRendering) return;
        AddToPlaylistDialogTests.EnsureAppStyles();
        try
        {
            foreach (var mode in Enum.GetValues<SheetMode>())
            foreach (var gpu in new[] { false, true })
            {
                if (gpu && mode is SheetMode.LiveBlur or SheetMode.LiveBlurHeld) continue; // no frost: GPU changes nothing
                var r = MeasureSettingsSheet(mode, gpu, notches: 30);
                _o.WriteLine($"settings {mode,-26} gpu={gpu,-5}: {r.MpxPerNotch:0.00} Mpx per notch, {r.FramesPerNotch:0.0} frames per notch, " +
                             $"{r.MsPerNotch:0.0} ms per notch (4 frames), app layer drawn {PageDrawsPerNotch:0.0}x per notch | idle {r.IdleMpxPerFrame:0.00} Mpx per frame right after, " +
                             $"{r.IdleLaterMpxPerFrame:0.00} a second later | first step frames ms: {FirstStepFrames}");
            }
        }
        finally
        {
            AppGlass.Clear();
            GlassBackdropCache.UnreadableForTests = false;
        }
    }

    /// <summary>Frame times (ms) of the first scroll step and the 7 frames after it, last run.</summary>
    internal static string FirstStepFrames = "";
    /// <summary>Times the app layer under the sheet was drawn per notch, last run.</summary>
    internal static double PageDrawsPerNotch;

    internal readonly record struct SheetResult(double MpxPerNotch, double FramesPerNotch, double MsPerNotch, double IdleMpxPerFrame, double IdleLaterMpxPerFrame);

    internal static SheetResult MeasureSettingsSheet(SheetMode mode, bool gpu, int notches)
    {
        var live = mode is SheetMode.LiveBlur or SheetMode.LiveBlurHeld;
        var glass = !live;
        if (glass) AppGlass.Set(true, Colors.Black, Colors.Black); else AppGlass.Clear();
        GlassBackdropCache.UnreadableForTests = gpu;
        var vm = new Noctis.ViewModels.SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistory());
        var w = new Noctis.Views.MainWindow { Width = 1400, Height = 900 };
        try
        {
            w.Show();
            var root = (Panel)w.Content!;
            var probe = new ReplayProbe { ZIndex = 1000 };
            root.Children.Add(probe);
            // Under the sheet: how often the app layer itself is drawn.
            var pageProbe = new ReplayProbe();
            w.FindControl<Panel>("AppContentLayer")!.Children.Add(pageProbe);
            w.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
            w.EnsureSettingsViewLoaded();
            var host = w.FindControl<ContentControl>("SettingsViewHost")!;
            host.DataContext = vm;
            vm.SelectedSettingsTab = Noctis.ViewModels.SettingsViewModel.TabAppearance;
            var card = w.FindControl<Border>("SettingsCard")!;
            var frost = w.FindControl<GlassPanel>("SettingsGlass")!;
            card.Opacity = 1;
            card.RenderTransform = null;
            frost.Fade = 1;
            frost.RenderTransform = null;
            // The scrim as the open shows it: the live blur with Liquid Glass off, the opaque
            // blurred snapshot with it on.
            var scrim = w.FindControl<BlurredBackdrop>("SettingsScrim")!;
            scrim.Target = w.FindControl<Panel>("AppContentLayer");
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 300) { Frame(); Thread.Sleep(5); }
            scrim.IsVisible = true;
            var prepare = scrim.PrepareAsync();
            t.Restart();
            while (t.ElapsedMilliseconds < 3000 && !(prepare.IsCompleted && (live || scrim.HasSnapshot))) { Frame(); Thread.Sleep(5); }
            scrim.Opacity = 1;
            Assert.Equal(live, scrim.IsLive);
            Assert.Equal(!live, scrim.HasSnapshot);
            // MainWindow wires this once it has its view model, which this window has not.
            if (mode == SheetMode.LiveBlurHeld) scrim.HoldWhileScrolling(w.FindControl<Border>("SettingsOverlay")!);
            // Without the fix the frost never knew the app was hidden.
            if (mode == SheetMode.SnapshotFrost)
                frost.SetValue(GlassPanel.BeneathRepaintsWholeProperty, false, Avalonia.Data.BindingPriority.Animation);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500) { Frame(); Thread.Sleep(5); }
            Assert.Equal(glass, frost.EffectiveGlassActive);
            Assert.Equal(mode == SheetMode.SnapshotFrostBeneathWhole, frost.BeneathRepaintsWhole);
            var scroll = ((Control)host.Content!).FindControl<ScrollViewer>("SettingsScrollViewer")!;
            var y = 0.0;
            void Notches(int count)
            {
                for (var n = 0; n < count; n++)
                {
                    y += 50;
                    if (y > scroll.Extent.Height - scroll.Viewport.Height) y = 0;
                    scroll.Offset = new Vector(0, y);
                    Frame();
                    for (var i = 0; i < 3; i++) Frame();
                }
            }
            // The first step: what the swap to the held frame costs, frame by frame.
            var first = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                if (i == 0) { y += 50; scroll.Offset = new Vector(0, y); }
                var f = Stopwatch.StartNew();
                Frame();
                first.Add($"{f.Elapsed.TotalMilliseconds:0}");
            }
            FirstStepFrames = string.Join(" ", first);
            Notches(5);
            probe.Pixels = 0;
            probe.Replays = 0;
            pageProbe.Replays = 0;
            var timer = Stopwatch.StartNew();
            Notches(notches);
            PageDrawsPerNotch = pageProbe.Replays / (double)notches;
            var ms = timer.Elapsed.TotalMilliseconds / notches;
            var perNotch = probe.Pixels / 1e6 / notches;
            var frames = probe.Replays / (double)notches;
            if (mode == SheetMode.LiveBlurHeld) Assert.True(scrim.IsHeld, "not held after scrolling");
            probe.Pixels = 0;
            for (var i = 0; i < 20; i++) Frame();
            var idle = probe.Pixels / 1e6 / 20;
            sw.Restart();
            while (sw.ElapsedMilliseconds < 1000) { Frame(); Thread.Sleep(5); }
            Assert.False(scrim.IsHeld);
            probe.Pixels = 0;
            for (var i = 0; i < 20; i++) Frame();
            return new SheetResult(perNotch, frames, ms, idle, probe.Pixels / 1e6 / 20);
        }
        finally { w.Close(); }
    }

    internal static string Line(string what, Result r) =>
        $"{what}: page {r.Replays}/{r.Frames} {r.PagePixelsPerFrame / 1e6:0.00} Mpx, dirty {r.DirtyPixelsPerFrame / 1e6:0.00} Mpx/frame, " +
        $"{r.MeanMs:0.0} ms mean {r.MaxMs:0.0} max, {r.AllocKbPerFrame:0} KB/frame";
}
