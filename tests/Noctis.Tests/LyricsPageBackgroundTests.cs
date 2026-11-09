using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public sealed class LyricsPageBackgroundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-page-bg-" + Guid.NewGuid().ToString("N"));

    public LyricsPageBackgroundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static LyricsPageSpec Custom(string path, string? ffmpeg = null) =>
        LyricsPageTestData.Spec() with
        {
            BackgroundKind = LyricsPageBackgroundKind.Custom,
            BackgroundMediaPath = path,
            FfmpegPath = ffmpeg,
        };

    private static SKBitmap Render(IPageBackground background, int w, int h, double seconds = 0)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        background.Draw(canvas, seconds);
        canvas.Flush();
        return bitmap;
    }

    private string WritePng(string name, int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Cover_fit_crops_the_centre_to_fill()
    {
        Assert.Equal(new SKRect(656.25f, 0, 1263.75f, 1080), BackgroundFit.CoverSource(1920, 1080, 1080, 1920));
        Assert.Equal(new SKRect(0, 0, 1000, 1000), BackgroundFit.CoverSource(1000, 1000, 1080, 1080));
        Assert.Equal(new SKRect(0, 1500, 1080, 2340), BackgroundFit.CoverSource(1080, 3840, 1080, 840));
    }

    [Fact]
    public void Readability_strength_maps_to_the_page_scrim()
    {
        Assert.Equal(0, PageBackgroundTint.OverlayAlpha(0));
        Assert.Equal(152, PageBackgroundTint.OverlayAlpha(70));   // ≈ the page's #99 scrim
        Assert.Equal(217, PageBackgroundTint.OverlayAlpha(100));
        Assert.Equal(PageBackgroundTint.OverlayAlpha(100), PageBackgroundTint.OverlayAlpha(250));
        Assert.Equal(0, PageBackgroundTint.OverlayAlpha(-5));
        PageBackgroundTint.Tint(new SKColor(200, 40, 40)).ToHsl(out _, out var s, out var l);
        Assert.InRange(l, 11.5f, 12.5f); Assert.True(s <= 60.5f);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.Png", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.gif", false)]
    [InlineData("a", false)]
    public void Still_images_are_told_apart_from_clips_by_extension(string path, bool still) =>
        Assert.Equal(still, PageBackgroundTint.IsStillImage(path));

    [Fact]
    public void Missing_or_unreadable_custom_file_falls_back_to_the_wash()
    {
        using (var missing = PageBackgrounds.Create(Custom(Path.Combine(_dir, "nope.mp4"), "ffmpeg"), 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(missing);

        var textPng = Path.Combine(_dir, "text.png");
        File.WriteAllText(textPng, "this is not a picture");
        using (var unreadable = PageBackgrounds.Create(Custom(textPng), 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(unreadable);

        using (var noMedia = PageBackgrounds.Create(LyricsPageTestData.Spec() with
               { BackgroundKind = LyricsPageBackgroundKind.AnimatedCover }, 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(noMedia);
    }

    [Fact]
    public void Still_image_is_scaled_to_fill_and_tinted()
    {
        var png = WritePng("red.png", 200, 100, new SKColor(255, 0, 0));
        using var background = PageBackgrounds.Create(Custom(png), 108, 192, 60, true, null);
        Assert.IsType<StillBackground>(background);

        using var frame = Render(background, 108, 192);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var p = frame.GetPixel(x, y);
                Assert.True(p.Red > p.Green + 40 && p.Red > p.Blue + 40, $"({x},{y}) = {p}");
            }

        // The tint sits over the picture at the readability overlay: 70 % → alpha 152 → red ≈ 255 × (1 − 152/255).
        var centre = frame.GetPixel(54, 96);
        var expected = 255 * (1 - PageBackgroundTint.OverlayAlpha(70) / 255.0);
        Assert.InRange(centre.Red, expected - 2, expected + 30);
    }

    [Fact]
    public void Artwork_wash_is_the_blurred_cover_under_the_scrim()
    {
        using var art = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        art.Erase(new SKColor(0, 200, 0));
        using var wash = new WashBackground(art, 108, 192, 70);
        using var frame = Render(wash, 108, 192);
        var c = frame.GetPixel(54, 96);
        var expected = 200 * (1 - PageBackgroundTint.OverlayAlpha(70) / 255.0);
        Assert.InRange(c.Green, expected - 3, expected + 3);
        Assert.True(c.Red < 5 && c.Blue < 5, c.ToString());

        // No artwork: the vibrant fallback #1A1A2E under the same scrim.
        using var plain = new WashBackground(null, 108, 192, 0);
        using var plainFrame = Render(plain, 108, 192);
        Assert.Equal(new SKColor(0x1A, 0x1A, 0x2E), plainFrame.GetPixel(54, 96));
    }

    /// <summary>A JPEG of a <paramref name="w"/>×<paramref name="h"/> picture, red on the left half
    /// and blue on the right, with an EXIF APP1 segment carrying <paramref name="orientation"/>.</summary>
    private string WriteExifJpeg(string name, int w, int h, ushort orientation)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            paint.Color = new SKColor(255, 0, 0);
            canvas.DrawRect(0, 0, w / 2f, h, paint);
            paint.Color = new SKColor(0, 0, 255);
            canvas.DrawRect(w / 2f, 0, w / 2f, h, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = data.ToArray();
        Assert.True(jpeg[0] == 0xFF && jpeg[1] == 0xD8);

        // APP1 "Exif\0\0", little-endian TIFF header, one IFD entry: 0x0112 Orientation, SHORT, 1.
        var exif = new List<byte> { 0xFF, 0xE1, 0x00, 0x22 };
        exif.AddRange("Exif\0\0"u8.ToArray());
        exif.AddRange(new byte[] { (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 });
        exif.AddRange(new byte[] { 0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 });
        exif.AddRange(new byte[] { (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00 });
        exif.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        Assert.Equal(0x22 + 2, exif.Count);

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, jpeg.Take(2).Concat(exif).Concat(jpeg.Skip(2)).ToArray());
        return path;
    }

    [Fact]
    public void A_photo_turned_by_its_exif_orientation_shows_upright()
    {
        // Orientation 6: stored on its side, shown turned 90° clockwise — the stored left
        // (red) half becomes the top.
        var path = WriteExifJpeg("turned.jpg", 40, 20, 6);
        using (var codec = SKCodec.Create(path))
            Assert.Equal(SKEncodedOrigin.RightTop, codec.EncodedOrigin);

        using var background = PageBackgrounds.Create(Custom(path) with { BackgroundStrength = 0 }, 20, 40, 60, true, null);
        Assert.IsType<StillBackground>(background);
        using var frame = Render(background, 20, 40);
        Assert.True(IsRed(frame.GetPixel(4, 5)) && IsRed(frame.GetPixel(15, 5)), $"top {frame.GetPixel(4, 5)} {frame.GetPixel(15, 5)}");
        Assert.True(IsBlue(frame.GetPixel(4, 34)) && IsBlue(frame.GetPixel(15, 34)), $"bottom {frame.GetPixel(4, 34)} {frame.GetPixel(15, 34)}");
    }

    /// <summary>Every EXIF orientation: where the stored top-left pixel (red) and its right
    /// neighbour (green) of a 3×2 picture end up.</summary>
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0, 0, 1, 0)]
    [InlineData(SKEncodedOrigin.TopRight, 2, 0, 1, 0)]
    [InlineData(SKEncodedOrigin.BottomRight, 2, 1, 1, 1)]
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 1, 1, 1)]
    [InlineData(SKEncodedOrigin.LeftTop, 0, 0, 0, 1)]
    [InlineData(SKEncodedOrigin.RightTop, 1, 0, 1, 1)]
    [InlineData(SKEncodedOrigin.RightBottom, 1, 2, 1, 1)]
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 2, 0, 1)]
    public void Each_exif_orientation_maps_the_pixels(SKEncodedOrigin origin, int redX, int redY, int greenX, int greenY)
    {
        using var source = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(SKColors.Black);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Lime);
        using var upright = PageBackgrounds.Orient(source, origin);
        bool turns = origin >= SKEncodedOrigin.LeftTop;
        Assert.Equal(turns ? 2 : 3, upright.Width);
        Assert.Equal(turns ? 3 : 2, upright.Height);
        Assert.Equal(SKColors.Red, upright.GetPixel(redX, redY));
        Assert.Equal(SKColors.Lime, upright.GetPixel(greenX, greenY));
    }

    [Fact]
    public void A_large_photo_decodes_at_a_reduced_size_that_still_fills()
    {
        var path = WriteExifJpeg("big.jpg", 4000, 3000, 1);
        using var image = PageBackgrounds.DecodeStill(path, 400, 400);
        Assert.NotNull(image);
        Assert.True(image!.Width < 4000, $"decoded at {image.Width}×{image.Height}");
        Assert.True(image.Width >= 400 && image.Height >= 400, $"decoded at {image.Width}×{image.Height}");
    }

    [Fact]
    public void Readability_strength_changes_without_a_rebuild()
    {
        var png = WritePng("red3.png", 20, 20, new SKColor(255, 0, 0));
        using var still = PageBackgrounds.Create(Custom(png) with { BackgroundStrength = 0 }, 20, 20, 60, true, null);
        using (var clear = Render(still, 20, 20))
            Assert.Equal(255, clear.GetPixel(10, 10).Red);
        still.Strength = 100;
        using (var dark = Render(still, 20, 20))
            Assert.InRange(dark.GetPixel(10, 10).Red, 30, 60);   // 255 × (1 − 217/255) under the tint

        using var wash = new WashBackground(null, 20, 20, 0);
        wash.Strength = 100;
        using var washed = Render(wash, 20, 20);
        Assert.True(washed.GetPixel(10, 10).Blue < 0x2E, washed.GetPixel(10, 10).ToString());
    }

    [Fact]
    public void A_still_webp_stays_a_still()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(20, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        var path = Path.Combine(_dir, "still.webp");
        File.WriteAllBytes(path, data.ToArray());
        using var background = PageBackgrounds.Create(Custom(path), 20, 20, 60, true, null);
        Assert.IsType<StillBackground>(background);
    }

    [Fact]
    public void An_animated_webp_plays_its_frames_in_a_loop()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var path = Path.Combine(_dir, "anim.webp");
        if (!RunFfmpeg(ffmpeg, "-f", "lavfi", "-i", "color=red:s=64x64:d=0.3:r=10",
                "-f", "lavfi", "-i", "color=blue:s=64x64:d=0.3:r=10",
                "-filter_complex", "concat=n=2:v=1", "-loop", "0", "-c:v", "libwebp_anim", path))
            return;   // an ffmpeg build without the libwebp encoder

        using var background = PageBackgrounds.Create(Custom(path) with { BackgroundStrength = 0 }, 64, 64, 60, true, null);
        Assert.IsType<AnimatedImageBackground>(background);
        foreach (var (t, red) in new[] { (0.05, true), (0.45, false), (0.65, true), (1.05, false), (0.1, true) })
        {
            using var frame = Render(background, 64, 64, t);
            var c = frame.GetPixel(32, 32);
            Assert.True(red ? IsRed(c) : IsBlue(c), $"t {t}: {c}");
        }
    }

    [Fact]
    public void Artwork_wash_kind_ignores_the_media_path()
    {
        var png = WritePng("red2.png", 20, 20, SKColors.Red);
        using var background = PageBackgrounds.Create(LyricsPageTestData.Spec() with
            { BackgroundKind = LyricsPageBackgroundKind.ArtworkWash, BackgroundMediaPath = png }, 108, 192, 60, true, null);
        Assert.IsType<WashBackground>(background);
    }

    // ---- Clip backgrounds (ffmpeg) ----

    private static string? Ffmpeg() =>
        new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();

    private static readonly SKColor FallbackColour = new(0x1A, 0x1A, 0x2E);

    /// <summary>Slack over the background's own timeout for a failing wait to return (a loaded CI box).</summary>
    private const int WaitMarginMs = 3000;

    private static WashBackground Fallback(int w, int h) => new(null, w, h, 0);

    /// <summary>6 frames at 10 fps, 64×64: 3 red then 3 blue (F6: lavfi sources at r=10, not the default 25).</summary>
    private string? WriteRedBlueClip(string ffmpeg)
    {
        var clip = Path.Combine(_dir, "clip.mp4");
        return RunFfmpeg(ffmpeg,
            "-f", "lavfi", "-i", "color=red:s=64x64:d=0.3:r=10",
            "-f", "lavfi", "-i", "color=blue:s=64x64:d=0.3:r=10",
            "-filter_complex", "concat=n=2:v=1", "-r", "10", "-pix_fmt", "yuv420p", clip) ? clip : null;
    }

    /// <summary>Runs ffmpeg to write the file named last in <paramref name="args"/>; true when it
    /// exited cleanly and wrote it. A hung run is killed and fails the test.</summary>
    private static bool RunFfmpeg(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(5000);
            Assert.Fail($"ffmpeg did not finish writing {args[^1]} within 30 s");
        }
        stderr.Wait(5000);
        return p.ExitCode == 0 && File.Exists(args[^1]);
    }

    private static SKColor Centre(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    private static bool IsRed(SKColor c) => c.Red > 150 && c.Green < 80 && c.Blue < 80;
    private static bool IsBlue(SKColor c) => c.Blue > 150 && c.Red < 80 && c.Green < 80;

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void AssertNoChildLeft(ClipBackground clip, int? pid)
    {
        Assert.True(clip.ProcessExited);
        if (pid is not { } id) return;
        try
        {
            using var p = Process.GetProcessById(id);
            Assert.True(p.HasExited || !p.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase),
                $"ffmpeg {id} still running");
        }
        catch (ArgumentException) { } // gone
        catch (InvalidOperationException) { } // exited between the lookup and the check
    }

    [Fact]
    public void Clip_args_loop_scale_to_fill_and_pipe_raw_bgra()
    {
        var a = ClipBackground.BuildArgs("bg.mp4", 1080, 1920, 60);
        Assert.Equal("-1", a[a.IndexOf("-stream_loop") + 1]);
        Assert.True(a.IndexOf("-stream_loop") < a.IndexOf("-i"));
        Assert.Equal("bg.mp4", a[a.IndexOf("-i") + 1]);
        Assert.Equal("scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,fps=60,format=bgra",
            a[a.IndexOf("-vf") + 1]);
        Assert.Contains("-an", a); Assert.Equal("rawvideo", a[a.IndexOf("-f") + 1]); Assert.Equal("-", a[^1]);
        Assert.Equal("bgra", a[a.IndexOf("-pix_fmt") + 1]);
    }

    [Fact]
    public void A_short_clip_loops_frame_exactly_in_blocking_mode()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var clipPath = WriteRedBlueClip(ffmpeg);
        Assert.NotNull(clipPath);

        using var clip = new ClipBackground(ffmpeg, clipPath!, 64, 64, 10, SKColors.Transparent, 0, blocking: true, Fallback(64, 64));
        Assert.True(IsRed(Centre(clip.FrameAt(0.0)!)));
        Assert.True(IsRed(Centre(clip.FrameAt(0.2)!)));
        Assert.True(IsBlue(Centre(clip.FrameAt(0.3)!)));
        Assert.True(IsBlue(Centre(clip.FrameAt(0.5)!)));
        Assert.True(IsRed(Centre(clip.FrameAt(0.6)!)));
        Assert.True(IsBlue(Centre(clip.FrameAt(1.5)!)));
        // Going back restarts the decode and still lands on the exact frame.
        Assert.True(IsBlue(Centre(clip.FrameAt(0.4)!)));
        Assert.False(clip.Failed);
    }

    [Fact]
    public void Preview_mode_never_waits_for_a_frame()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var clipPath = WriteRedBlueClip(ffmpeg);
        Assert.NotNull(clipPath);

        using var clip = new ClipBackground(ffmpeg, clipPath!, 64, 64, 10, SKColors.Transparent, 0, blocking: false, Fallback(64, 64));
        var sw = Stopwatch.StartNew();
        clip.FrameAt(0);
        Assert.True(sw.ElapsedMilliseconds < 50, $"FrameAt took {sw.ElapsedMilliseconds} ms");
        Assert.True(PumpUntil(() => clip.FrameAt(0) != null));
        Assert.True(IsRed(Centre(clip.FrameAt(0)!)));
        Assert.False(clip.Failed);
    }

    [Fact]
    public void Corrupt_clip_falls_back_to_the_wash_within_the_timeout()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var bad = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(bad, "this is not a video, just some text bytes");

        var clip = new ClipBackground(ffmpeg, bad, 64, 64, 10, SKColors.Transparent, 0, blocking: true, Fallback(64, 64));
        try
        {
            var pid = clip.ProcessId;
            var sw = Stopwatch.StartNew();
            using (var frame = Render(clip, 64, 64))
            {
                Assert.True(sw.ElapsedMilliseconds < ClipBackground.FirstFrameTimeoutMs + WaitMarginMs, $"{sw.ElapsedMilliseconds} ms");
                Assert.True(clip.Failed);
                Assert.Equal(FallbackColour, frame.GetPixel(32, 32));
            }
            clip.Dispose();
            AssertNoChildLeft(clip, pid);
        }
        finally { clip.Dispose(); }   // ffmpeg never outlives a failed assert
    }

    [Fact]
    public void Ffmpeg_dying_mid_export_ends_the_wait_and_falls_back()   // F5
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var clipPath = WriteRedBlueClip(ffmpeg);
        Assert.NotNull(clipPath);

        var clip = new ClipBackground(ffmpeg, clipPath!, 64, 64, 10, SKColors.Transparent, 0, blocking: true, Fallback(64, 64));
        try
        {
            Assert.NotNull(clip.FrameAt(0));
            var pid = clip.ProcessId;
            Assert.NotNull(pid);
            using (var child = Process.GetProcessById(pid!.Value))
            {
                child.Kill();
                child.WaitForExit(5000);
            }

            // Far enough ahead that no frame still sitting in the pipe can answer it.
            var sw = Stopwatch.StartNew();
            using (var frame = Render(clip, 64, 64, 30.0))
            {
                Assert.True(sw.ElapsedMilliseconds < ClipBackground.FirstFrameTimeoutMs + WaitMarginMs, $"{sw.ElapsedMilliseconds} ms");
                Assert.True(clip.Failed);
                Assert.Equal(FallbackColour, frame.GetPixel(32, 32));
            }
            // Failed is final: later frames keep the fallback.
            using (var later = Render(clip, 64, 64, 31.0))
                Assert.Equal(FallbackColour, later.GetPixel(32, 32));
            clip.Dispose();
            AssertNoChildLeft(clip, pid);
        }
        finally { clip.Dispose(); }
    }

    [Fact]
    public void A_stalled_ffmpeg_times_out_to_the_wash()   // F5: a source that never delivers a byte
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var clip = new ClipBackground(ffmpeg, $"tcp://127.0.0.1:{port}", 64, 64, 10, SKColors.Transparent, 0,
                blocking: true, Fallback(64, 64));
            var pid = clip.ProcessId;
            var sw = Stopwatch.StartNew();
            using (var frame = Render(clip, 64, 64))
            {
                Assert.True(sw.ElapsedMilliseconds < ClipBackground.FirstFrameTimeoutMs + WaitMarginMs, $"{sw.ElapsedMilliseconds} ms");
                Assert.True(clip.Failed);
                Assert.Equal(FallbackColour, frame.GetPixel(32, 32));
            }
            clip.Dispose();
            AssertNoChildLeft(clip, pid);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Dispose_ends_a_blocked_wait()   // F5
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var clip = new ClipBackground(ffmpeg, $"tcp://127.0.0.1:{port}", 64, 64, 10, SKColors.Transparent, 0,
                blocking: true, Fallback(64, 64));
            var pid = clip.ProcessId;
            SKImage? result = null;
            bool returned = false;
            var waiter = new Thread(() => { result = clip.FrameAt(0); returned = true; }) { IsBackground = true };
            waiter.Start();
            Thread.Sleep(300);
            Assert.True(waiter.IsAlive, "FrameAt should still be waiting on the stalled source");
            clip.Dispose();
            Assert.True(waiter.Join(2000), "FrameAt still blocked after Dispose");
            Assert.True(returned);
            Assert.Null(result);
            AssertNoChildLeft(clip, pid);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void A_custom_video_with_ffmpeg_becomes_a_clip_background()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var clipPath = WriteRedBlueClip(ffmpeg);
        Assert.NotNull(clipPath);
        using (var withFfmpeg = PageBackgrounds.Create(Custom(clipPath!, ffmpeg), 64, 64, 10, true, null))
            Assert.IsType<ClipBackground>(withFfmpeg);
        using (var without = PageBackgrounds.Create(Custom(clipPath!), 64, 64, 10, true, null))
            Assert.IsType<WashBackground>(without);
    }

    [Fact]
    public void Page_background_settings_default_and_round_trip()
    {
        var s = new AppSettings();
        Assert.Equal("", s.ShareLyricsPageBackgroundPath);
        Assert.Equal(PageBackgroundTint.DefaultStrength, s.ShareLyricsPageBackgroundStrength);
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(
            new AppSettings { ShareLyricsPageBackgroundPath = "x.mp4", ShareLyricsPageBackgroundStrength = 40 }))!;
        Assert.Equal("x.mp4", back.ShareLyricsPageBackgroundPath);
        Assert.Equal(40, back.ShareLyricsPageBackgroundStrength);
    }
}
