using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The volume pop-up's drop shadow and its compact geometry (owner 10-08: "make the volume
/// slider smaller and fix the shadow behind the rounded pill"). The pop-up is overlay-hosted
/// and the glass pill was its whole content, so the overlay popup host — which clips to its
/// bounds — cut the shadow at the pill's bounding box: no shadow past the pill's ends or top,
/// and shadow only in the four box corners outside the round caps, ending on straight edges.
/// </summary>
public class VolumePillShadowTests
{
    private static void EnsureIcons()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SpeakerHighIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
    }

    private sealed record Rig(Window Window, PlaybackBarView Bar, PlayerViewModel Vm)
    {
        public Button Button => Bar.FindControl<Button>("VolumeButton")!;
        public Popup Flyout => Bar.FindControl<Popup>("VolumeFlyout")!;
        public GlassPanel Content => Bar.FindControl<GlassPanel>("VolumeFlyoutContent")!;
    }

    private static Rig Open(int volume = 62, Color? background = null)
    {
        EnsureIcons();
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.Volume = volume;
        var bar = new PlaybackBarView { DataContext = vm };
        var window = new Window { Width = 900, Height = 300, Content = bar };
        if (background is { } bg) window.Background = new SolidColorBrush(bg);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var rig = new Rig(window, bar, vm);
        // Pointer over the speaker button so the posted hover check never arms the close.
        var at = rig.Button.TranslatePoint(new Point(rig.Button.Bounds.Width / 2, rig.Button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Dispatcher.UIThread.RunJobs();
        rig.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Flyout.IsOpen);
        return rig;
    }

    /// <summary>The pill's shadow, in the pill's own coordinates.</summary>
    private static Rect ShadowBounds(GlassPanel content) =>
        content.BoxShadow.TransformBounds(new Rect(content.Bounds.Size));

    [AvaloniaFact]
    public void Shadow_HasRoomInsideEveryClippingAncestor()
    {
        var rig = Open();
        var content = rig.Content;
        var shadow = ShadowBounds(content);
        Assert.True(shadow.Width > content.Bounds.Width, "the pill casts a shadow");

        var clippers = new List<string>();
        var layoutOffset = content.Bounds.Position; // the shown pose: no render transform
        for (var v = content.GetVisualParent(); v != null && v is not TopLevel; v = v.GetVisualParent())
        {
            var room = new Rect(v.Bounds.Size).Inflate(0.5); // half a pixel for layout rounding
            if (v.ClipToBounds)
            {
                var shown = shadow.Translate(layoutOffset);
                if (!room.Contains(shown))
                    clippers.Add($"shown: {v.GetType().Name} {room} cuts shadow {shown}");
                // Whatever pose the reveal transform holds right now (the hidden pose drops 6px).
                var tl = content.TranslatePoint(shadow.TopLeft, v)!.Value;
                var br = content.TranslatePoint(shadow.BottomRight, v)!.Value;
                var now = new Rect(tl, br);
                if (!room.Contains(now))
                    clippers.Add($"transformed: {v.GetType().Name} {room} cuts shadow {now}");
            }
            layoutOffset += v.Bounds.Position;
        }
        Assert.True(clippers.Count == 0, string.Join("; ", clippers));
        rig.Window.Close();
    }

    /// <summary>The shadow room must not move the pill: it still rests 6px above the speaker
    /// button, centred on it.</summary>
    [AvaloniaFact]
    public void Pill_StillRestsJustAboveTheSpeakerButton()
    {
        var rig = Open();
        var button = rig.Button;
        var content = rig.Content;
        var buttonTop = button.TranslatePoint(new Point(0, 0), rig.Window)!.Value;
        // Layout position only (the reveal transform may still be mid-flight in a stub run).
        var host = content.GetVisualParent()!;
        var hostAt = host.TranslatePoint(new Point(0, 0), rig.Window)!.Value;
        var pillTop = hostAt + content.Bounds.Position;
        var pillBottom = pillTop.Y + content.Bounds.Height;
        var gap = buttonTop.Y - pillBottom;
        Assert.True(Math.Abs(gap - 6) <= 1,
            $"pill bottom {pillBottom}, button top {buttonTop.Y} (gap {gap}); host {host.GetType().Name} at {hostAt} {host.Bounds.Size}, pill {content.Bounds}");
        var pillMid = pillTop.X + content.Bounds.Width / 2;
        var buttonMid = buttonTop.X + button.Bounds.Width / 2;
        Assert.True(Math.Abs(pillMid - buttonMid) <= 1, $"pill centre {pillMid}, button centre {buttonMid}");
        rig.Window.Close();
    }

    /// <summary>The pop-up grew around the pill to hold the shadow; the margin must not catch
    /// clicks meant for the speaker button below the pill (the click that mutes).</summary>
    [AvaloniaFact]
    public void ShadowRoom_DoesNotCoverTheSpeakerButton()
    {
        var rig = Open();
        var button = rig.Button;
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, 3), rig.Window)!.Value;
        var hit = rig.Window.InputHitTest(center) as Visual;
        Assert.NotNull(hit);
        Assert.True(ReferenceEquals(hit, button) || hit!.GetVisualAncestors().Contains(button),
            $"hit {hit!.GetType().Name}, not the speaker button");
        rig.Window.Close();
    }

    /// <summary>Compact geometry (10-08): the track, thumb and fill share one centre line,
    /// the thumb travels the whole slider, and the slider spans the pill's full height so the
    /// thinner track keeps a full-height hit strip.</summary>
    [AvaloniaFact]
    public void CompactPill_GeometryStaysConsistent()
    {
        var rig = Open(volume: 100);
        var bar = rig.Bar;
        var pill = bar.FindControl<Grid>("VolumePill")!;
        var slider = bar.FindControl<Slider>("VolumeSlider")!;
        var track = bar.FindControl<Border>("VolumeTrackBackground")!;
        var fill = bar.FindControl<Border>("VolumeTrackFill")!;
        var thumb = bar.FindControl<Panel>("VolumeThumb")!;
        var dot = bar.FindControl<Border>("VolumeThumbDot")!;

        Assert.True(pill.Bounds.Height <= 30, $"pill {pill.Bounds.Height}px tall");
        Assert.True(slider.Bounds.Width <= 100, $"track {slider.Bounds.Width}px long");
        Assert.True(track.Height <= 6 && thumb.Width <= 14, "thinner track, smaller thumb");
        Assert.Equal(pill.Bounds.Height, slider.Bounds.Height);
        Assert.Equal(thumb.Width, dot.Width);

        var mid = pill.Bounds.Height / 2;
        Assert.Equal(mid, Canvas.GetTop(track) + track.Height / 2);
        Assert.Equal(mid, Canvas.GetTop(fill) + fill.Height / 2);
        Assert.Equal(mid, Canvas.GetTop(thumb) + thumb.Height / 2);

        // Full volume: the thumb's right edge meets the slider's, and the rest track starts
        // under the thumb's centre (code-behind's VolumeThumbSize matches the XAML thumb).
        var x = ((TranslateTransform)thumb.RenderTransform!).X;
        Assert.Equal(slider.Bounds.Width, x + thumb.Width, 3);
        Assert.Equal(thumb.Width / 2, Canvas.GetLeft(track), 3);
        rig.Window.Close();
    }

    /// <summary>Channel mean: the same for Rgba8888 and Bgra8888 frames.</summary>
    private static double Luma(Color c) => (c.R + c.G + c.B) / 3.0;

    private static byte[] Pixels(Bitmap bitmap)
    {
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var px = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { handle.Free(); }
        return px;
    }

    /// <summary>Four bytes a pixel; the channel order does not matter to <see cref="Luma"/>.</summary>
    private static Color PixelAt(byte[] px, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return Color.FromArgb(px[i + 3], px[i + 2], px[i + 1], px[i]);
    }

    /// <summary>Real Skia (NOCTIS_TEST_SKIA=1): beyond the pill's round ends and above it the
    /// shadow falls off softly into the window colour — before the fix those pixels were the
    /// bare background, cut at the pill's bounding box.</summary>
    [AvaloniaFact]
    public void Shadow_FallsOffSoftlyPastThePillsBoundingBox()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            // The island's brushes (IslandBackground, IslandSliderFilled) live here.
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var bg = Color.Parse("#2A3550");
        var rig = Open(background: bg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 600)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        var frame = rig.Window.CaptureRenderedFrame()!;
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "volume-shots");
        Directory.CreateDirectory(dir);
        frame.Save(System.IO.Path.Combine(dir, "volume-pill-shadow.png"));

        var px = Pixels(frame);
        var w = frame.PixelSize.Width;

        var c = rig.Content;
        var tl = c.TranslatePoint(new Point(0, 0), rig.Window)!.Value;
        int left = (int)Math.Floor(tl.X), top = (int)Math.Floor(tl.Y);
        int right = (int)Math.Ceiling(tl.X + c.Bounds.Width), midY = (int)Math.Round(tl.Y + c.Bounds.Height / 2);
        int midX = (int)Math.Round(tl.X + c.Bounds.Width / 2);
        var bgL = Luma(bg);

        // Past each round end, and above the middle: darker than the window, easing back to it.
        foreach (var (name, x0, y0, dx, dy) in new[]
                 {
                     ("left end", left - 2, midY, -1, 0),
                     ("right end", right + 1, midY, 1, 0),
                     ("top", midX, top - 2, 0, -1),
                 })
        {
            var first = Luma(PixelAt(px, w, x0, y0));
            Assert.True(bgL - first >= 1, $"{name}: no shadow past the pill at ({x0},{y0}) (luma {first:F1} vs window {bgL:F1})");
            var prev = first;
            for (var i = 1; i < 10; i++)
            {
                var l = Luma(PixelAt(px, w, x0 + dx * i, y0 + dy * i));
                Assert.True(l >= prev - 0.6, $"{name}: shadow darkens again {i}px out");
                Assert.True(l - prev <= 4, $"{name}: hard step {l - prev:F1} at {i}px out");
                prev = l;
            }
        }
        rig.Window.Close();
    }
}
